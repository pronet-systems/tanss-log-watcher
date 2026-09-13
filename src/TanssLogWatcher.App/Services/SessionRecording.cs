using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Recording;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Recordings;

namespace TanssLogWatcher.App.Services;

/// <summary>
/// Die Aufzeichnung genau einer Sitzung — auf einem eigenen Strang.
/// </summary>
/// <remarks>
/// <para><b>Ein eigener Strang und nicht der Takt des Dienstes.</b> Die Zeitachse der Datei
/// hängt daran, wie gleichmässig getaktet wird; ein Dienst, der nebenbei noch aufräumt und
/// meldet, taktet nicht gleichmässig. Der Strang hier tut nichts anderes als: Fenster suchen,
/// Bild holen, schreiben, warten.</para>
///
/// <para><b>Die Zeitstempel kommen trotzdem von der Wanduhr</b> und nicht aus der Zahl der
/// Takte. Bleibt der Rechner einmal hängen, ist das Video an dieser Stelle ruckelig — aber
/// nicht falsch. Das ist die Reihenfolge, in der es darauf ankommt.</para>
///
/// <para><b>Hausregel 5.</b> Was hier schiefgeht, kostet die Aufzeichnung — nie die Sitzung.
/// Die Sitzung trägt die Arbeitszeit und geht ihren eigenen Weg in die Warteschlange; das
/// Video ist die Beigabe.</para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class SessionRecording : IDisposable
{
    private readonly SessionSnapshot _session;
    private readonly RecordingSection _settings;
    private readonly RecordingOptions _options;
    private readonly string _root;
    private readonly RecordingStore _store;
    private readonly IWindowGeometrySource _geometry;
    private readonly IScreenSource _screens;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _retention;
    private readonly TaskCompletionSource _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CancellationTokenSource _stop = new();
    private readonly SessionRecorder _recorder;
    private readonly List<string> _relativeFiles = [];

    private Thread? _thread;
    private long _ended;
    private long? _openEntry;
    private string _openPath = string.Empty;
    private TimeSpan _openAtRecorded;
    private bool _disposed;

    /// <summary>Rüstet die Aufzeichnung einer Sitzung.</summary>
    /// <param name="session">Die Sitzung.</param>
    /// <param name="settings">Der geprüfte Konfigurationsabschnitt.</param>
    /// <param name="root">Die Wurzel, unter der Aufzeichnungen liegen.</param>
    /// <param name="store">Die Buchführung.</param>
    /// <param name="geometry">Woher die Fenster samt Lage kommen.</param>
    /// <param name="screens">Woher die Bildschirme kommen.</param>
    /// <param name="clock">Die Uhr.</param>
    public SessionRecording(SessionSnapshot session, RecordingSection settings, string root,
                            RecordingStore store, IWindowGeometrySource geometry,
                            IScreenSource screens, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(screens);
        ArgumentNullException.ThrowIfNull(clock);

        _session = session;
        _settings = settings;
        _root = root;
        _store = store;
        _geometry = geometry;
        _screens = screens;
        _clock = clock;
        _retention = TimeSpan.FromDays(settings.RetentionDays);
        _options = ToOptions(settings);
        _recorder = new SessionRecorder(_options, PathForVideo);
    }

    /// <summary>Die Sitzung, die aufgezeichnet wird.</summary>
    public SessionSnapshot Session => _session;

    /// <summary>Die aufgezeichnete Zeit — ohne die Pausen.</summary>
    public TimeSpan Recorded => _recorder.Recorded;

    /// <summary>Läuft gerade eine Aufzeichnung, oder ist gerade Pause?</summary>
    public bool IsRecording => _recorder.IsRecording;

    /// <summary>Was zuletzt geschah — deutscher Klartext.</summary>
    public string LastReason { get; private set; } = "Vorbereitet.";

    /// <summary>Wartet auf das Ende des Strangs.</summary>
    public Task Completion => _done.Task;

    /// <summary>
    /// Bildet den Konfigurationsabschnitt auf die Stellschrauben der Aufzeichnung ab.
    /// </summary>
    /// <remarks>
    /// Ausdrücklich hier und nicht im Aufnahmeprojekt: Jenes soll sich ohne
    /// <c>config.json</c> prüfen lassen. Die Werte sind bereits von
    /// <see cref="ConfigValidator"/> in Grenzen gehalten worden.
    /// </remarks>
    /// <param name="settings">Der Abschnitt.</param>
    public static RecordingOptions ToOptions(RecordingSection settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new RecordingOptions
        {
            Scope = settings.CapturesScreen ? CaptureScope.Screen : CaptureScope.SessionWindows,
            FramesPerSecond = settings.FramesPerSecond,
            Heartbeat = TimeSpan.FromSeconds(settings.HeartbeatSeconds),
            MinimumFreeMegabytes = settings.MinimumFreeMegabytes,
        };
    }

    /// <summary>Startet den Strang.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Aufzeichnung " + _session.SessionId,
        };

        _thread.Start();
    }

    /// <summary>
    /// Meldet, dass die Sitzung beendet ist.
    /// </summary>
    /// <remarks>
    /// Es wird nicht sofort abgebrochen, sondern dem Strang gesagt, dass der nächste Takt der
    /// letzte ist. So schliesst er die Datei geordnet ab — ohne Abschluss hätte sie keinen
    /// Index und wäre unbrauchbar, und zwar ausgerechnet die mit dem jüngsten Inhalt.
    /// </remarks>
    public void MarkEnded() => Interlocked.Exchange(ref _ended, 1);

    /// <summary>Bricht ab und wartet kurz auf den Strang.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        MarkEnded();

        // Anderthalb Sekunden, damit die Datei ihren Abschluss bekommt - ohne ihn haette sie
        // keinen Index und waere unbrauchbar. Laenger nicht: Beim Herunterfahren wartet ein
        // Mensch, und die Sitzung selbst ist laengst in der Warteschlange.
        if (_thread is not null && !_done.Task.Wait(TimeSpan.FromMilliseconds(1500)))
        {
            _stop.Cancel();
        }

        _stop.Dispose();
        _recorder.Dispose();
    }

    /// <summary>
    /// Der Strang: Fenster suchen, Bild holen, schreiben, warten.
    /// </summary>
    private void Run()
    {
        TimeSpan gap = TimeSpan.FromSeconds(1.0 / _options.FramesPerSecond);

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                bool ended = Interlocked.Read(ref _ended) == 1;

                if (!Step(ended))
                {
                    break;
                }

                if (_stop.Token.WaitHandle.WaitOne(gap))
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Die Aufzeichnung ist verloren, die Sitzung nicht. Der Grund wird
            // gemeldet, damit niemand auf eine Datei wartet, die nicht kommt.
            LastReason = "Die Aufzeichnung ist abgebrochen: " + ex.Message;
        }
        finally
        {
            Finish();
            _done.TrySetResult();
        }
    }

    /// <summary>Ein Takt; <c>false</c>, wenn die Aufzeichnung danach vorbei ist.</summary>
    private bool Step(bool ended)
    {
        IReadOnlyList<WindowBox> windows = ended
            ? []
            : _geometry.WindowsOf(_session.ProcessId);

        bool running = _recorder.Tick(new RecordingInput(
            _clock.GetLocalNow(), windows, ended, RecordingGate.FreeMegabytes(_root),
            _screens.Screens()));

        if (_recorder.LastReason.Length > 0)
        {
            LastReason = _recorder.LastReason;
        }

        return running && !ended;
    }

    /// <summary>
    /// Liefert den Pfad der Aufzeichnung und trägt sie in die Buchführung ein.
    /// </summary>
    /// <remarks>
    /// Genau einmal je Sitzung gerufen: Eine Sitzung ergibt eine Datei. Eingetragen wird beim
    /// Beginn und nicht beim Ende — stürzt das Werkzeug mitten im Schreiben ab, bliebe die
    /// Datei sonst als Waise liegen, und niemand wüsste, zu welcher Sitzung sie gehört und wann
    /// sie zu löschen wäre.
    /// </remarks>
    private string PathForVideo()
    {
        CloseEntry();

        string relative = RecordingPaths.VideoFor(_session.StartedAt, _session.SessionId);
        string full = Path.Combine(_root, relative);

        _ = Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        _openEntry = _store.Begin(_session.SessionId, relative, 1, _retention);
        _openPath = full;
        _openAtRecorded = _recorder.Recorded;

        _relativeFiles.Add(relative);

        return full;
    }

    /// <summary>Schliesst den Eintrag des laufenden Abschnitts ab.</summary>
    private void CloseEntry()
    {
        if (_openEntry is not { } id)
        {
            return;
        }

        _openEntry = null;

        try
        {
            long bytes = File.Exists(_openPath) ? new FileInfo(_openPath).Length : 0;

            _ = _store.Finish(id, _recorder.Recorded - _openAtRecorded, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Hausregel 5: Ein nicht fortgeschriebener Eintrag kostet die Buchfuehrung ueber
            // die Groesse - nicht die Aufzeichnung. Die Frist steht bereits fest.
        }
    }

    /// <summary>Beendet geordnet und legt die Begleitdatei daneben.</summary>
    private void Finish()
    {
        try
        {
            _recorder.Stop();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastReason = "Beim Abschliessen der Datei ist etwas schiefgegangen: " + ex.Message;
        }

        CloseEntry();
        WriteManifest();
    }

    /// <summary>
    /// Schreibt die Begleitdatei neben die Videodateien.
    /// </summary>
    /// <remarks>
    /// Ohne sie wäre ein Video von zwanzig Minuten zu einer Sitzung von fünfundvierzig nicht zu
    /// erklären. Sie trägt ausserdem Gegenstelle und Techniker — das, was bewusst nicht im
    /// Dateipfad steht, weil ein Pfad in Sicherungsläufe und Suchindizes wandert und diese
    /// Datei mit der Aufzeichnung zusammen gelöscht wird.
    /// </remarks>
    private void WriteManifest()
    {
        if (_relativeFiles.Count == 0)
        {
            return;
        }

        try
        {
            string folder = Path.Combine(
                _root, RecordingPaths.FolderFor(_session.StartedAt, _session.SessionId));

            DateTimeOffset started = _session.StartedAt;

            new RecordingManifest
            {
                SessionId = _session.SessionId,
                Destination = _session.Destination,
                Application = _session.ProfileName,
                Workstation = Environment.MachineName,
                Technician = Environment.UserName,
                StartedAt = started,
                EndedAt = _session.EndedAt ?? _clock.GetLocalNow(),
                RecordedSeconds = (long)_recorder.Recorded.TotalSeconds,
                Pauses = [.. _recorder.Pauses.Select(
                    p => new ManifestPause(p.StartedAt, (long)p.Length.TotalSeconds))],
                Files = [.. _relativeFiles.Select(f => Path.GetFileName(f) ?? f)],
                Canvas = Describe(),
                Scope = _settings.Scope,
                ScreenChanges = _recorder.ScreenChanges,
                FramesPerSecond = _options.FramesPerSecond,
                DeleteAfter = started + _retention,
                LegalBasis = _settings.LegalBasis ?? string.Empty,
                LegalReference = _settings.LegalReference ?? string.Empty,
            }.Save(Path.Combine(folder, RecordingPaths.ManifestName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Hausregel 5: Eine fehlende Begleitdatei kostet die Erklaerung, nicht das Video.
            LastReason = "Die Begleitdatei liess sich nicht schreiben: " + ex.Message;
        }
    }

    /// <summary>Die Abmessungen der letzten Leinwand, für die Begleitdatei.</summary>
    private string Describe() =>
        _recorder.Canvas is { } canvas
            ? string.Create(CultureInfo.InvariantCulture, $"{canvas.Width}×{canvas.Height}")
            : string.Empty;
}
