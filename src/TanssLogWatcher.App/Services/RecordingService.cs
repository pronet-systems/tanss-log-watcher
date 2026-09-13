using System.Globalization;
using System.Runtime.Versioning;
using System.Threading.Channels;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Recording;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Recordings;

namespace TanssLogWatcher.App.Services;

/// <summary>
/// Zeichnet die Bildschirme laufender Sitzungen auf und löscht, was seine Frist überschritten
/// hat.
/// </summary>
/// <remarks>
/// <para><b>Er zeichnet nichts auf, was nicht gebucht wird.</b> Eine Sitzung ohne Zuordnung auf
/// einen Fernwartungstyp geht nirgendwohin; ein Video davon wäre personenbezogene Daten ohne
/// den Vorgang, zu dem sie gehören. Aufgezeichnet wird deshalb genau das, was auch in die
/// Warteschlange geht.</para>
///
/// <para><b>Er fragt vor jeder Sitzung erneut, ob er darf.</b>
/// <see cref="RecordingSection.IsUsable"/> beantwortet das und sonst niemand: eingeschaltet,
/// Kenntnisnahme erteilt, Rechtsgrundlage und Beleg benannt. Wer die Kenntnisnahme zurückzieht,
/// hält damit die nächste Aufzeichnung auf, ohne das Werkzeug neu starten zu müssen.</para>
///
/// <para><b>Das Aufräumen läuft auch dann, wenn nicht aufgezeichnet wird.</b> Was ein früherer
/// Lauf hinterlassen hat, muss gelöscht werden, wenn seine Frist abläuft — und zwar unabhängig
/// davon, ob der Schalter heute noch steht. Eine Löschfrist, die sich abschalten liesse, wäre
/// keine.</para>
///
/// <para><b>Hausregel 5, eine Stufe strenger.</b> Was hier schiefgeht, kostet die Aufzeichnung
/// und nie die Sitzung: Die beiden Dienste kennen einander nur über ein Ereignis, und dieser
/// hier schreibt nichts, was der andere liest.</para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class RecordingService : PeriodicService
{
    /// <summary>Wie oft nach abgelaufenen Fristen gesehen wird.</summary>
    /// <remarks>
    /// Stündlich, nicht bei jedem Takt: Der Durchlauf liest die Buchführung und fasst das
    /// Dateisystem an. Eine Frist steht in Tagen; eine Stunde Verzug ist dabei ohne Belang.
    /// </remarks>
    private static readonly TimeSpan CleanupEvery = TimeSpan.FromHours(1);

    private readonly SessionWatcherService _sessions;
    private readonly Channel<RecordingCommand> _commands =
        Channel.CreateUnbounded<RecordingCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

    private readonly Dictionary<string, SessionRecording> _running =
        new(StringComparer.Ordinal);

    private readonly IWindowGeometrySource _geometry = new Win32WindowGeometrySource();
    private readonly IScreenSource _screens = new Win32ScreenSource();

    private DateTimeOffset? _lastCleanup;
    private string _lastCleanupSummary = string.Empty;

    /// <summary>Baut den Dienst und hängt sich an die Sitzungsbeobachtung.</summary>
    /// <param name="context">Der Zugang zu Zustand und Zusammenbau.</param>
    /// <param name="sessions">Die Sitzungsbeobachtung, deren Ereignisse den Anlass geben.</param>
    public RecordingService(IRuntimeContext context, SessionWatcherService sessions)
        : base(context)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = sessions;

        // Ueber einen Kanal und nicht unmittelbar: Beide Ereignisse kommen auf dem Strang der
        // Oberflaeche an. Ein Grafikgeraet anzulegen und einen Aufnahmestrang zu starten
        // gehoert nicht dorthin - die Oberflaeche stockte sichtbar.
        _sessions.SessionStarted += OnSessionStarted;
        _sessions.SessionEnded += OnSessionEnded;
    }

    /// <inheritdoc />
    public override string Name => "Bildschirmaufzeichnung";

    /// <inheritdoc />
    public override string Description =>
        "Zeichnet die Fenster laufender Fernwartungen auf und löscht jede Aufzeichnung, sobald "
        + "ihre Aufbewahrungsfrist abgelaufen ist.";

    /// <summary>Wie viele Sitzungen gerade aufgezeichnet werden.</summary>
    public int ActiveRecordings
    {
        get
        {
            lock (_running)
            {
                return _running.Count;
            }
        }
    }

    /// <inheritdoc />
    protected override TimeSpan Interval => TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    protected override string NotConfiguredMessage =>
        "Ruht: Ohne Konfiguration gibt es weder einen Ordner für Aufzeichnungen noch eine "
        + "Löschfrist. Es wird nichts aufgezeichnet und nichts gelöscht.";

    /// <inheritdoc />
    protected override string FaultMessage =>
        "Der letzte Takt der Aufzeichnung ist mit einem unerwarteten Fehler abgebrochen. Die "
        + "Sitzungen selbst sind davon nicht betroffen — sie werden unabhängig davon erkannt "
        + "und eingereiht.";

    /// <inheritdoc />
    protected override Task PrimeAsync(RuntimeComposition composition, CancellationToken ct)
    {
        // Beim Start und nicht erst nach einer Stunde: Ein Rechner, der zwei Monate im Schrank
        // lag, kommt mit lauter ueberfaelligen Aufzeichnungen zurueck.
        Cleanup(composition);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task<string> RunCycleAsync(RuntimeComposition composition,
                                                  CancellationToken ct)
    {
        DrainCommands(composition);
        Reap();

        if (Due(composition))
        {
            Cleanup(composition);
        }

        return Task.FromResult(Describe(composition));
    }

    /// <inheritdoc />
    protected override async Task ShutdownAsync(RuntimeComposition composition)
    {
        List<SessionRecording> running;

        lock (_running)
        {
            running = [.. _running.Values];
            _running.Clear();
        }

        foreach (SessionRecording recording in running)
        {
            recording.MarkEnded();
        }

        // Erst allen sagen, dass Schluss ist, dann warten: Nacheinander summierten sich die
        // Abschluesse, und beim Herunterfahren wartet ein Mensch.
        foreach (SessionRecording recording in running)
        {
            try
            {
                _ = await recording.Completion
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ContinueWith(t => t.IsCompleted, TaskScheduler.Default)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Die Datei bleibt ohne Abschluss liegen. Der Eintrag in der Buchfuehrung
                // steht trotzdem, und der Aufraeumer holt sie zu ihrer Frist ab.
            }
            finally
            {
                recording.Dispose();
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sessions.SessionStarted -= OnSessionStarted;
            _sessions.SessionEnded -= OnSessionEnded;
        }

        base.Dispose(disposing);
    }

    private void OnSessionStarted(object? sender, SessionSnapshot session) =>
        _commands.Writer.TryWrite(new RecordingCommand(Started: true, session));

    private void OnSessionEnded(object? sender, SessionClosed closed) =>
        _commands.Writer.TryWrite(new RecordingCommand(Started: false, closed.Session));

    /// <summary>Arbeitet ab, was seit dem letzten Takt gemeldet wurde.</summary>
    private void DrainCommands(RuntimeComposition composition)
    {
        while (_commands.Reader.TryRead(out RecordingCommand command))
        {
            if (command.Started)
            {
                Begin(composition, command.Session);
            }
            else
            {
                End(command.Session);
            }
        }
    }

    /// <summary>
    /// Beginnt die Aufzeichnung einer Sitzung — oder sagt, warum nicht.
    /// </summary>
    /// <remarks>
    /// Es wird protokolliert, dass <b>nicht</b> aufgezeichnet wurde, und warum. Eine
    /// Aufzeichnung, die stillschweigend ausbleibt, fällt erst an dem Tag auf, an dem jemand
    /// sie sucht.
    /// </remarks>
    private void Begin(RuntimeComposition composition, SessionSnapshot session)
    {
        RecordingSection settings = composition.Config.Recording;

        if (RecordingGate.WhyNotRecording(settings) is { } why)
        {
            if (settings.Enabled)
            {
                Log(composition, session, SessionOutcome.Skipped, why);
            }

            return;
        }

        if (!session.IsMapped)
        {
            Log(composition, session, SessionOutcome.Skipped,
                "Nicht aufgezeichnet: Diese Beobachtungsregel ist auf keinen Fernwartungstyp "
                + "abgebildet, die Sitzung wird also nirgends gebucht. Ein Video ohne den "
                + "Vorgang, zu dem es gehört, wäre allein eine Datensammlung.");

            return;
        }

        if (session.ProcessId <= 0)
        {
            Log(composition, session, SessionOutcome.Skipped,
                "Nicht aufgezeichnet: Zu dieser Sitzung gibt es keinen Prozess, dessen Fenster "
                + "sich aufnehmen liessen.");

            return;
        }

        string root = RecordingPaths.Root(settings);

        SessionRecording recording = new(session, settings, root, composition.Recordings,
                                         _geometry, _screens, Context.Clock);

        lock (_running)
        {
            if (!_running.TryAdd(session.SessionId, recording))
            {
                recording.Dispose();
                return;
            }
        }

        recording.Start();

        Log(composition, session, SessionOutcome.Ok,
            string.Create(CultureInfo.CurrentCulture,
                $"Aufzeichnung begonnen. Sie wird am "
                + $"{(session.StartedAt + TimeSpan.FromDays(settings.RetentionDays)).ToLocalTime():dd.MM.yyyy} "
                + $"gelöscht ({settings.RetentionDays} Tage Aufbewahrung, "
                + $"{settings.LegalBasis})."));
    }

    /// <summary>Sagt der Aufzeichnung, dass die Sitzung vorbei ist.</summary>
    private void End(SessionSnapshot session)
    {
        lock (_running)
        {
            if (_running.TryGetValue(session.SessionId, out SessionRecording? recording))
            {
                recording.MarkEnded();
            }
        }
    }

    /// <summary>Räumt beendete Aufzeichnungen ab.</summary>
    private void Reap()
    {
        List<SessionRecording> done = [];

        lock (_running)
        {
            foreach ((string id, SessionRecording recording) in _running)
            {
                if (recording.Completion.IsCompleted)
                {
                    done.Add(recording);
                }
            }

            foreach (SessionRecording recording in done)
            {
                _ = _running.Remove(recording.Session.SessionId);
            }
        }

        foreach (SessionRecording recording in done)
        {
            recording.Dispose();
        }
    }

    /// <summary>Ist ein Aufräumdurchlauf fällig?</summary>
    private bool Due(RuntimeComposition composition) =>
        _lastCleanup is not { } last
        || Context.Clock.GetUtcNow() - last >= CleanupEvery;

    /// <summary>Ein Aufräumdurchlauf.</summary>
    private void Cleanup(RuntimeComposition composition)
    {
        _lastCleanup = Context.Clock.GetUtcNow();

        try
        {
            RecordingSection settings = composition.Config.Recording;

            CleanupResult result = new RecordingCleaner(
                composition.Recordings, RecordingPaths.Root(settings))
                .Run(TimeSpan.FromDays(settings.RetentionDays));

            _lastCleanupSummary = result.Summary;

            if (result.DidAnything)
            {
                _ = composition.Log.Append(new SessionLogEntry
                {
                    Operation = "recording.cleanup",
                    Outcome = result.Failed > 0 ? SessionOutcome.Error : SessionOutcome.Ok,
                    Reason = result.Summary,
                    Trigger = SessionTrigger.Retry,
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Ein misslungener Durchlauf kostet den Durchlauf. Beim naechsten
            // Mal steht dasselbe wieder an - nichts geht dabei verloren.
            _lastCleanupSummary = "Das Aufräumen ist fehlgeschlagen: " + ex.Message;
        }
    }

    /// <summary>Der Satz, der nach dem Takt angezeigt wird.</summary>
    private string Describe(RuntimeComposition composition)
    {
        RecordingSection settings = composition.Config.Recording;

        if (RecordingGate.WhyNotRecording(settings) is { } why)
        {
            return "Ruht: " + why + Suffix();
        }

        List<SessionRecording> running;

        lock (_running)
        {
            running = [.. _running.Values];
        }

        if (running.Count == 0)
        {
            return string.Create(CultureInfo.CurrentCulture,
                $"Bereit. Aufbewahrung {settings.RetentionDays} Tage.{Suffix()}");
        }

        string what = string.Join(", ", running.Select(r => string.Create(
            CultureInfo.CurrentCulture,
            $"{r.Session.Destination} ({r.Recorded.TotalMinutes:0} min"
            + $"{(r.IsRecording ? string.Empty : ", Pause")})")));

        return string.Create(CultureInfo.CurrentCulture,
            $"Zeichnet auf: {what}.");

        string Suffix() =>
            _lastCleanupSummary.Length == 0 ? string.Empty : " " + _lastCleanupSummary;
    }

    /// <summary>Schreibt einen Protokolleintrag zur Aufzeichnung einer Sitzung.</summary>
    private static void Log(RuntimeComposition composition, SessionSnapshot session,
                            SessionOutcome outcome, string reason)
    {
        try
        {
            _ = composition.Log.Append(new SessionLogEntry
            {
                Operation = "recording.session",
                Outcome = outcome,
                Reason = reason,
                Trigger = SessionTrigger.Watcher,
                RemoteMaintenanceId = session.SessionId,
                WindowTitle = session.Destination,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Ein nicht geschriebener Protokolleintrag darf keine Aufzeichnung
            // kosten. Das Protokoll ist der Beleg, nicht der Vorgang.
        }
    }

    /// <summary>Was die Sitzungsbeobachtung gemeldet hat.</summary>
    /// <param name="Started">Beginn (<c>true</c>) oder Ende (<c>false</c>).</param>
    /// <param name="Session">Die Sitzung.</param>
    private readonly record struct RecordingCommand(bool Started, SessionSnapshot Session);
}
