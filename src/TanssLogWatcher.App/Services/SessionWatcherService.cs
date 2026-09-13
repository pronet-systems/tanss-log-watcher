using System.Globalization;
using System.Runtime.Versioning;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Monitoring;
using TanssLogWatcher.Monitoring.Model;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;

namespace TanssLogWatcher.App.Services;

/// <summary>
/// Beobachtet im Takt <c>watcher.poll_interval_seconds</c> und legt fertige Sitzungen in die
/// Warteschlange.
/// </summary>
/// <remarks>
/// <para><b>Dieser Dienst spricht nie mit TANSS.</b> Eine abgeschlossene Sitzung geht auf die
/// Platte und wird von dort gesendet — das ist Aufgabe des <see cref="UploadService"/>. So
/// kostet ein Netzfehler keine Sitzung, und genau deshalb läuft die Beobachtung im Zustand
/// <see cref="RuntimeState.Degraded"/> ungerührt weiter.</para>
///
/// <para><b>Der Trockenlauf ist der Standard, solange keine Zuordnung vorliegt.</b> Ein
/// Werkzeug, das ungefragt Fenstertitel von Kundenrechnern in ein Ticketsystem schreibt, wäre
/// schon beim ersten Start ein Datenschutzvorfall. Gebucht wird erst, wenn jemand ein Profil
/// ausdrücklich auf einen Fernwartungstyp abgebildet hat.</para>
///
/// <para><b>Laufende Sitzungen werden mit jedem Takt fortgeschrieben.</b> Nach einem Absturz
/// ist <see cref="OpenSession.StartedAt"/> sonst verloren — aus der Prozessliste ist der
/// Beginn rückwirkend nicht zu holen, und die ganze Sitzung wäre fort.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SessionWatcherService : PeriodicService
{
    private readonly List<SessionSnapshot> _active = [];
    private readonly object _activeGate = new();

    /// <summary>
    /// Wie lange eine frisch eingereihte Sitzung liegen bleibt, bevor der Sendedienst sie
    /// anfasst.
    /// </summary>
    /// <remarks>
    /// <para><b>Das Zeitfenster für den Abschlussdialog.</b> Ohne diese Frist kann der
    /// Sendedienst die Sitzung verschicken, während der Techniker noch am Bericht schreibt —
    /// gemessen und erlebt: Der Bericht war fertig und liess sich nicht mehr speichern, weil
    /// der Eintrag schon unterwegs war.</para>
    ///
    /// <para>Fünf Minuten, und zwar ohne Einstellmöglichkeit: Kürzer reicht für einen
    /// ordentlichen Bericht nicht, länger verzögert im Normalfall — Fenster zu, nichts
    /// getippt — jede Sitzung ohne Gegenwert. Wer früher fertig ist, gibt den Eintrag mit dem
    /// Speichern selbst frei (<see cref="Storage.Queue.IUploadQueue.Release"/>); wer den Dialog
    /// wegklickt, wartet die Frist ab und verliert nichts dabei.</para>
    ///
    /// <para>Die Frist gilt nur hier, nicht bei der Wiederherstellung nach einem Absturz: Jene
    /// Sitzungen sind längst beendet, und für sie geht kein Fenster mehr auf.</para>
    /// </remarks>
    public static readonly TimeSpan CommentGrace = TimeSpan.FromMinutes(5);

    /// <summary>Baut den Dienst.</summary>
    /// <param name="context">Der Zugang zu Zustand und Zusammenbau.</param>
    public SessionWatcherService(IRuntimeContext context) : base(context)
    {
    }

    /// <summary>Eine neu erkannte Sitzung.</summary>
    public event EventHandler<SessionSnapshot>? SessionStarted;

    /// <summary>Eine abgeschlossene Sitzung samt dem, was mit ihr geschehen ist.</summary>
    public event EventHandler<SessionClosed>? SessionEnded;

    /// <summary>Die Liste der laufenden Sitzungen hat sich geändert.</summary>
    /// <remarks>
    /// Ausdrücklich neben den beiden Einzelereignissen: Eine Ansicht, die nur eine Liste zeigt,
    /// soll nicht zwei Ereignisse zusammenrechnen müssen — und die Dauer läuft ohnehin weiter,
    /// ohne dass etwas beginnt oder endet.
    /// </remarks>
    public event EventHandler<IReadOnlyList<SessionSnapshot>>? ActiveSessionsChanged;

    /// <inheritdoc />
    public override string Name => "Sitzungsbeobachtung";

    /// <inheritdoc />
    public override string Description =>
        "Erkennt Fernwartungssitzungen an Fenstern, Prozessen und offenen Verbindungen und "
        + "legt jede abgeschlossene Sitzung in die Warteschlange.";

    /// <summary>Die laufenden Sitzungen, als Abschrift.</summary>
    public IReadOnlyList<SessionSnapshot> ActiveSessions
    {
        get
        {
            lock (_activeGate)
            {
                return [.. _active];
            }
        }
    }

    /// <summary>
    /// Läuft die Beobachtung im Trockenlauf?
    /// </summary>
    /// <remarks>
    /// Wahr, solange keine Beobachtungsregel auf einen Fernwartungstyp abgebildet ist. Die
    /// Anzeige muss das sagen: Sitzungen erscheinen dann in der Liste, gehen aber nirgendwohin,
    /// und ein Techniker, der das nicht weiss, wartet Wochen auf Buchungen, die nie kommen.
    /// </remarks>
    public bool IsDryRun { get; private set; }

    /// <summary>Der Grund für den Trockenlauf, oder <c>null</c>, wenn scharf gebucht wird.</summary>
    public string? DryRunReason { get; private set; }

    /// <inheritdoc />
    protected override TimeSpan Interval =>
        Context.Composition is { } composition
            ? TimeSpan.FromSeconds(composition.Config.Watcher.PollIntervalSeconds)
            : TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    protected override string NotConfiguredMessage =>
        "Ruht: Ohne Konfiguration ist nicht bekannt, welche Anwendungen zu beobachten sind und "
        + "auf welchen Fernwartungstyp sie buchen. Es wird nichts mitgelesen.";

    /// <inheritdoc />
    protected override string FaultMessage =>
        "Der letzte Beobachtungstakt ist abgebrochen. Laufende Sitzungen bleiben erfasst, der "
        + "nächste Takt kann gelingen. Hält es an, sind seit dem Fehler begonnene Sitzungen "
        + "womöglich nicht erkannt worden; sie sind dann von Hand in TANSS nachzutragen.";

    /// <summary>
    /// Schliesst Sitzungen ab, die ein früherer Lauf offen zurückgelassen hat.
    /// </summary>
    /// <remarks>
    /// Als Ende gilt <see cref="OpenSession.LastSeenAt"/> und nicht jetzt: Der letzte
    /// beobachtete Augenblick ist nach einem Absturz die einzige belastbare Aussage. Sie
    /// fortzusetzen wäre die Alternative — aber der Prozess von damals ist längst fort, und
    /// eine wiederbelebte Sitzung berechnete dem Kunden die Zeit, in der der Rechner aus war.
    /// </remarks>
    protected override Task PrimeAsync(RuntimeComposition composition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(composition);

        IReadOnlyList<OpenSession> open;
        try
        {
            open = composition.Queue.LoadOpenSessions();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Context.ReportDegraded(DegradedCause.StateDatabase,
                "Die Zustandsdatenbank liess sich nicht öffnen; offene Sitzungen aus einem "
                + "früheren Lauf sind damit weder zu lesen noch abzuschliessen. Üblichste "
                + "Ursache: ein zweiter Lauf des Werkzeugs hält die Datei, oder das Profil liegt "
                + "auf einem nicht erreichbaren Netzlaufwerk. Meldung: "
                + Redaction.Scrub(ex.Message),
                "Zweite Instanz beenden und die Anzeige neu laden. Solange das so bleibt, kann "
                + "keine Sitzung zwischengelagert werden — ein Netzfehler kostet dann die "
                + "Sitzung selbst.");

            return Task.CompletedTask;
        }

        foreach (OpenSession session in open)
        {
            RemoteSupportWrite payload = new()
            {
                TypeId = session.RemoteSupportTypeId,
                EmployeeId = composition.Config.Tanss.EmployeeId,
                StartTime = TanssTime.ToUnixSeconds(session.StartedAt),
                EndTime = TanssTime.ToUnixSeconds(session.LastSeenAt),
                RemoteMaintenanceId = session.RemoteMaintenanceId,
                Comment = session.Comment,
                DeviceName = session.DeviceName ?? string.Empty,
                UserName = session.UserName ?? string.Empty,
                TicketId = session.TicketId,
            };

            bool added;
            try
            {
                added = composition.Queue.Enqueue(payload);
                _ = composition.Queue.RemoveOpenSession(session.RemoteMaintenanceId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Hausregel 5: Diese eine Sitzung geht schief, die Wiederherstellung nicht.
                continue;
            }

            SessionSnapshot snapshot = new()
            {
                SessionId = session.RemoteMaintenanceId,
                ProfileKey = session.MonitorKey,
                ProfileName = DescribeProfile(session.MonitorKey),
                Destination = session.Target ?? session.DeviceName ?? string.Empty,
                ProcessId = session.ProcessId,
                StartedAt = session.StartedAt,
                EndedAt = session.LastSeenAt,
                RemoteSupportTypeId = session.RemoteSupportTypeId,
                Comment = session.Comment,
            };

            string reason = added
                ? "Aus einem früheren Lauf übernommen und eingereiht; als Ende gilt der letzte "
                  + "beobachtete Zeitpunkt und nicht jetzt."
                : "Aus einem früheren Lauf übernommen; die Kennung stand bereits in der "
                  + "Warteschlange und wurde nicht erneut aufgenommen.";

            Log(composition, "watch.recover", snapshot,
                added ? SessionOutcome.Deferred : SessionOutcome.Skipped, reason,
                SessionTrigger.Startup);

            Context.Notifier.Raise(SessionEnded, this, new SessionClosed(snapshot,
                added ? SessionDisposition.Enqueued : SessionDisposition.AlreadyQueued, reason));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task<string> RunCycleAsync(RuntimeComposition composition,
                                                  CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(composition);
        return Task.FromResult(RunOnce(composition, SessionTrigger.Watcher));
    }

    /// <summary>
    /// Das geordnete Ende: laufende Sitzungen abschliessen und einreihen, nicht verwerfen.
    /// </summary>
    /// <remarks>
    /// Der Mangel der Vorlage, der bares Geld kostete. Eine Fernwartung, die beim Schliessen des
    /// Fensters verschwindet, ist eine Stunde Arbeit, die niemand mehr belegen kann.
    /// </remarks>
    protected override Task ShutdownAsync(RuntimeComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);

        DateTimeOffset now = Context.Clock.GetLocalNow();
        IReadOnlyList<WatchedSession> ended = composition.Engine.EndAll();

        foreach (WatchedSession session in ended)
        {
            Finish(composition, session, now, SessionTrigger.Shutdown);
        }

        SetActive([]);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ein einzelner Beobachtungstakt.
    /// </summary>
    /// <remarks>
    /// Öffentlich, damit sich das Verhalten ohne Uhrläufe prüfen lässt: Ein Takt ist die
    /// Einheit, in der dieser Dienst arbeitet, und ein Test, der dafür sekundenlang warten
    /// müsste, würde nicht geschrieben.
    /// </remarks>
    /// <param name="composition">Die Bausteine.</param>
    /// <param name="trigger">Was diesen Takt ausgelöst hat.</param>
    /// <returns>Der Satz für die Anzeige.</returns>
    public string RunOnce(RuntimeComposition composition, SessionTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(composition);

        DateTimeOffset now = Context.Clock.GetLocalNow();
        UpdateDryRun(composition);

        SessionRunResult run = composition.Engine.Run(composition.Settings);

        foreach (WatchedSession session in run.Started)
        {
            SessionSnapshot snapshot = ToSnapshot(composition, session, now, ended: false);

            if (!IsDryRun)
            {
                // Fortgeschrieben mit jedem Takt: Nach einem Absturz ist StartedAt sonst
                // verloren - aus der Prozessliste ist der Beginn rueckwirkend nicht zu holen.
                Save(composition, session, now);
            }

            Log(composition, "watch", snapshot,
                IsDryRun ? SessionOutcome.DryRun : SessionOutcome.Ok,
                "Sitzung erkannt.", trigger);

            Context.Notifier.Raise(SessionStarted, this, snapshot);
        }

        // Laufende Sitzungen mitschreiben, damit LastSeenAt stimmt. Es ist nach einem Absturz
        // die einzige belastbare Aussage ueber das Ende - die aktuelle Uhrzeit zu nehmen hiesse,
        // dem Kunden die Zeit zu berechnen, in der der Rechner aus war.
        List<SessionSnapshot> active = [];
        foreach (WatchedSession session in composition.Engine.ActiveSessions)
        {
            if (!IsDryRun)
            {
                Save(composition, session, now);
            }

            active.Add(ToSnapshot(composition, session, now, ended: false));
        }

        SetActive(active);

        int enqueued = 0;
        foreach (WatchedSession session in run.Ended)
        {
            if (Finish(composition, session, now, trigger))
            {
                enqueued++;
            }
        }

        return string.Create(CultureInfo.CurrentCulture,
            $"{active.Count} laufend, {run.Started.Count} begonnen, {run.Ended.Count} beendet, "
            + $"{enqueued} eingereiht.")
            + (IsDryRun ? " Trockenlauf: es geht nichts nach TANSS." : string.Empty);
    }

    /// <summary>
    /// Ermittelt, ob scharf gebucht wird — und sagt, warum nicht.
    /// </summary>
    /// <remarks>
    /// Ohne Zuordnung ist der Trockenlauf nicht eine Vorsichtsmassnahme, sondern die einzig
    /// mögliche Betriebsart: Es gäbe keinen Fernwartungstyp, auf den zu buchen wäre.
    /// </remarks>
    private void UpdateDryRun(RuntimeComposition composition)
    {
        int active = composition.Settings.Count(setting => setting.IsActive);

        IsDryRun = active == 0;
        DryRunReason = IsDryRun
            ? "Keine Beobachtungsregel trägt eine Zuordnung auf einen Fernwartungstyp — es gäbe "
              + "nichts, worauf gebucht werden könnte. Sitzungen werden erkannt und angezeigt, "
              + "aber weder eingereiht noch nach TANSS geschrieben. Zuzuordnen ist die Kennung "
              + "in monitoring[].remote_support_type_id; sie steht in TANSS unter „Externe "
              + "Fernwartungs-Anbindungen verwalten“ und muss mindestens 1000 sein."
            : null;
    }

    /// <summary>
    /// Reiht eine abgeschlossene Sitzung ein — oder sagt, warum nicht.
    /// </summary>
    /// <returns><c>true</c>, wenn sie in die Warteschlange ging.</returns>
    private bool Finish(RuntimeComposition composition, WatchedSession session,
                        DateTimeOffset now, SessionTrigger trigger)
    {
        SessionSnapshot snapshot = ToSnapshot(composition, session, now, ended: true);
        MonitoringSetting? setting = FindSetting(composition, session.ProfileKey);

        if (setting is null || !setting.IsActive)
        {
            const string reason =
                "Keine Zuordnung auf einen Fernwartungstyp; es gäbe nichts, worauf gebucht "
                + "werden könnte. Die Sitzung ist erkannt, aber nirgends eingereiht. "
                + "Zuzuordnen ist die Kennung in monitoring[].remote_support_type_id.";

            Log(composition, "watch", snapshot, SessionOutcome.Skipped, reason, trigger);
            Context.Notifier.Raise(SessionEnded, this,
                new SessionClosed(snapshot, SessionDisposition.NotMapped, reason));
            return false;
        }

        if (IsDryRun)
        {
            string reason = DryRunReason
                ?? "Trockenlauf; es wurde nichts eingereiht und nichts nach TANSS geschrieben.";

            Log(composition, "watch", snapshot, SessionOutcome.DryRun, reason, trigger);
            Context.Notifier.Raise(SessionEnded, this,
                new SessionClosed(snapshot, SessionDisposition.DryRun, reason));
            return false;
        }

        try
        {
            bool added = composition.Queue.Enqueue(ToPayload(composition, snapshot, setting),
                                                   CommentGrace);
            _ = composition.Queue.RemoveOpenSession(snapshot.SessionId);

            string reason = added
                ? "Eingereiht; geht mit dem nächsten Sendelauf nach TANSS."
                : "Die Kennung stand bereits in der Warteschlange und wurde nicht erneut "
                  + "aufgenommen — so entsteht keine Dublette, die TANSS nicht abfangen würde.";

            Log(composition, "watch", snapshot,
                added ? SessionOutcome.Deferred : SessionOutcome.Skipped, reason, trigger);

            Context.Notifier.Raise(SessionEnded, this, new SessionClosed(snapshot,
                added ? SessionDisposition.Enqueued : SessionDisposition.AlreadyQueued, reason));

            return added;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Diese eine Sitzung geht schief, der Beobachtungslauf nicht.
            string reason =
                "Die Sitzung liess sich nicht einreihen; sie ist damit nicht gesichert und muss "
                + "in TANSS von Hand nachgetragen werden. Üblichste Ursache ist eine nicht "
                + "beschreibbare Zustandsdatenbank. Meldung: " + Redaction.Scrub(ex.Message);

            Log(composition, "watch", snapshot, SessionOutcome.Error, reason, trigger);
            Context.Notifier.Raise(SessionEnded, this,
                new SessionClosed(snapshot, SessionDisposition.Failed, reason));
            return false;
        }
    }

    /// <summary>Schreibt eine laufende Sitzung auf die Platte.</summary>
    private static void Save(RuntimeComposition composition, WatchedSession session,
                             DateTimeOffset now)
    {
        MonitoringSetting? setting = FindSetting(composition, session.ProfileKey);

        try
        {
            composition.Queue.SaveOpenSession(new OpenSession
            {
                RemoteMaintenanceId = session.SessionId.ToString(),
                MonitorKey = session.ProfileKey,
                RemoteSupportTypeId = setting?.RemoteSupportTypeId ?? MonitoringSetting.NotMonitored,
                StartedAt = session.StartedAt,
                LastSeenAt = now,
                ProcessId = session.ProcessId,
                Target = session.Destination,
                DeviceName = Shorten(session.Destination, 120),
                Comment = Describe(session),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Eine Sitzung, die sich gerade nicht sichern laesst, haelt den Takt
            // nicht an. Beim naechsten Takt wird es erneut versucht; gelingt es bis zum Ende
            // der Sitzung, ist nichts verloren.
        }
    }

    /// <summary>
    /// Baut die Nutzlast, die später unverändert an TANSS geht.
    /// </summary>
    /// <remarks>
    /// Sie entsteht genau einmal, beim Abschluss der Sitzung. Würde sie beim Senden
    /// nachgerechnet, hinge sie an Einstellungen, die sich seither geändert haben könnten —
    /// und die hochgeladene Fernwartung wäre nicht mehr die beobachtete.
    /// </remarks>
    private static RemoteSupportWrite ToPayload(RuntimeComposition composition,
                                                SessionSnapshot session,
                                                MonitoringSetting setting) => new()
    {
        TypeId = setting.RemoteSupportTypeId,
        EmployeeId = composition.Config.Tanss.EmployeeId,
        StartTime = TanssTime.ToUnixSeconds(session.StartedAt),
        EndTime = TanssTime.ToUnixSeconds(session.EndedAt ?? session.StartedAt),
        RemoteMaintenanceId = session.SessionId,
        Comment = session.Comment,
        DeviceName = Shorten(session.Destination, 120),
    };

    /// <summary>
    /// Die Abschrift einer Sitzung für die Oberfläche.
    /// </summary>
    /// <remarks>
    /// <paramref name="ended"/> steht ausdrücklich daneben und wird nicht aus
    /// <see cref="WatchedSession.EndedAt"/> geraten: Die Zustandsmaschine meldet eine Sitzung
    /// als beendet, ohne in jedem Fall einen Zeitpunkt zu setzen. Ohne diesen Ersatz stünde
    /// eine abgeschlossene Sitzung mit offenem Ende in der Anzeige und liefe dort ewig weiter.
    /// </remarks>
    /// <param name="composition">Die Bausteine.</param>
    /// <param name="session">Die Sitzung.</param>
    /// <param name="now">Der Bezugszeitpunkt.</param>
    /// <param name="ended">Ist die Sitzung abgeschlossen?</param>
    private static SessionSnapshot ToSnapshot(RuntimeComposition composition,
                                              WatchedSession session, DateTimeOffset now,
                                              bool ended) =>
        new()
        {
            SessionId = session.SessionId.ToString(),
            ProfileKey = session.ProfileKey,
            ProfileName = DescribeProfile(session.ProfileKey),
            Destination = session.Destination,
            ProcessId = session.ProcessId,
            StartedAt = session.StartedAt,
            EndedAt = ended ? session.EndedAt ?? now : null,
            RemoteSupportTypeId =
                FindSetting(composition, session.ProfileKey)?.RemoteSupportTypeId
                ?? MonitoringSetting.NotMonitored,
            Comment = Describe(session),
        };

    /// <summary>Der Kommentar, den TANSS an der Fernwartung zeigt.</summary>
    private static string Describe(WatchedSession session)
    {
        MonitoringProfile? profile = MonitoringProfiles.Find(session.ProfileKey);
        string what = profile?.TypeDescription ?? session.ProfileKey;
        return $"{what}: {session.Destination}";
    }

    /// <summary>Der sprechende Name eines Profils; ersatzweise sein Schlüssel.</summary>
    private static string DescribeProfile(string profileKey) =>
        MonitoringProfiles.Find(profileKey)?.TypeDescription ?? profileKey;

    private static MonitoringSetting? FindSetting(RuntimeComposition composition,
                                                  string profileKey) =>
        composition.Settings.FirstOrDefault(
            setting => string.Equals(setting.Key, profileKey, StringComparison.Ordinal));

    /// <summary>Kürzt einen Text auf eine Höchstlänge, mit Auslassungszeichen.</summary>
    private static string Shorten(string? text, int max) =>
        text is null || text.Length <= max ? text ?? string.Empty : text[..(max - 1)] + "…";

    /// <summary>
    /// Schreibt einen Protokolleintrag samt Fensterbeschriftung.
    /// </summary>
    /// <remarks>
    /// Die Beschriftung wandert ausdrücklich in <see cref="SessionLogEntry.WindowTitle"/> und
    /// nicht in den Begründungstext: Nur dieses Feld unterliegt
    /// <c>logging.redact_window_titles</c>. Im Begründungstext stünde sie im Klartext in
    /// <c>state.db</c>, und zwar unabhängig davon, was eingestellt ist.
    /// </remarks>
    private static void Log(RuntimeComposition composition, string operation,
                            SessionSnapshot session, SessionOutcome outcome, string reason,
                            SessionTrigger trigger)
    {
        try
        {
            _ = composition.Log.Append(new SessionLogEntry
            {
                Operation = operation,
                Outcome = outcome,
                Reason = reason,
                Trigger = trigger,
                RemoteMaintenanceId = session.SessionId,
                WindowTitle = session.Destination,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Ein nicht geschriebener Protokolleintrag darf keine Sitzung kosten.
            // Das Protokoll ist der Beleg, nicht der Vorgang.
        }
    }

    /// <summary>Tauscht die Liste der laufenden Sitzungen und meldet sie.</summary>
    /// <summary>
    /// Übernimmt den Stand und meldet ihn — aber nur, wenn er ein anderer ist.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Vergleich ist die Voraussetzung für den Sekundentakt.</b> Die Ansicht
    /// ersetzt ihre Liste bei jeder Meldung vollständig; eine Meldung je Takt hiesse also, die
    /// Zeilen jede Sekunde neu aufzubauen — sichtbar als Flackern, und die Auswahl wäre nach
    /// jedem Takt weg. Bei zehn Sekunden fiel das kaum auf, bei einer Sekunde sofort.</para>
    /// <para>Der Vergleich ist zulässig, weil <see cref="SessionSnapshot"/> ein
    /// <c>record</c> ist und alle angezeigten Angaben trägt. Die Dauer gehört ausdrücklich
    /// nicht dazu: Sie läuft in der Ansicht in deren eigenem Takt weiter und braucht dafür
    /// keine Meldung von hier.</para>
    /// </remarks>
    /// <param name="sessions">Der neue Stand.</param>
    private void SetActive(IReadOnlyList<SessionSnapshot> sessions)
    {
        bool changed;

        lock (_activeGate)
        {
            changed = !_active.SequenceEqual(sessions);

            if (changed)
            {
                _active.Clear();
                _active.AddRange(sessions);
            }
        }

        if (changed)
        {
            Context.Notifier.Raise(ActiveSessionsChanged, this, sessions);
        }
    }
}
