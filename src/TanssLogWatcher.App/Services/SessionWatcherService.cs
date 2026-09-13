using System.Globalization;
using System.Runtime.Versioning;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Monitoring;
using TanssLogWatcher.Monitoring.Model;
using TanssLogWatcher.Storage.History;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;

namespace TanssLogWatcher.App.Services;

/// <summary>
/// Beobachtet im Takt <c>watcher.poll_interval_seconds</c> und legt fertige Sitzungen dem
/// Abschlussdialog vor.
/// </summary>
/// <remarks>
/// <para><b>Dieser Dienst spricht nie mit TANSS.</b> Er meldet eine abgeschlossene Sitzung, und
/// gebucht wird sie aus dem Abschlussdialog heraus. So kostet ein Netzfehler keine Sitzung, und
/// genau deshalb läuft die Beobachtung im Zustand <see cref="RuntimeState.Degraded"/> ungerührt
/// weiter.</para>
///
/// <para><b>Beim gewöhnlichen Sitzungsende entsteht keine Warteschlangenzeile mehr.</b> Das ist
/// die Kehrtwende gegenüber der Vorgängerversion, und sie ist so angeordnet: Eingereiht wird
/// nur, was sich <i>nicht</i> buchen liess. Die Warteschlange ist damit wieder das, wofür sie
/// gebaut wurde — der Fehlerpfad — und nicht der Regelweg jeder Sitzung. Die Sitzung ist
/// deswegen nicht ungesichert: Sie bleibt bis zur Antwort im Dialog als <b>laufende</b> Sitzung
/// auf der Platte vermerkt (<see cref="OpenSession"/>), und die Wiederherstellung beim nächsten
/// Start holt sie von dort. Zwei Ausnahmen reihen weiterhin selbst ein, und beide sind
/// begründet: das geordnete Beenden (<see cref="ShutdownAsync"/>) und die Wiederherstellung
/// (<see cref="PrimeAsync"/>) — dort gibt es keinen Dialog mehr, der antworten könnte.</para>
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
        + "legt jede abgeschlossene Sitzung dem Abschlussdialog vor. In die Warteschlange geht "
        + "sie erst, wenn das Buchen misslingt oder niemand antwortet.";

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

        // VOR dem Abschliessen der offenen Sitzungen: Jene werden gleich selbst mit
        // awaitDecision eingereiht und wuerden sonst zweimal vorgelegt - einmal aus der
        // Warteschlange und einmal als frisch abgeschlossene Sitzung.
        OfferPendingDecisions(composition);

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

                // Aus dem gespeicherten Bezeichner neu gebildet, nicht aus device_name: Der
                // ist auf 120 Zeichen gestutzt und traefe keine Zuordnung mehr. Der Riegel
                // des Profilkatalogs greift hier ein zweites Mal, und zwar mit dem HEUTIGEN
                // Katalog - ein Profil, das seine Zusicherung verloren hat oder gar nicht
                // mehr steht, ergibt keine Kennung.
                DeviceId = DeviceIdentity.From(session.MonitorKey, session.IdentityKey),
            };

            bool added;
            try
            {
                // Auch hier awaitDecision: Fuer diese Sitzung geht gleich ein Abschlussdialog
                // auf (siehe Close weiter unten - die Meldung traegt Enqueued), und bis er
                // beantwortet ist, gilt dasselbe wie fuer jede andere Sitzung. Ohne das
                // Kennzeichen ginge ausgerechnet die wiederhergestellte Sitzung ungefragt
                // hinaus, waehrend der Techniker noch vor ihrem Fenster sitzt.
                added = composition.Queue.Enqueue(payload, awaitDecision: true);
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

                // Auch in der Abschrift, damit eine wiederhergestellte Sitzung in Anzeige und
                // Verlauf dieselbe Auskunft gibt wie eine ununterbrochene.
                IdentityKey = session.IdentityKey ?? string.Empty,
                ProcessId = session.ProcessId,
                StartedAt = session.StartedAt,
                EndedAt = session.LastSeenAt,
                RemoteSupportTypeId = session.RemoteSupportTypeId,
                Comment = session.Comment,
            };

            string reason = added
                ? "Aus einem früheren Lauf übernommen und eingereiht; als Ende gilt der letzte "
                  + "beobachtete Zeitpunkt und nicht jetzt. Zurückgestellt, bis der "
                  + "Abschlussdialog beantwortet ist."
                : "Aus einem früheren Lauf übernommen; die Kennung stand bereits in der "
                  + "Warteschlange und wurde nicht erneut aufgenommen.";

            Log(composition, "watch.recover", snapshot,
                added ? SessionOutcome.Deferred : SessionOutcome.Skipped, reason,
                SessionTrigger.Startup);

            // Als Ende gilt LastSeenAt, also der letzte beobachtete Augenblick - das ist eine
            // Schaetzung und keine Messung. Der Verlauf sagt das ausdruecklich, sonst laese
            // sich spaeter nicht mehr unterscheiden, welche Dauer belegt ist.
            Close(composition, new SessionClosed(snapshot,
                added ? SessionDisposition.Enqueued : SessionDisposition.AlreadyQueued, reason),
                endedEstimated: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Legt Sitzungen erneut vor, deren Abschlussdialog nie beantwortet wurde.
    /// </summary>
    /// <remarks>
    /// <para><b>Die Antwort auf die eine Frage, die der Wegfall der Frist offenlässt:</b> Was
    /// geschieht mit einer Zeile, deren Dialog niemand beantwortet hat — weil die Anwendung
    /// abstürzte, weil das Fenster weggeklickt wurde oder weil der Techniker Feierabend
    /// machte?</para>
    ///
    /// <para><b>Gewählt ist: beim nächsten Start erneut vorlegen.</b> Der Weg verliert keine
    /// Arbeitszeit — die Zeile bleibt, bis jemand entscheidet — und er bucht nichts ungefragt.
    /// Der andere vertretbare Weg wäre gewesen, beim geordneten Beenden hinauszuschicken, was
    /// noch wartet; der ist verworfen, weil „geordnetes Beenden“ und „der Techniker hat
    /// entschieden“ zweierlei sind: Das Abmelden am Freitagabend schickte dann jede Sitzung mit
    /// der automatischen Beschreibung hinaus, ohne Firma, ohne Ticket, ohne Bericht — genau der
    /// Zustand, der diesen Umbau ausgelöst hat. Und ein Absturz beendet gar nicht geordnet; der
    /// Weg hätte den häufigsten Fall nicht einmal abgedeckt.</para>
    ///
    /// <para>Vorgelegt wird über dasselbe Ereignis wie ein frisches Sitzungsende, damit es dafür
    /// keinen zweiten Weg mit eigenen Fehlern gibt. Der Verlauf wird dabei <b>nicht</b>
    /// fortgeschrieben: Die Sitzung ist längst abgeschlossen und steht dort, wo sie
    /// hingehört — hier wird nur ein Fenster nachgeholt.</para>
    ///
    /// <para>Hausregel 5: Lässt sich die Warteschlange nicht lesen, meldet das der Sendedienst;
    /// die Beobachtung läuft trotzdem an.</para>
    ///
    /// <para><b>Sichtbar für die Prüfung und nicht privat</b> (<c>InternalsVisibleTo</c>): Was
    /// hier steht, ist die Antwort auf „was wird aus einer unbeantworteten Sitzung“ — und diese
    /// Antwort muss sich prüfen lassen, ohne den ganzen Beobachtungstakt anzuwerfen.</para>
    /// </remarks>
    internal void OfferPendingDecisions(RuntimeComposition composition)
    {
        IReadOnlyList<QueuedUpload> waiting;
        try
        {
            waiting = [.. composition.Queue.List(QueueState.Pending).Items
                .Where(item => item.AwaitingDecision)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        foreach (QueuedUpload item in waiting)
        {
            SessionSnapshot snapshot = Reconstruct(composition, item);

            Log(composition, "dialog.reoffer", snapshot, SessionOutcome.Deferred,
                "Der Abschlussdialog dieser Sitzung wurde nie beantwortet; sie wartet seither "
                + "in der Warteschlange und ist NICHT nach TANSS gegangen. Der Dialog wird "
                + "erneut vorgelegt.", SessionTrigger.Startup);

            // Ohne RecordHistory: Die Zeile im Verlauf steht seit dem Sitzungsende und ist
            // richtig, wie sie ist. Close() waere hier also das falsche Werkzeug.
            Context.Notifier.Raise(SessionEnded, this, new SessionClosed(snapshot,
                SessionDisposition.AlreadyQueued,
                "Diese Sitzung wartet seit einem früheren Lauf auf Ihre Entscheidung. "
                + "Gebucht wurde sie nicht."));
        }
    }

    /// <summary>
    /// Legt den Abschlussdialog zu einem wartenden Eintrag <b>noch einmal</b> vor.
    /// </summary>
    /// <remarks>
    /// <para><b>Derselbe Weg wie beim Neustart, und das ist der ganze Witz.</b> Beim Start
    /// werden unbeantwortete Einträge erneut vorgelegt; hier geschieht genau dasselbe, nur auf
    /// Zuruf. Ein zweiter Dialogweg neben diesem hätte einen zweiten Riegel gebraucht, eine
    /// zweite Stelle für das Protokoll und eine zweite Gelegenheit, sie auseinanderlaufen zu
    /// lassen.</para>
    ///
    /// <para><b>Geändert wird nichts hier.</b> Diese Methode legt vor; was der Techniker dann
    /// tut, entscheidet der Dialog, und geschrieben wird über <c>UpdatePayload</c> — das
    /// schreibt ausschliesslich auf <c>state = 'pending'</c> und schliesst damit das Zeitfenster
    /// zum Sendedienst.</para>
    ///
    /// <para><b>Warum ein Grund zurückkommt und nicht nur ein <c>bool</c>.</b> Ein Doppelklick,
    /// der nichts tut, sieht aus wie ein Fehler des Werkzeugs. „Gerade unterwegs“, „steht schon
    /// in TANSS“ und „gibt es nicht mehr“ sind drei verschiedene Sachverhalte, und der
    /// Techniker soll erfahren, welcher davon zutrifft.</para>
    /// </remarks>
    /// <param name="remoteMaintenanceId">Die Kennung des Eintrags.</param>
    /// <returns>Ob der Dialog kommt — und wenn nicht, warum.</returns>
    public ReofferResult Reoffer(string remoteMaintenanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteMaintenanceId);

        if (Context.Composition is not { } composition)
        {
            return new ReofferResult(false,
                "Ohne eingerichtete Verbindung gibt es keine Warteschlange, die sich ändern "
                + "liesse. Die Einrichtung steht unter „Einstellungen“.");
        }

        QueuedUpload? item;

        try
        {
            item = composition.Queue.Find(remoteMaintenanceId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Die Warteschlange ist hin, die Seite nicht. Und ausdruecklich KEIN
            // Dialog auf gut Glueck - ohne den Eintrag waere er ohne Grundlage.
            return new ReofferResult(false,
                "Die Warteschlange liess sich nicht lesen: " + ex.Message
                + " Ob der Eintrag noch wartet, ist damit ungeklärt.");
        }

        if (item is null)
        {
            return new ReofferResult(false,
                "Diesen Eintrag gibt es nicht mehr. Er ist inzwischen gesendet oder verworfen "
                + "worden; unter „Verlauf“ steht, was aus der Sitzung geworden ist.");
        }

        if (item.State != QueueState.Pending)
        {
            return new ReofferResult(false, item.State switch
            {
                QueueState.Sending =>
                    "Dieser Eintrag ist gerade unterwegs nach TANSS. Solange das läuft, wird er "
                    + "nicht geändert — sonst ginge etwas anderes hinaus, als hier steht.",
                QueueState.Done =>
                    "Diese Fernwartung steht bereits in TANSS. Sie ist dort zu ändern und nicht "
                    + "hier; die Zeile bleibt nur stehen, damit nichts ein zweites Mal gebucht "
                    + "wird.",
                QueueState.Failed =>
                    "Dieser Eintrag ist aufgegeben worden und geht nicht mehr von selbst hinaus. "
                    + "Ändern lässt er sich deshalb nicht mehr.",
                _ =>
                    "Dieser Eintrag lässt sich im Zustand „" + item.State + "“ nicht ändern.",
            });
        }

        SessionSnapshot snapshot = Reconstruct(composition, item);

        Log(composition, "dialog.reopen", snapshot, SessionOutcome.Deferred,
            "Der Abschlussdialog wurde aus der Warteschlange heraus erneut geöffnet. Gesendet "
            + "wurde dadurch nichts; der Eintrag wartet unverändert weiter, bis der Dialog "
            + "beantwortet ist.", SessionTrigger.Manual);

        Context.Notifier.Raise(SessionEnded, this, new SessionClosed(snapshot,
            SessionDisposition.AlreadyQueued,
            "Dieser Eintrag wartet in der Warteschlange. Was hier geändert wird, geht mit ihm "
            + "hinaus."));

        return new ReofferResult(true, string.Empty);
    }

    /// <summary>
    /// Stellt aus einer wartenden Warteschlangenzeile so viel Sitzung her, wie belegbar ist.
    /// </summary>
    /// <remarks>
    /// <para>Die Nutzlast trägt Beginn, Ende, Fernwartungstyp, Kommentar, Ticket und den
    /// Gerätenamen. Was sie <b>nicht</b> trägt, ist das Profil — welche Anwendung beobachtet
    /// wurde und wie sie heisst. Steht die Sitzung im Verlauf, kommt es von dort; sonst bleibt
    /// es bei einer Angabe, die sagt, dass sie fehlt. Hausregel 2: lieber „nicht mehr
    /// ermittelbar“ als ein erfundener Anwendungsname in einem Text, der zum Kunden geht.</para>
    /// <para>Der Bezeichner der Gegenstelle bleibt bewusst leer: Er entstünde hier nur als
    /// Vermutung aus dem gekürzten Gerätenamen, und aus einer Vermutung darf keine
    /// Gerätekennung werden. Für die Anzeige im Dialog braucht es ihn nicht.</para>
    /// </remarks>
    private static SessionSnapshot Reconstruct(RuntimeComposition composition, QueuedUpload item)
    {
        SessionHistoryEntry? history = null;
        try
        {
            history = composition.History.Find(item.RemoteMaintenanceId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Der Verlauf ist hier eine Bequemlichkeit und keine Bedingung.
            history = null;
        }

        string destination = FirstNonEmpty(history?.Destination, item.Payload.DeviceName,
                                           "Keine Gegenstelle vermerkt");

        return new SessionSnapshot
        {
            SessionId = item.RemoteMaintenanceId,
            ProfileKey = history?.ProfileKey ?? string.Empty,
            ProfileName = FirstNonEmpty(history?.ProfileName, null,
                                        "Anwendung nicht mehr ermittelbar"),
            Destination = destination,
            // Ersatzweise der Augenblick des Einreihens: Eine 0 heisst in der Nutzlast
            // "nicht gesetzt", und der Zeitpunkt der Zeile ist eine Messung, keine Erfindung.
            StartedAt = TanssTime.FromUnixSeconds(item.Payload.StartTime)?.ToLocalTime()
                        ?? item.CreatedAt.ToLocalTime(),
            EndedAt = TanssTime.FromUnixSeconds(item.Payload.EndTime)?.ToLocalTime()
                      ?? item.CreatedAt.ToLocalTime(),
            RemoteSupportTypeId = item.Payload.TypeId,
            Comment = item.Payload.Comment,
        };
    }

    /// <summary>Der erste Wert, der etwas aussagt.</summary>
    private static string FirstNonEmpty(string? first, string? second, string fallback) =>
        !string.IsNullOrWhiteSpace(first) ? first
        : !string.IsNullOrWhiteSpace(second) ? second
        : fallback;

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
    /// Hier geht es um bares Geld. Eine Fernwartung, die beim Schliessen des Fensters
    /// verschwindet, ist eine Stunde Arbeit, die niemand mehr belegen kann.
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

        // "eingereiht" steht nur noch da, wenn wirklich etwas eingereiht wurde - im laufenden
        // Takt ist das nie der Fall. Eine Zeile "0 eingereiht" unter jedem Takt laese sich als
        // "es klappt gerade nichts" lesen, waehrend in Wahrheit alles seinen Weg geht.
        string summary = string.Create(CultureInfo.CurrentCulture,
            $"{active.Count} laufend, {run.Started.Count} begonnen, {run.Ended.Count} beendet")
            + (enqueued > 0
                ? string.Create(CultureInfo.CurrentCulture, $", {enqueued} eingereiht.")
                : ".");

        return summary
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
    /// Schliesst eine Sitzung ab: legt sie dem Abschlussdialog vor — oder reiht sie ein, wenn
    /// keiner mehr antworten kann.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Kern des Umbaus steht in dieser Methode.</b> Beim gewöhnlichen
    /// Sitzungsende entsteht <b>keine</b> Warteschlangenzeile: Der Dialog hält die Sitzung und
    /// bucht sie gleich selbst. Eingereiht wird nur beim geordneten Beenden — dann geht kein
    /// Dialog mehr auf, der antworten könnte, und eine Zeile ist der einzige Weg, die Sitzung
    /// über den Neustart zu tragen.</para>
    ///
    /// <para><b>Die laufende Sitzung wird hier ausdrücklich NICHT aus
    /// <c>open_sessions</c> entfernt.</b> Sie ist ab jetzt die einzige Sicherung, solange der
    /// Dialog offen steht: Stürzt das Werkzeug, während der Techniker tippt, findet die
    /// Wiederherstellung sie beim nächsten Start dort und legt sie erneut vor. Aufgeräumt wird
    /// die Zeile von dem, der die Sitzung versorgt — dem Abschlussdialog.</para>
    ///
    /// <para><b>Sichtbar für die Prüfung und nicht privat</b> (<c>InternalsVisibleTo</c>): Dass
    /// beim Sitzungsende nichts eingereiht wird, ist die eine Eigenschaft, an der dieser Umbau
    /// hängt. Sie muss sich prüfen lassen, ohne echte Fenster und Prozesse zu beschaffen.</para>
    /// </remarks>
    /// <param name="composition">Die Bausteine.</param>
    /// <param name="session">Die abgeschlossene Sitzung.</param>
    /// <param name="now">Der Bezugszeitpunkt.</param>
    /// <param name="trigger">Was den Abschluss ausgelöst hat.</param>
    /// <returns><c>true</c>, wenn sie in die Warteschlange ging.</returns>
    internal bool Finish(RuntimeComposition composition, WatchedSession session,
                         DateTimeOffset now, SessionTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(session);

        SessionSnapshot snapshot = ToSnapshot(composition, session, now, ended: true);
        MonitoringSetting? setting = FindSetting(composition, session.ProfileKey);

        // ToSnapshot setzt als Ende ersatzweise "jetzt", wenn die Zustandsmaschine keinen
        // Zeitpunkt gemeldet hat. Das ist dann der Takt, in dem das Verschwinden auffiel, und
        // nicht der Augenblick des Verschwindens - also geschaetzt.
        bool estimated = session.EndedAt is null;

        if (setting is null || !setting.IsActive)
        {
            const string reason =
                "Keine Zuordnung auf einen Fernwartungstyp; es gäbe nichts, worauf gebucht "
                + "werden könnte. Die Sitzung ist erkannt, aber nirgends eingereiht. "
                + "Zuzuordnen ist die Kennung in monitoring[].remote_support_type_id.";

            Log(composition, "watch", snapshot, SessionOutcome.Skipped, reason, trigger);
            Close(composition, new SessionClosed(snapshot, SessionDisposition.NotMapped, reason),
                  estimated);
            return false;
        }

        if (IsDryRun)
        {
            string reason = DryRunReason
                ?? "Trockenlauf; es wurde nichts eingereiht und nichts nach TANSS geschrieben.";

            Log(composition, "watch", snapshot, SessionOutcome.DryRun, reason, trigger);
            Close(composition, new SessionClosed(snapshot, SessionDisposition.DryRun, reason),
                  estimated);
            return false;
        }

        if (trigger != SessionTrigger.Shutdown)
        {
            // DIE Stelle, die frueher jede Sitzung eingereiht hat. Jetzt geht nur eine Meldung
            // hinaus, und der Abschlussdialog haelt die Sitzung, bis jemand entscheidet.
            // Gesichert ist sie ueber ihre Zeile in open_sessions - die bleibt ausdruecklich
            // stehen, bis der Dialog geantwortet hat.
            const string held =
                "Der Abschlussdialog hält die Sitzung. Sie steht ausdrücklich NICHT in der "
                + "Warteschlange: Gebucht wird direkt aus dem Dialog heraus, und eingereiht "
                + "wird erst, wenn das misslingt. Bis dahin ist sie als laufende Sitzung auf "
                + "der Platte vermerkt — ein Absturz jetzt kostet sie nicht, sie wird beim "
                + "nächsten Start erneut vorgelegt.";

            Log(composition, "watch", snapshot, SessionOutcome.Deferred, held, trigger);
            Close(composition, new SessionClosed(snapshot, SessionDisposition.Held, held),
                  estimated);

            return false;
        }

        try
        {
            // Nur noch beim geordneten Beenden, und hier ist das Einreihen der einzige Weg:
            // Ein Dialog, der jetzt aufginge, hat kein Fenster mehr, in dem er stehen koennte.
            // awaitDecision, damit die Zeile die Nacht ueber nicht von selbst hinausgeht -
            // ungefragt gebucht werden darf nichts; vorgelegt wird sie beim naechsten Start.
            bool added = composition.Queue.Enqueue(ToPayload(composition, snapshot, setting),
                                                   awaitDecision: true);
            _ = composition.Queue.RemoveOpenSession(snapshot.SessionId);

            string reason = added
                ? "Beim Beenden eingereiht und zurückgestellt: Das Werkzeug geht gerade zu, "
                  + "und ein Abschlussdialog hätte kein Fenster mehr. Die Sitzung geht NICHT "
                  + "von selbst hinaus — sie wird beim nächsten Start vorgelegt."
                : "Die Kennung stand bereits in der Warteschlange und wurde nicht erneut "
                  + "aufgenommen — so entsteht keine Dublette, die TANSS nicht abfangen würde.";

            Log(composition, "watch", snapshot,
                added ? SessionOutcome.Deferred : SessionOutcome.Skipped, reason, trigger);

            Close(composition, new SessionClosed(snapshot,
                added ? SessionDisposition.Enqueued : SessionDisposition.AlreadyQueued, reason),
                estimated);

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
            Close(composition, new SessionClosed(snapshot, SessionDisposition.Failed, reason),
                  estimated);
            return false;
        }
    }

    /// <summary>
    /// Der eine Ausgang für jede abgeschlossene Sitzung: erst in den Verlauf, dann melden.
    /// </summary>
    /// <remarks>
    /// <para><b>Es gibt ihn, damit es keinen zweiten gibt.</b> Fünf Stellen in diesem Dienst
    /// schliessen eine Sitzung ab; jede von ihnen einzeln um eine Verlaufszeile zu ergänzen
    /// hiesse, dass die nächste hinzukommende sie vergisst — und eine fehlende Zeile fällt
    /// niemandem auf, weil die Seite dann eben eine Sitzung weniger zeigt.</para>
    ///
    /// <para>Der Verlauf wird <b>vor</b> der Meldung geschrieben: Die Empfänger der Meldung
    /// öffnen den Abschlussdialog, und der kann die Sitzung verwerfen. Verwirft er sie, bevor
    /// es die Zeile gibt, ginge das Verwerfen ins Leere — <c>MarkDiscarded</c> fände nichts.</para>
    /// </remarks>
    /// <param name="composition">Die Bausteine.</param>
    /// <param name="closed">Die abgeschlossene Sitzung samt Ausgang und Begründung.</param>
    /// <param name="endedEstimated">
    /// Ist das Ende geschätzt statt gemessen? Siehe <c>SessionHistoryEntry.EndedEstimated</c>.
    /// </param>
    private void Close(RuntimeComposition composition, SessionClosed closed,
                       bool endedEstimated)
    {
        RecordHistory(composition, closed, endedEstimated);
        Context.Notifier.Raise(SessionEnded, this, closed);
    }

    /// <summary>
    /// Hält die abgeschlossene Sitzung im Verlauf fest.
    /// </summary>
    /// <remarks>
    /// <para><b>Die Gegenstelle wandert in <c>Destination</c> und nicht in den
    /// Begründungstext</b> — dieselbe Überlegung wie bei <see cref="Log"/>: Nur dieses Feld
    /// unterliegt der Schwärzung und der kürzeren Frist
    /// <c>history.plain_text_days</c>. Im Begründungstext stünde sie im Klartext in
    /// <c>state.db</c>, und zwar unbefristet.</para>
    ///
    /// <para><b>Abgeriegelt wie der Protokolleintrag</b> (Hausregel 5): Der Verlauf ist der
    /// Beleg, nicht der Vorgang. Misslingt er, ist die Sitzung trotzdem eingereiht, und die
    /// Meldung an die Oberfläche geht ebenso hinaus.</para>
    /// </remarks>
    private static void RecordHistory(RuntimeComposition composition, SessionClosed closed,
                                      bool endedEstimated)
    {
        if (!composition.Config.History.Enabled)
        {
            return;
        }

        SessionSnapshot session = closed.Session;

        try
        {
            _ = composition.History.Record(new SessionHistoryEntry
            {
                SessionId = session.SessionId,
                StartedAt = session.StartedAt,
                EndedAt = session.EndedAt ?? session.StartedAt,
                ProfileKey = session.ProfileKey,
                ProfileName = session.ProfileName,
                Destination = session.Destination,

                // MonitoringSetting.NotMonitored ist -1 und eine Erfindung der Beobachtung.
                // In der Spalte heisst "keine Zuordnung" 0 - das ist ihr Vorgabewert, und der
                // Speicher laesst beim zweiten Abschluss genau die 0 von einem spaeter doch
                // ermittelten Typ ueberschreiben. Eine -1 stuende dort fuer immer.
                RemoteSupportTypeId = session.IsMapped ? session.RemoteSupportTypeId : 0,

                Disposition = ToHistory(closed.Disposition),
                Reason = closed.Reason,
                EndedEstimated = endedEstimated,
                Origin = HistoryOrigin.Live,

                // Pflichtangabe des Satztyps, die der Speicher sogleich verwirft: Er rechnet
                // den Loeschzeitpunkt selbst aus history.retention_days und dem Beginn. Hier
                // den Beginn zu setzen ist der einzige Wert, der nichts behauptet.
                DeleteAfter = session.StartedAt,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Eine nicht geschriebene Verlaufszeile darf keine Sitzung kosten.
        }
    }

    /// <summary>
    /// Bildet den Ausgang der Oberfläche auf den des Verlaufs ab.
    /// </summary>
    /// <remarks>
    /// <para><b>Die einzige Stelle, an der diese beiden Aufzählungen zusammentreffen.</b> Sie
    /// sehen sich ähnlich und meinen Verschiedenes: <see cref="SessionDisposition"/> sagt, was
    /// die Anzeige gerade zeigen soll, <c>HistoryDisposition</c> sagt, was mit der Sitzung
    /// geschehen ist. An vier Stellen geschrieben würde die Abbildung an vier Stellen
    /// verschieden.</para>
    ///
    /// <para><see cref="SessionDisposition.AlreadyQueued"/> wird zu <c>Enqueued</c> und nicht
    /// zu einem eigenen Wert: Die Sitzung <i>steht</i> in der Warteschlange, sie wurde nur
    /// nicht ein zweites Mal aufgenommen. Für den Verlauf ist das derselbe Sachverhalt — und
    /// der Unterschied steht ohnehin im Begründungstext.</para>
    ///
    /// <para><b><see cref="SessionDisposition.Held"/> wird ebenfalls zu <c>Enqueued</c>, und
    /// das ist die einzige Stelle dieses Umbaus, an der zwei Dinge auf einen Wert fallen.</b>
    /// Der Verlauf führt keinen Ausgang „wartet auf den Dialog“; seine Aufzählung steht in der
    /// Ablage, wird als Text gespeichert und von der Verlaufsansicht ausgewertet. Gewählt ist
    /// <c>Enqueued</c>, weil die Verlaufsansicht ihn als <b>„wartet“</b> zeigt — und das trifft
    /// zu: Die Sitzung wartet, nur eben auf eine Antwort statt auf einen Sendelauf. Wo genau
    /// sie wartet, steht im Begründungstext derselben Zeile, und sobald jemand antwortet, wird
    /// die Zeile ohnehin auf <c>Booked</c>, <c>Discarded</c> oder <c>Failed</c> fortgeschrieben.
    /// Ein <c>Unknown</c> wäre hier falsch: Es heisst „nicht mehr feststellbar“, und
    /// feststellbar ist dieser Zustand sehr wohl.</para>
    /// </remarks>
    private static HistoryDisposition ToHistory(SessionDisposition disposition) =>
        disposition switch
        {
            SessionDisposition.Enqueued => HistoryDisposition.Enqueued,
            SessionDisposition.AlreadyQueued => HistoryDisposition.Enqueued,
            SessionDisposition.Held => HistoryDisposition.Enqueued,
            SessionDisposition.NotMapped => HistoryDisposition.NotMapped,
            SessionDisposition.DryRun => HistoryDisposition.DryRun,
            SessionDisposition.Failed => HistoryDisposition.Failed,

            // Kein Wurf: Ein unbekannter Ausgang ist kein Grund, eine Sitzung zu verlieren.
            // Der Verlauf fuehrt dafuer einen eigenen Wert, statt zu raten.
            _ => HistoryDisposition.Unknown,
        };

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

                // Der Bezeichner wandert UNGEKUERZT mit, damit die Wiederherstellung nach
                // einem Neustart dieselbe Geraetekennung bilden kann wie ToPayload. Ohne ihn
                // buchte genau die unterbrochene Fernwartung ohne Firmenzuordnung.
                IdentityKey = session.IdentityKey,
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
    /// Sie entsteht aus dem, was beim Abschluss der Sitzung gemessen wurde. Würde sie beim
    /// Senden nachgerechnet, hinge sie an Einstellungen, die sich seither geändert haben
    /// könnten — und die hochgeladene Fernwartung wäre nicht mehr die beobachtete.
    /// </remarks>
    private static RemoteSupportWrite ToPayload(RuntimeComposition composition,
                                                SessionSnapshot session,
                                                MonitoringSetting setting) =>
        Build(composition, session, setting.RemoteSupportTypeId);

    /// <summary>
    /// Dieselbe Nutzlast, gebaut aus der Abschrift allein — oder <see langword="null"/>, wenn
    /// die Sitzung auf keinen Fernwartungstyp abgebildet ist.
    /// </summary>
    /// <remarks>
    /// <para><b>Sie gehört hierher, obwohl der Abschlussdialog sie braucht.</b> Seit beim
    /// Sitzungsende keine Zeile mehr entsteht, bauen drei Stellen dieselbe Nutzlast: dieser
    /// Dienst beim geordneten Beenden, der Abschlussdialog beim Buchen und derselbe Dialog,
    /// wenn er nach einem misslungenen Versand einreiht. Drei Rechenwege ergäben drei
    /// Fernwartungen, die sich in Gerätekennung oder Zeitraum unterscheiden — und die
    /// Existenzprüfung gegen Dubletten nimmt genau diese Felder.</para>
    /// <para>Der Fernwartungstyp kommt aus der Abschrift und nicht erneut aus dem
    /// Einstellungssatz: Er ist dort beim Abschluss eingetragen worden, und eine Zuordnung, die
    /// sich seither geändert hat, darf die beobachtete Sitzung nicht rückwirkend umbuchen.</para>
    /// </remarks>
    /// <param name="composition">Die Bausteine.</param>
    /// <param name="session">Die abgeschlossene Sitzung.</param>
    /// <returns>Die Nutzlast, oder <see langword="null"/> ohne Zuordnung.</returns>
    internal static RemoteSupportWrite? PayloadFor(RuntimeComposition composition,
                                                   SessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(session);

        return session.IsMapped
            ? Build(composition, session, session.RemoteSupportTypeId)
            : null;
    }

    /// <summary>Die eine Stelle, an der die Nutzlast entsteht.</summary>
    private static RemoteSupportWrite Build(RuntimeComposition composition,
                                            SessionSnapshot session, int typeId) => new()
    {
        TypeId = typeId,
        EmployeeId = composition.Config.Tanss.EmployeeId,
        StartTime = TanssTime.ToUnixSeconds(session.StartedAt),
        EndTime = TanssTime.ToUnixSeconds(session.EndedAt ?? session.StartedAt),
        RemoteMaintenanceId = session.SessionId,
        Comment = session.Comment,
        DeviceName = Shorten(session.Destination, 120),

        // Der Schluessel, ueber den TANSS die Firma selbst einsetzt - und ausdruecklich der
        // BEZEICHNER, nicht die gekuerzte Gegenstelle daneben: Was auf 120 Zeichen gestutzt
        // wurde, trifft keine Zuordnung mehr. Ist kein Geraet gemeint, bleibt das Feld weg.
        DeviceId = DeviceIdentity.From(session.ProfileKey, session.IdentityKey),
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
            IdentityKey = session.IdentityKey,
            ProcessId = session.ProcessId,
            WindowHandle = session.WindowHandle,
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

/// <summary>
/// Ob der Abschlussdialog zu einem wartenden Eintrag noch einmal kommt — und wenn nicht, warum.
/// </summary>
/// <remarks>
/// <b>Der Grund ist Pflicht und nicht Beiwerk</b> (Hausregel 3). Ein Doppelklick, der nichts
/// tut, sieht aus wie ein Fehler des Werkzeugs; „gerade unterwegs“, „steht schon in TANSS“ und
/// „gibt es nicht mehr“ sind drei verschiedene Sachverhalte mit drei verschiedenen Folgen.
/// </remarks>
/// <param name="Opened">Kommt der Dialog?</param>
/// <param name="Reason">Warum nicht — leer, wenn er kommt.</param>
public sealed record ReofferResult(bool Opened, string Reason);
