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
    private readonly Func<string> _pathFor;
    private readonly Dictionary<nint, WindowCapture> _captures = [];

    private CaptureDevice? _device;
    private WindowCapture? _screen;
    private nint _screenHandle;
    private VideoFile? _file;
    private byte[] _canvas = [];
    private byte[] _scratch = [];
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
    /// <param name="pathFor">
    /// Liefert den Dateipfad der Aufzeichnung. Die Benennung gehört nicht hierher: Wie eine
    /// Aufzeichnung heisst und wo sie liegt, entscheidet die Ablage. Genau einmal je Sitzung
    /// gerufen — eine Sitzung ergibt eine Datei.
    /// </param>
    public SessionRecorder(RecordingOptions options, Func<string> pathFor)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pathFor);

        options.Validate();

        _options = options;
        _pathFor = pathFor;
        _director = new RecordingDirector(options);
        _clock = new RecordingClock(options.FramesPerSecond);
        _cadence = new FrameCadence(options.FramesPerSecond, options.Heartbeat);
    }

    /// <summary>Die Pausen, für die Begleitdatei.</summary>
    public IReadOnlyList<RecordingPause> Pauses => _clock.Pauses;

    /// <summary>Die aufgezeichnete Zeit — ohne die Pausen.</summary>
    public TimeSpan Recorded => _clock.Duration;

    /// <summary>
    /// Die geschriebenen Dateien.
    /// </summary>
    /// <remarks>
    /// Seit eine Sitzung genau eine Datei ergibt, steht hier höchstens eine. Es bleibt trotzdem
    /// eine Liste: Die Begleitdatei führt sie als Liste, und eine bestehende
    /// <c>sitzung.json</c> wäre sonst nicht mehr lesbar.
    /// </remarks>
    public IReadOnlyList<string> Files { get; } = new List<string>();

    /// <summary>Läuft gerade eine Aufzeichnung?</summary>
    public bool IsRecording => _director.IsRecording;

    /// <summary>Ist die Aufzeichnung vorbei?</summary>
    public bool IsFinished => _director.IsFinished;

    /// <summary>Wie oft das Bild einem anderen Bildschirm gefolgt ist.</summary>
    public int ScreenChanges => _director.CanvasMoves;

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
            // Bei JEDEM Takt und nicht nur beim Start: Ein Dialog, der mitten in der Sitzung
            // aufgeht, bekommt sonst nie eine Aufnahme und erscheint im Video ueberhaupt
            // nicht. Der Abgleich ist ein Mengenvergleich und kostet nichts.
            Attach(input.Windows);
            PullAndWrite(input.Now, input.Windows);
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

        _screen?.Dispose();
        _screen = null;
        _screenHandle = 0;

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

            case RecordingAction.MoveCanvas:
                // Kein Dateiwechsel: Die Leinwand hat nur einen anderen Ursprung. Der Takt
                // wird zurueckgesetzt, damit das erste Bild an der neuen Stelle sofort
                // durchgeht - es zeigt etwas voellig anderes als das davor.
                _cadence.Reset();
                _anythingChanged = true;
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

        Canvas = (canvas.Width, canvas.Height);
        string path = _pathFor();

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

        if (_options.Scope is CaptureScope.Screen)
        {
            AttachScreen();
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
    /// Hängt die Aufnahme an den Bildschirm, auf dem die Sitzung liegt.
    /// </summary>
    /// <remarks>
    /// Wechselt die Sitzung den Bildschirm, wird die alte Aufnahme gelöst und eine neue
    /// begonnen — die Datei bleibt dieselbe, nur die Quelle ist eine andere. Schlägt das fehl
    /// (ein eben abgemeldeter Bildschirm), bleibt das Bild schwarz, bis es wieder geht:
    /// Hausregel 5, ein Fehler kostet Bilder und nicht die Aufzeichnung.
    /// </remarks>
    private void AttachScreen()
    {
        if (_director.Screen is not { } screen || screen.Handle == 0)
        {
            return;
        }

        if (_screen is not null && _screenHandle == screen.Handle)
        {
            return;
        }

        _screen?.Dispose();
        _screen = null;

        try
        {
            _screen = WindowCapture.StartScreen(_device!, screen.Handle);
            _screenHandle = screen.Handle;
        }
        catch (RecordingException ex)
        {
            LastReason = "Dieser Bildschirm liess sich nicht aufnehmen: " + ex.Message;
            _screenHandle = 0;
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
    /// <param name="windows">Die Fensterlage dieses Taktes.</param>
    private void PullAndWrite(DateTimeOffset now, IReadOnlyList<WindowBox> windows)
    {
        if (_director.Canvas is not { } canvas || _file is null)
        {
            return;
        }

        bool changed = Compose(canvas, windows);
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
    private bool Compose(CanvasLayout canvas, IReadOnlyList<WindowBox> windows)
    {
        Array.Clear(_canvas);

        if (_options.Scope is CaptureScope.Screen)
        {
            return ComposeScreen(canvas);
        }

        bool any = false;

        foreach (WindowPlacement placement in canvas.PlaceAll(windows))
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

    /// <summary>
    /// Setzt das Bild des ganzen Bildschirms auf die Leinwand.
    /// </summary>
    /// <remarks>
    /// Die Leinwand ist der Bildschirm, also liegt das Bild bündig bei 0/0. Ist der Bildschirm
    /// grösser als die Leinwand — das kommt vor, wenn die Sitzung mitten im Betrieb auf einen
    /// grösseren Bildschirm wandert —, wird beschnitten statt skaliert; die Bildgrösse einer
    /// laufenden Datei lässt sich nicht ändern, und eine zweite Datei ist genau das, was hier
    /// vermieden werden soll.
    /// </remarks>
    private bool ComposeScreen(CanvasLayout canvas)
    {
        if (_screen is null || _screen.TryCopyLatest(ref _scratch) is not { } size)
        {
            return false;
        }

        Blit(canvas, new WindowPlacement(_screenHandle, 0, 0, size.Width, size.Height), size);
        return true;
    }

    /// <summary>
    /// Kopiert ein Fensterbild an seinen Platz — Zeile für Zeile, nie skaliert.
    /// </summary>
    /// <remarks>
    /// <b>Beschnitten wird an allen vier Seiten, auch links und oben.</b> Hier stand einmal
    /// <c>placement.X &lt; 0 || placement.Y &lt; 0 -&gt; return</c>, und das liess ein Fenster
    /// vollständig verschwinden, sobald es über den linken oder oberen Rand ragte. Gemessen:
    /// Ein maximiertes Fenster meldet auf einem Bildschirm von 2880×1800 das Rechteck
    /// (−13,−13) bei 2906×1730 — sein unsichtbarer Anfassrahmen liegt ausserhalb. Mit der
    /// Bildschirmfläche als Leinwand wäre das Video einer jeden Fernwartung mit maximiertem
    /// Fenster durchgehend schwarz gewesen, ohne eine einzige Fehlermeldung.
    /// </remarks>
    private void Blit(CanvasLayout canvas, WindowPlacement placement, FrameSize size)
    {
        int canvasStride = canvas.Width * 4;
        int sourceStride = size.Width * 4;

        // Was links oder oben ausserhalb liegt, wird im QUELLBILD uebersprungen - das Bild
        // rueckt dadurch nicht, es wird nur der sichtbare Teil kopiert.
        int sourceX = placement.X < 0 ? -placement.X : 0;
        int sourceY = placement.Y < 0 ? -placement.Y : 0;

        int targetX = placement.X + sourceX;
        int targetY = placement.Y + sourceY;

        int rows = Math.Min(size.Height - sourceY, canvas.Height - targetY);
        int columns = Math.Min(size.Width - sourceX, canvas.Width - targetX);

        if (rows <= 0 || columns <= 0)
        {
            return;
        }

        int bytes = columns * 4;

        for (int row = 0; row < rows; row++)
        {
            int from = ((sourceY + row) * sourceStride) + (sourceX * 4);
            int to = ((targetY + row) * canvasStride) + (targetX * 4);

            _scratch.AsSpan(from, bytes).CopyTo(_canvas.AsSpan(to, bytes));
        }
    }
}
