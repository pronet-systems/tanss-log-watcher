using System.Runtime.Versioning;

namespace TanssLogWatcher.Recording;

/// <summary>
/// Die Aufzeichnung einer Sitzung: Fenster fangen, zusammensetzen, schreiben.
/// </summary>
/// <remarks>
/// <para><b>Sie führt aus, was der <see cref="RecordingDirector"/> beschliesst.</b> Hier steht
/// kein <c>if</c>, das über Beginn, Pause oder Abschnittswechsel entscheidet — das ist die
/// Grenze, an der sich die Aufzeichnung ohne Bildschirm prüfen lässt. Hier steht nur, wie
/// beschlossene Handlungen ausgeführt werden.</para>
///
/// <para><b>Ein Video aus mehreren Fenstern.</b> Jedes Fenster der Sitzung wird für sich
/// aufgenommen — gemessen: Die Fensteraufnahme von Windows liefert besessene Unterfenster nicht
/// mit — und dann an seinen Platz auf einer gemeinsamen Leinwand kopiert. Der Platz ergibt sich
/// aus der Lage auf dem Bildschirm; das Bild sieht aus wie der Bildschirm, nur ohne alles, was
/// nicht zur Sitzung gehört.</para>
///
/// <para><b>Ein Fenster, das gerade kein Bild liefert, hinterlässt Schwarz</b> und nicht sein
/// letztes Bild. Ein stehengebliebenes Bild behauptete, dort sei noch etwas zu sehen.</para>
///
/// <para><b>Hausregel 5 gilt auch hier.</b> Ein misslungenes Bild kostet ein Bild, nie die
/// Aufzeichnung — und eine misslungene Aufzeichnung kostet die Aufzeichnung, nie die Sitzung.
/// Die Sitzung trägt die Arbeitszeit; das Video ist die Beigabe.</para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class SessionRecorder : IDisposable
{
    private readonly RecordingOptions _options;
    private readonly RecordingDirector _director;
    private readonly RecordingClock _clock;
    private readonly FrameCadence _cadence;
    private readonly Func<int, string> _pathForSegment;
    private readonly Dictionary<nint, WindowCapture> _captures = [];

    private CaptureDevice? _device;
    private VideoFile? _file;
    private byte[] _canvas = [];
    private byte[] _scratch = [];
    private int _segment;
    private bool _anythingChanged;

    /// <summary>
    /// Der letzte bekannte Zeitpunkt.
    /// </summary>
    /// <remarks>
    /// Gebraucht von <see cref="Stop"/>: Endet die Aufzeichnung während einer Pause, wäre die
    /// Pause sonst nie eingetragen — sie wird erst beim Fortsetzen abgeschlossen. In der
    /// Begleitdatei fehlte dann genau die letzte, und die ist oft die längste.
    /// </remarks>
    private DateTimeOffset _lastNow;

    /// <summary>Baut die Aufzeichnung für eine Sitzung.</summary>
    /// <param name="options">Die geprüften Stellschrauben.</param>
    /// <param name="pathForSegment">
    /// Liefert den Dateipfad für den n-ten Abschnitt, beginnend bei 1. Die Benennung gehört
    /// nicht hierher: Wie eine Aufzeichnung heisst und wo sie liegt, entscheidet die Ablage.
    /// </param>
    public SessionRecorder(RecordingOptions options, Func<int, string> pathForSegment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pathForSegment);

        options.Validate();

        _options = options;
        _pathForSegment = pathForSegment;
        _director = new RecordingDirector(options);
        _clock = new RecordingClock(options.FramesPerSecond);
        _cadence = new FrameCadence(options.FramesPerSecond, options.Heartbeat);
    }

    /// <summary>Die Pausen, für die Begleitdatei.</summary>
    public IReadOnlyList<RecordingPause> Pauses => _clock.Pauses;

    /// <summary>Die aufgezeichnete Zeit — ohne die Pausen.</summary>
    public TimeSpan Recorded => _clock.Duration;

    /// <summary>Die geschriebenen Dateien, in der Reihenfolge ihrer Entstehung.</summary>
    public IReadOnlyList<string> Files { get; } = new List<string>();

    /// <summary>Läuft gerade eine Aufzeichnung?</summary>
    public bool IsRecording => _director.IsRecording;

    /// <summary>Ist die Aufzeichnung vorbei?</summary>
    public bool IsFinished => _director.IsFinished;

    /// <summary>Was zuletzt geschah — deutscher Klartext für Anzeige und Protokoll.</summary>
    public string LastReason { get; private set; } = string.Empty;

    /// <summary>
    /// Die Abmessungen der zuletzt begonnenen Datei; <c>null</c>, solange keine begonnen wurde.
    /// </summary>
    /// <remarks>
    /// Ausdrücklich hier gemerkt und nicht beim Direktor erfragt: Jener vergisst seine Leinwand
    /// beim Beenden, und die Begleitdatei wird erst danach geschrieben.
    /// </remarks>
    public (int Width, int Height)? Canvas { get; private set; }

    /// <summary>
    /// Ein Takt: entscheiden lassen, ausführen, Bild schreiben.
    /// </summary>
    /// <param name="input">Was dieser Takt vorfindet.</param>
    /// <returns><c>true</c>, solange die Aufzeichnung weiterläuft.</returns>
    public bool Tick(RecordingInput input)
    {
        _lastNow = input.Now;

        RecordingDecision decision = _director.Decide(input);

        if (decision.Action is not RecordingAction.None)
        {
            LastReason = decision.Reason;
            Apply(decision, input.Now);
        }

        if (_director.IsRecording && _file is not null)
        {
            PullAndWrite(input.Now);
        }

        return !_director.IsFinished;
    }

    /// <summary>
    /// Beendet die Aufzeichnung geordnet.
    /// </summary>
    /// <remarks>
    /// Der Weg, den das Herunterfahren nimmt. Ohne ihn bliebe die letzte Datei ohne Index —
    /// also unbrauchbar, und zwar genau die mit dem jüngsten Inhalt.
    /// </remarks>
    public void Stop()
    {
        // Eine offene Pause wird geschlossen, bevor irgendetwas anderes geschieht. Sonst
        // fehlte sie in der Begleitdatei, und die Aufstellung der Pausen waere unvollstaendig
        // - ausgerechnet um die letzte, die haeufig die laengste ist.
        _clock.Resume(_lastNow);

        CloseFile();

        foreach (WindowCapture capture in _captures.Values)
        {
            capture.Dispose();
        }

        _captures.Clear();

        _device?.Dispose();
        _device = null;
    }

    /// <summary>Beendet und räumt auf.</summary>
    public void Dispose() => Stop();

    private void Apply(RecordingDecision decision, DateTimeOffset now)
    {
        switch (decision.Action)
        {
            case RecordingAction.Start:
                _device ??= CaptureDevice.Create();
                OpenFile();
                Attach(decision.Windows);
                _clock.Start(now);
                break;

            case RecordingAction.Pause:
                _clock.Pause(now);
                _cadence.Reset();
                break;

            case RecordingAction.Resume:
                _clock.Resume(now);
                Attach(decision.Windows);
                break;

            case RecordingAction.RollSegment:
                _clock.Resume(now);
                CloseFile();
                OpenFile();
                Attach(decision.Windows);
                _cadence.Reset();
                break;

            case RecordingAction.Stop:
                _lastNow = now;
                Stop();
                break;

            default:
                break;
        }
    }

    private void OpenFile()
    {
        if (_director.Canvas is not { } canvas)
        {
            return;
        }

        _segment++;
        Canvas = (canvas.Width, canvas.Height);
        string path = _pathForSegment(_segment);

        _file = VideoFile.Create(path, canvas.Width, canvas.Height, _options.FramesPerSecond);
        ((List<string>)Files).Add(path);

        int needed = canvas.Width * canvas.Height * 4;

        if (_canvas.Length < needed)
        {
            _canvas = new byte[needed];
        }
    }

    private void CloseFile()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            _file.Complete();
        }
        finally
        {
            _file.Dispose();
            _file = null;
        }
    }

    /// <summary>
    /// Hängt die Aufnahme an die Fenster der Entscheidung — und löst, was nicht mehr dazugehört.
    /// </summary>
    /// <remarks>
    /// Aufnahmen bleiben über einen Abschnittswechsel hinweg bestehen. Sie neu anzulegen kostete
    /// jedes Mal die ersten Bilder: Der Bildbeutel füllt sich nicht sofort, und genau in dem
    /// Augenblick beginnt die neue Datei.
    /// </remarks>
    private void Attach(IReadOnlyList<WindowBox> windows)
    {
        if (_device is null)
        {
            return;
        }

        HashSet<nint> wanted = [.. windows.Select(w => w.Handle)];

        foreach (nint gone in _captures.Keys.Where(h => !wanted.Contains(h)).ToList())
        {
            _captures[gone].Dispose();
            _ = _captures.Remove(gone);
        }

        foreach (nint handle in wanted)
        {
            if (_captures.ContainsKey(handle))
            {
                continue;
            }

            try
            {
                _captures[handle] = WindowCapture.Start(_device, handle);
            }
            catch (RecordingException)
            {
                // Hausregel 5: Dieses eine Fenster laesst sich nicht aufnehmen - ein Programm
                // mit erhoehten Rechten etwa. Die uebrigen werden trotzdem aufgezeichnet; sein
                // Platz auf der Leinwand bleibt schwarz, und das ist ehrlicher, als die ganze
                // Aufzeichnung ausfallen zu lassen.
            }
        }
    }

    /// <summary>
    /// Holt die Bilder, setzt sie zusammen und schreibt — wenn der Takt es zulässt.
    /// </summary>
    /// <remarks>
    /// Der Takt wird an der <b>aufgezeichneten Wanduhr</b> gemessen und nicht an der Zahl der
    /// geschriebenen Bilder. Am Bildzähler gemessen stünde die Uhr, sobald nichts mehr
    /// geschrieben wird — der Herzschlag käme dann nie, und ein stehender Bildschirm
    /// hinterliesse ein einziges Bild für eine Viertelstunde Lesen.
    /// </remarks>
    /// <param name="now">Die Wanduhr dieses Taktes.</param>
    private void PullAndWrite(DateTimeOffset now)
    {
        if (_director.Canvas is not { } canvas || _file is null)
        {
            return;
        }

        bool changed = Compose(canvas);
        _anythingChanged |= changed;

        TimeSpan elapsed = _clock.Elapsed(now);
        FrameReason reason = _cadence.Decide(elapsed, _anythingChanged);

        if (reason is FrameReason.Skip)
        {
            return;
        }

        try
        {
            TimeSpan timestamp = _clock.NextFrameTimestamp(now);

            _file.Write(_canvas, timestamp,
                        TimeSpan.FromTicks(TimeSpan.TicksPerSecond / _options.FramesPerSecond));

            _cadence.Accepted(elapsed);
            _anythingChanged = false;
        }
        catch (Exception ex) when (ex is RecordingException or InvalidOperationException)
        {
            // Hausregel 5: Ein misslungenes Bild kostet ein Bild, nie die Aufzeichnung.
            LastReason = "Ein Bild liess sich nicht schreiben: " + ex.Message;
        }
    }

    /// <summary>
    /// Setzt die Fenster auf der Leinwand zusammen.
    /// </summary>
    /// <returns><c>true</c>, wenn sich mindestens ein Fenster gemeldet hat.</returns>
    private bool Compose(CanvasLayout canvas)
    {
        Array.Clear(_canvas);

        bool any = false;

        foreach (WindowPlacement placement in canvas.Placements)
        {
            if (!_captures.TryGetValue(placement.Handle, out WindowCapture? capture))
            {
                continue;
            }

            if (capture.TryCopyLatest(ref _scratch) is not { } size)
            {
                // Kein neues Bild von diesem Fenster. Sein Platz bleibt schwarz - ein
                // stehengebliebenes Bild behauptete, dort sei noch etwas zu sehen.
                continue;
            }

            any = true;
            Blit(canvas, placement, size);
        }

        return any;
    }

    /// <summary>Kopiert ein Fensterbild an seinen Platz — Zeile für Zeile, nie skaliert.</summary>
    private void Blit(CanvasLayout canvas, WindowPlacement placement, FrameSize size)
    {
        int canvasStride = canvas.Width * 4;
        int sourceStride = size.Width * 4;

        int rows = Math.Min(size.Height, canvas.Height - placement.Y);
        int columns = Math.Min(size.Width, canvas.Width - placement.X);

        if (rows <= 0 || columns <= 0 || placement.X < 0 || placement.Y < 0)
        {
            return;
        }

        int bytes = columns * 4;

        for (int row = 0; row < rows; row++)
        {
            int from = row * sourceStride;
            int to = ((placement.Y + row) * canvasStride) + (placement.X * 4);

            _scratch.AsSpan(from, bytes).CopyTo(_canvas.AsSpan(to, bytes));
        }
    }
}
