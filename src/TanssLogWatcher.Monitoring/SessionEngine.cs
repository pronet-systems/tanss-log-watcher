using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.Monitoring.Model;

namespace TanssLogWatcher.Monitoring;

/// <summary>Stellschrauben der Zustandsmaschine.</summary>
public sealed record SessionEngineOptions
{
    /// <summary>
    /// Der Abstand zwischen zwei Durchläufen. Zugleich die Schwelle, bis zu der die echte
    /// Startzeit eines Prozesses als Sitzungsbeginn gilt.
    /// </summary>
    public TimeSpan SampleInterval { get; init; } = SessionConstants.DefaultSampleInterval;

    /// <summary>Karenz für Profile, die das Ziel aus Netzverbindungen lesen.</summary>
    public TimeSpan IpGracePeriod { get; init; } = SessionConstants.DefaultIpGracePeriod;

    /// <summary>
    /// Der Profilkatalog. <c>null</c> nimmt <see cref="MonitoringProfiles.ByKey"/>.
    /// </summary>
    /// <remarks>
    /// Austauschbar, damit die Zustandsmaschine mit erfundenen Profilen geprüft werden kann — etwa
    /// mit zwei Profilen, die auf denselben Prozessnamen passen. Im Betrieb gibt es genau einen
    /// Katalog.
    /// </remarks>
    public IReadOnlyDictionary<string, MonitoringProfile>? Catalog { get; init; }
}

/// <summary>Was ein Durchlauf verändert hat.</summary>
/// <param name="Started">Neu begonnene Sitzungen.</param>
/// <param name="Ended">Beendete Sitzungen, jeweils mit gesetztem Ende.</param>
public sealed record SessionRunResult(
    IReadOnlyList<WatchedSession> Started,
    IReadOnlyList<WatchedSession> Ended)
{
    /// <summary>Ein Durchlauf ohne Veränderung.</summary>
    public static SessionRunResult Empty { get; } = new([], []);
}

/// <summary>
/// Die Zustandsmaschine: sie vergleicht das gerade Beobachtete mit dem bisher Bekannten und sagt,
/// welche Sitzungen begonnen und welche geendet haben.
/// </summary>
/// <remarks>
/// <para><b>Eine Sitzung ist eindeutig über (Prozesskennung, Bezeichner).</b> Das Fensterhandle
/// dient allein der Zuordnung bei Umbenennungen.</para>
/// <para><b>Fehler werden je Prozess eingegrenzt.</b> Ein Prozess, der mitten im Durchlauf stirbt,
/// darf den Durchlauf nicht abbrechen — ein Abbruch verlöre stumm alle übrigen
/// Sitzungswechsel.</para>
/// <para><b>Datenschutz:</b> Fenstertitel und Ziele erscheinen in keinem Protokolleintrag. Sie
/// tragen Kundennamen, Rechnernamen und E-Mail-Betreffe.</para>
/// </remarks>
public sealed class SessionEngine
{
    private readonly object gate = new();
    private readonly List<WatchedSession> active = [];
    private readonly HashSet<string> reportedUnknownKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> reportedConfigErrors = new(StringComparer.Ordinal);

    private readonly IWindowSource windowSource;
    private readonly IProcessSource processSource;
    private readonly DestinationResolver resolver;
    private readonly TimeProvider clock;
    private readonly ILogger<SessionEngine> logger;
    private readonly SessionEngineOptions options;
    private readonly IReadOnlyDictionary<string, MonitoringProfile> catalog;

    /// <summary>
    /// Wann der vorige Durchlauf gemessen hat, oder <c>null</c> vor dem ersten.
    /// </summary>
    /// <remarks>
    /// Gebraucht von <see cref="DetermineStart"/>: Massgeblich ist der <b>tatsächliche</b>
    /// Abstand zweier Momentaufnahmen, nicht der eingestellte. Bei einem Takt von einer Sekunde
    /// liegen die beiden selten genau aufeinander — ein Durchlauf, der einmal 1,4 Sekunden
    /// braucht, verlöre sonst den echten Beginn jeder in dieser Zeitspanne gestarteten Sitzung
    /// und setzte sie auf „jetzt“.
    /// </remarks>
    private DateTimeOffset? lastRunAt;

    /// <summary>Die aktuell geltende Schwelle für „gerade gestartet“.</summary>
    private TimeSpan startTolerance = SessionConstants.DefaultSampleInterval;

    /// <summary>Erzeugt die Zustandsmaschine.</summary>
    /// <param name="windowSource">Quelle der sichtbaren Fenster.</param>
    /// <param name="processSource">Quelle der Prozessmomentaufnahme.</param>
    /// <param name="resolver">Der Zielauflöser.</param>
    /// <param name="clock">Zeitgeber; <c>null</c> nimmt die Systemuhr.</param>
    /// <param name="logger">Protokoll; <c>null</c> schaltet es ab.</param>
    /// <param name="options">Stellschrauben; <c>null</c> nimmt die Voreinstellungen.</param>
    public SessionEngine(IWindowSource windowSource, IProcessSource processSource,
                         DestinationResolver resolver, TimeProvider? clock = null,
                         ILogger<SessionEngine>? logger = null, SessionEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(windowSource);
        ArgumentNullException.ThrowIfNull(processSource);
        ArgumentNullException.ThrowIfNull(resolver);

        this.windowSource = windowSource;
        this.processSource = processSource;
        this.resolver = resolver;
        this.clock = clock ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<SessionEngine>.Instance;
        this.options = options ?? new SessionEngineOptions();
        catalog = this.options.Catalog ?? MonitoringProfiles.ByKey;
    }

    /// <summary>Die derzeit laufenden Sitzungen, als Abzug.</summary>
    public IReadOnlyList<WatchedSession> ActiveSessions
    {
        get
        {
            lock (gate)
            {
                return [.. active];
            }
        }
    }

    /// <summary>
    /// Führt einen Durchlauf aus.
    /// </summary>
    /// <param name="settings">
    /// Die Einstellungen des Technikers. Nur Einträge mit
    /// <see cref="MonitoringSetting.IsActive"/> werden beachtet.
    /// </param>
    public SessionRunResult Run(IEnumerable<MonitoringSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        List<MonitoringSetting> activeSettings = [];
        foreach (MonitoringSetting setting in settings)
        {
            if (setting.IsActive)
            {
                activeSettings.Add(setting);
            }
        }

        lock (gate)
        {
            return RunCore(activeSettings);
        }
    }

    /// <summary>
    /// Beendet alle laufenden Sitzungen, etwa beim Herunterfahren.
    /// </summary>
    /// <remarks>
    /// Ohne diesen Weg gingen laufende Sitzungen beim Beenden des Programms verloren — und das
    /// kostet bares Geld.
    /// </remarks>
    public IReadOnlyList<WatchedSession> EndAll()
    {
        lock (gate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            List<WatchedSession> ended = [.. active];
            foreach (WatchedSession session in ended)
            {
                session.EndedAt = now;
            }

            active.Clear();
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("{Count} laufende Sitzungen beim Beenden abgeschlossen.", ended.Count);
            }

            return ended;
        }
    }

    private SessionRunResult RunCore(List<MonitoringSetting> activeSettings)
    {
        DateTimeOffset now = clock.GetUtcNow();

        // Vor allem, was werfen koennte: Der Abstand gehoert zu DIESEM Durchlauf, auch wenn er
        // gleich abbricht - sonst waechst er ueber einen gescheiterten Durchlauf hinweg.
        TimeSpan sinceLast = lastRunAt is { } previous && previous <= now
            ? now - previous
            : TimeSpan.Zero;
        lastRunAt = now;

        // Der groessere der beiden: der eingestellte Takt als Untergrenze, der gemessene
        // Abstand, wenn der Durchlauf laenger gedauert hat.
        startTolerance = sinceLast > options.SampleInterval ? sinceLast : options.SampleInterval;

        // a) Fenster und Prozesse. Scheitert eine der beiden Quellen, bleibt der Bestand
        //    unveraendert: eine leere Fensterliste wuerde sonst jede laufende Sitzung beenden und
        //    einen Schwall falscher Fernwartungen erzeugen.
        IReadOnlyList<WindowInfo> windows;
        IReadOnlyList<ProcessInfo> processes;
        try
        {
            windows = windowSource.GetVisibleWindows();
            processes = processSource.Snapshot();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Durchlauf übersprungen: Fenster oder Prozesse nicht lesbar.");
            return SessionRunResult.Empty;
        }

        Dictionary<int, ProcessInfo> processById = [];
        Dictionary<int, List<int>> childrenByParent = [];
        foreach (ProcessInfo process in processes)
        {
            processById[process.Id] = process;
            if (process.ParentId != 0 && process.ParentId != process.Id)
            {
                if (!childrenByParent.TryGetValue(process.ParentId, out List<int>? children))
                {
                    children = [];
                    childrenByParent[process.ParentId] = children;
                }

                children.Add(process.Id);
            }
        }

        Dictionary<int, List<WindowInfo>> windowsByPid = [];
        List<int> windowPids = [];
        foreach (WindowInfo window in windows)
        {
            if (!windowsByPid.TryGetValue(window.ProcessId, out List<WindowInfo>? list))
            {
                list = [];
                windowsByPid[window.ProcessId] = list;
                windowPids.Add(window.ProcessId);
            }

            list.Add(window);
        }

        // b) Kandidaten. Jeder Prozess genau einmal, auch wenn zwei Profile auf ihn passen.
        List<Candidate> candidates = CollectCandidates(activeSettings, now, processes, processById, windowPids);

        // c) Ziele. Fehler bleiben auf den einzelnen Prozess beschraenkt.
        List<SessionCandidate> current = ResolveDestinations(candidates, windowsByPid, childrenByParent);

        // d) und e) Adoption, danach f) Abgleich und g) Flatterunterdrueckung.
        AdoptPlaceholders(current);
        AdoptChangedDestinations(current);

        (List<SessionCandidate> started, List<WatchedSession> ended) = Diff(current);
        SuppressFlutter(started, ended);

        foreach (WatchedSession session in ended)
        {
            session.EndedAt = now;
            active.Remove(session);
        }

        List<WatchedSession> startedSessions = new(started.Count);
        foreach (SessionCandidate candidate in started)
        {
            bool ignoreTitleChanges = FindProfile(candidate.ProfileKey)?.IgnoreTitleChanges ?? false;
            WatchedSession session = new(candidate, DetermineStart(candidate, now), ignoreTitleChanges);
            active.Add(session);
            startedSessions.Add(session);
        }

        if ((startedSessions.Count > 0 || ended.Count > 0) && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Durchlauf: {Started} begonnen, {Ended} beendet, {Active} laufend.",
                                  startedSessions.Count, ended.Count, active.Count);
        }

        return new SessionRunResult(startedSessions, ended);
    }

    private List<Candidate> CollectCandidates(
        List<MonitoringSetting> activeSettings, DateTimeOffset now,
        IReadOnlyList<ProcessInfo> processes, Dictionary<int, ProcessInfo> processById,
        List<int> windowPids)
    {
        List<Candidate> candidates = [];
        HashSet<int> taken = [];

        foreach (MonitoringSetting setting in activeSettings)
        {
            MonitoringProfile? profile = FindProfile(setting.Key);
            if (profile is null)
            {
                continue;
            }

            foreach (int pid in windowPids)
            {
                if (taken.Contains(pid) || !processById.TryGetValue(pid, out ProcessInfo process))
                {
                    continue;
                }

                if (!profile.MatchesProcessName(process.Name))
                {
                    continue;
                }

                if (profile.DestinationResolveMethod == DestinationResolveMethod.RemoteHostIpConnection &&
                    !GracePeriodElapsed(process, now))
                {
                    continue;
                }

                taken.Add(pid);
                candidates.Add(new Candidate(process, profile, setting));
            }
        }

        // NoGui: Anwendungen ohne eigenes Fenster. Die Karenz gilt hier bewusst nicht — ein
        // VPN-Dienst baut seine eine Verbindung sofort auf, und sie ist genau das gesuchte Ziel.
        foreach (MonitoringSetting setting in activeSettings)
        {
            MonitoringProfile? profile = FindProfile(setting.Key);
            if (profile is null || !profile.NoGui)
            {
                continue;
            }

            bool hasWindowedProcess = false;
            foreach (int pid in windowPids)
            {
                if (processById.TryGetValue(pid, out ProcessInfo windowed) &&
                    profile.MatchesProcessName(windowed.Name))
                {
                    hasWindowedProcess = true;
                    break;
                }
            }

            if (hasWindowedProcess)
            {
                continue;
            }

            foreach (ProcessInfo process in processes)
            {
                if (profile.MatchesProcessName(process.Name) && taken.Add(process.Id))
                {
                    candidates.Add(new Candidate(process, profile, setting));
                }
            }
        }

        return candidates;
    }

    private List<SessionCandidate> ResolveDestinations(
        List<Candidate> candidates, Dictionary<int, List<WindowInfo>> windowsByPid,
        Dictionary<int, List<int>> childrenByParent)
    {
        List<SessionCandidate> current = [];
        HashSet<(int, string)> seen = [];

        foreach (Candidate candidate in candidates)
        {
            try
            {
                List<WindowInfo> processWindows =
                    windowsByPid.TryGetValue(candidate.Process.Id, out List<WindowInfo>? list) ? list : [];
                List<int> children =
                    childrenByParent.TryGetValue(candidate.Process.Id, out List<int>? kids) ? kids : [];
                nint mainWindowHandle = MainWindowHandle(processWindows);

                foreach (ResolvedDestination destination in resolver.Resolve(
                             candidate.Profile, candidate.Setting, candidate.Process,
                             mainWindowHandle, processWindows, children))
                {
                    // Zwei Fenster mit gleichem Titel sind eine Sitzung, nicht zwei. Ohne diese
                    // Pruefung entstuende sie doppelt, und der Techniker wuerde zweimal nach
                    // demselben Kommentar gefragt.
                    if (seen.Add((candidate.Process.Id, destination.IdentityKey)))
                    {
                        current.Add(new SessionCandidate(
                            candidate.Process.Id, candidate.Process.Name, candidate.Profile.Key,
                            destination.Destination, destination.IdentityKey, destination.WindowHandle,
                            candidate.Process.StartedAt));
                    }
                }
            }
            catch (MonitoringConfigurationException ex)
            {
                // Ein Konfigurationsfehler bleibt bestehen, bis ihn jemand behebt. Er wird deshalb
                // je Profil einmal gemeldet statt in jedem Durchlauf erneut — und der Prozess
                // liefert bis dahin gar kein Ziel, denn ein erfundener Platzhalter wuerde im TANSS
                // des Kunden als inhaltsleere Fernwartung landen.
                if (reportedConfigErrors.Add(candidate.Profile.Key))
                {
                    logger.LogError(ex, "Profil {ProfileKey} ist falsch eingestellt und liefert " +
                                        "bis zur Korrektur keine Sitzungen.", candidate.Profile.Key);
                }
            }
            catch (Exception ex)
            {
                // Der haeufigste Fall: der Prozess ist zwischen Momentaufnahme und Auswertung
                // gestorben. Das kostet diesen einen Prozess, nicht den Durchlauf.
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning(ex, "Ziel für Prozess {ProcessId} ({ProfileKey}) nicht ermittelbar.",
                                      candidate.Process.Id, candidate.Profile.Key);
                }
            }
        }

        return current;
    }

    /// <summary>
    /// Das Hauptfenster eines Prozesses: sein erstes <b>besitzerloses</b> Fenster.
    /// </summary>
    /// <remarks>
    /// <para>Die Auswahl folgt der von <c>Process.MainWindowHandle</c>: sie überspringt
    /// besessene Fenster — Dialoge und frei schwebende Werkzeugfenster — und bleibt deshalb stabil,
    /// während sich die Z-Reihenfolge ständig ändert. Schlicht das oberste Fenster zu nehmen, machte
    /// jeden offenen Dialog zum vermeintlichen Hauptfenster und unterdrückte bei
    /// <see cref="MonitoringProfile.MonitorOnlyMainWindowHandle"/> genau das Fenster, auf das es
    /// ankommt.</para>
    /// <para>Mit einer bewussten Abweichung: hier zählen nur Fenster <b>mit Titel</b>, denn
    /// nur solche liefert <see cref="IWindowSource"/>. Ein Prozess, dessen besitzerloses Fenster
    /// titellos ist, hat damit kein Hauptfenster — und unterdrückt bei
    /// <see cref="MonitoringProfile.MonitorOnlyMainWindowHandle"/> alles, genau wie es ein nicht
    /// passendes Handle täte.</para>
    /// </remarks>
    private static nint MainWindowHandle(List<WindowInfo> processWindows)
    {
        foreach (WindowInfo window in processWindows)
        {
            if (!window.IsOwned)
            {
                return window.WindowHandle;
            }
        }

        return 0;
    }

    /// <summary>
    /// Eine angezeigte Sitzung mit Platzhalter übernimmt Ziel, Bezeichner <b>und Fensterhandle</b>
    /// der ersten aktuellen Sitzung desselben Prozesses. Der Beginn bleibt stehen — das ist der
    /// ganze Zweck des Platzhalters.
    /// </summary>
    private void AdoptPlaceholders(List<SessionCandidate> current)
    {
        foreach (WatchedSession session in active)
        {
            if (!session.HasNoDestination)
            {
                continue;
            }

            foreach (SessionCandidate candidate in current)
            {
                if (candidate.ProcessId == session.ProcessId)
                {
                    session.Destination = candidate.Destination;
                    session.IdentityKey = candidate.IdentityKey;

                    // Das Handle gehoert dazu: das uebernommene Ziel kann aus einem anderen Fenster
                    // desselben Prozesses stammen als dem, mit dem der Platzhalter entstand. Bliebe
                    // das alte Handle stehen, faende die handlebasierte Uebernahme diese Sitzung nie
                    // wieder und die naechste Umbenennung spaltete sie in zwei Fernwartungen.
                    session.WindowHandle = candidate.WindowHandle;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Zwei Übernahmen, die eine Sitzung <b>nicht</b> beenden: die Umbenennung eines Fensters bei
    /// <see cref="MonitoringProfile.IgnoreTitleChanges"/>, zugeordnet über das Fensterhandle, und
    /// der nachgereichte Rechnername bei den verbindungsbasierten Profilen, zugeordnet über die
    /// Adresse.
    /// </summary>
    private void AdoptChangedDestinations(List<SessionCandidate> current)
    {
        foreach (WatchedSession session in active)
        {
            if (session.IgnoreTitleChanges)
            {
                foreach (SessionCandidate candidate in current)
                {
                    if (candidate.ProcessId == session.ProcessId &&
                        candidate.WindowHandle == session.WindowHandle)
                    {
                        session.Destination = candidate.Destination;
                        session.IdentityKey = candidate.IdentityKey;
                        break;
                    }
                }
            }

            foreach (SessionCandidate candidate in current)
            {
                if (candidate.ProcessId == session.ProcessId &&
                    string.Equals(candidate.IdentityKey, session.IdentityKey, StringComparison.Ordinal) &&
                    !string.Equals(candidate.Destination, session.Destination, StringComparison.Ordinal))
                {
                    session.Destination = candidate.Destination;
                    break;
                }
            }
        }
    }

    private (List<SessionCandidate> Started, List<WatchedSession> Ended) Diff(List<SessionCandidate> current)
    {
        List<SessionCandidate> started = [];
        foreach (SessionCandidate candidate in current)
        {
            if (!active.Exists(session => Matches(session, candidate)))
            {
                started.Add(candidate);
            }
        }

        List<WatchedSession> ended = [];
        foreach (WatchedSession session in active)
        {
            if (!current.Exists(candidate => Matches(session, candidate)))
            {
                ended.Add(session);
            }
        }

        return (started, ended);
    }

    /// <summary>
    /// Eine angezeigte Sitzung und ein Kandidat gehören zusammen, wenn der Prozess derselbe ist
    /// und entweder der Bezeichner übereinstimmt oder die angezeigte noch Platzhalter ist. Der
    /// zweite Fall hält eine eben begonnene Sitzung am Leben, bis ihr erstes Fenster da ist.
    /// </summary>
    private static bool Matches(WatchedSession session, SessionCandidate candidate) =>
        session.ProcessId == candidate.ProcessId &&
        (string.Equals(session.IdentityKey, candidate.IdentityKey, StringComparison.Ordinal) ||
         session.HasNoDestination);

    /// <summary>
    /// Fällt im selben Durchlauf ein Ende mit einem neuen Platzhalter desselben Prozesses
    /// zusammen, werden beide verworfen. Ein Fenster, das kurz seinen Titel verliert, beendet
    /// damit keine Sitzung und erzeugt keine zweite.
    /// </summary>
    private static void SuppressFlutter(List<SessionCandidate> started, List<WatchedSession> ended)
    {
        for (int i = ended.Count - 1; i >= 0; i--)
        {
            int placeholderIndex = started.FindIndex(
                candidate => candidate.ProcessId == ended[i].ProcessId && candidate.HasNoDestination);
            if (placeholderIndex >= 0)
            {
                started.RemoveAt(placeholderIndex);
                ended.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Der Beginn einer Sitzung: die echte Startzeit nur für Prozesse, die seit der vorigen
    /// Momentaufnahme hinzugekommen sind. Alles Ältere — vor allem alles, was beim Start des
    /// Werkzeugs bereits lief — beginnt jetzt. Sonst schriebe das Werkzeug eine Fernwartung über
    /// die gesamte Laufzeit einer seit Tagen offenen Sitzung.
    /// </summary>
    /// <remarks>
    /// Die Schwelle ist der <b>gemessene</b> Abstand zum vorigen Durchlauf, mindestens aber der
    /// eingestellte Takt. Beides hat seinen Grund: Der eingestellte Takt allein wäre zu knapp,
    /// sobald ein Durchlauf einmal länger braucht als er selbst; der gemessene allein wäre beim
    /// allerersten Durchlauf null und verlöre jeden echten Beginn.
    /// </remarks>
    private DateTimeOffset DetermineStart(SessionCandidate candidate, DateTimeOffset now)
    {
        if (candidate.StartedAt is not { } startedAt)
        {
            return now;
        }

        return startedAt <= now && now - startedAt <= startTolerance ? startedAt : now;
    }

    /// <summary>
    /// Ein unbekannter Startzeitpunkt gilt als abgelaufen: ein erhöht laufender Prozess, dessen
    /// Zeiten verschlossen sind, darf nicht dauerhaft aus der Überwachung fallen.
    /// </summary>
    private bool GracePeriodElapsed(ProcessInfo process, DateTimeOffset now) =>
        process.StartedAt is not { } startedAt || startedAt + options.IpGracePeriod <= now;

    private MonitoringProfile? FindProfile(string key)
    {
        MonitoringProfile? profile = catalog.TryGetValue(key, out MonitoringProfile? found) ? found : null;
        if (profile is null && reportedUnknownKeys.Add(key))
        {
            logger.LogWarning("Einstellung für unbekanntes Profil {ProfileKey} wird übergangen.", key);
        }

        return profile;
    }

    private readonly record struct Candidate(
        ProcessInfo Process, MonitoringProfile Profile, MonitoringSetting Setting);
}
