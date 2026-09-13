namespace TanssLogWatcher.Recording;

/// <summary>
/// Was der Aufzeichnung als Nächstes zu tun ist.
/// </summary>
public enum RecordingAction
{
    /// <summary>Nichts. Der Normalfall zwischen zwei Takten.</summary>
    None = 0,

    /// <summary>Eine Aufzeichnung beginnen.</summary>
    Start,

    /// <summary>Anhalten — es gibt gerade nichts zu sehen.</summary>
    Pause,

    /// <summary>Fortsetzen.</summary>
    Resume,

    /// <summary>
    /// Die Leinwand auf einen anderen Bildschirm schieben.
    /// </summary>
    /// <remarks>
    /// Kein Dateiwechsel: Die Bildgrösse liegt in der laufenden Datei fest, der Ursprung nicht.
    /// </remarks>
    MoveCanvas,

    /// <summary>Endgültig beenden.</summary>
    Stop,
}

/// <summary>Was der Direktor in einem Takt beschlossen hat.</summary>
/// <param name="Action">Die Handlung.</param>
/// <param name="Windows">Die Fenster, die sie betrifft; leer bei <see cref="RecordingAction.Pause"/>.</param>
/// <param name="Reason">Warum — deutscher Klartext für Protokoll und Begleitdatei.</param>
public sealed record RecordingDecision(
    RecordingAction Action,
    IReadOnlyList<WindowBox> Windows,
    string Reason)
{
    /// <summary>Ein Takt ohne Veränderung.</summary>
    public static RecordingDecision Nothing { get; } =
        new(RecordingAction.None, [], string.Empty);
}

/// <summary>Was der Direktor je Takt vorgesetzt bekommt.</summary>
/// <param name="Now">Die Wanduhr.</param>
/// <param name="Windows">Die gerade sichtbaren Fenster der Sitzung, mit Geometrie.</param>
/// <param name="SessionEnded">Hat die Sitzungsbeobachtung das Ende gemeldet?</param>
/// <param name="FreeMegabytes">Wie viel Platz auf dem Zieldatenträger noch frei ist.</param>
/// <param name="Screens">
/// Die angeschlossenen Bildschirme. Leer heisst: nicht ermittelbar — dann gilt die Hüllfläche
/// der Fenster wie früher. Sie kommen von aussen herein wie die Fenster, damit der Direktor
/// weiterhin kein Windows kennt und sich ohne Bildschirm prüfen lässt; das Handle darin ist für
/// ihn eine Zahl ohne Bedeutung und dient allein dem Rekorder, der den Bildschirm aufnimmt.
/// </param>
public readonly record struct RecordingInput(
    DateTimeOffset Now,
    IReadOnlyList<WindowBox> Windows,
    bool SessionEnded,
    long FreeMegabytes,
    IReadOnlyList<ScreenInfo> Screens)
{
    /// <summary>Ein Takt ohne Kenntnis der Bildschirme.</summary>
    /// <param name="now">Die Wanduhr.</param>
    /// <param name="windows">Die sichtbaren Fenster.</param>
    /// <param name="sessionEnded">Ist die Sitzung beendet?</param>
    /// <param name="freeMegabytes">Freier Platz.</param>
    public RecordingInput(DateTimeOffset now, IReadOnlyList<WindowBox> windows,
                          bool sessionEnded, long freeMegabytes)
        : this(now, windows, sessionEnded, freeMegabytes, [])
    {
    }
}

/// <summary>
/// Der Direktor: Er entscheidet alles und ruft nichts auf.
/// </summary>
/// <remarks>
/// <para><b>Die Prüfgrenze verläuft hier.</b> In diesem Projekt gibt es genau zwei Sorten von
/// Bausteinen: solche, die entscheiden, und solche, die etwas tun. Der Direktor entscheidet und
/// kennt kein Windows, keine Grafikkarte und keine Datei; der Rekorder tut und entscheidet
/// nichts. Ohne diesen Schnitt liesse sich die Aufzeichnung nur mit einem Bildschirm prüfen,
/// und das hiesse: gar nicht.</para>
///
/// <para><b>Eine Sitzung, eine Datei.</b> Früher wechselte er die Datei aus zwei Gründen: nach
/// einer eingestellten Zeit, und sobald die Fenster nicht mehr auf die Leinwand passten. Beides
/// ist fort. Im Betrieb gemessen, was der zweite Grund anrichtete: Eine Fernwartung von
/// neunzehn Sekunden ergab drei Dateien, weil ein Remotedesktop-Fenster beim Verbindungsaufbau
/// zweimal seine Grösse ändert. Die Leinwand ist jetzt die Bildschirmfläche und passt deshalb
/// immer; wandert die Sitzung auf einen anderen Bildschirm, wandert nur der Ursprung mit.</para>
///
/// <para><b>Er führt einen Zustand.</b> Anders als eine reine Rechenvorschrift merkt er sich,
/// ob gerade aufgezeichnet wird, wann die Fenster verschwunden sind und seit wann die Sitzung
/// auf einem anderen Bildschirm liegt. Genau diese Angaben entscheiden über Pause, Wechsel und
/// Ende — und sie aus der Umgebung neu zu erraten wäre die Sorte Doppelbuchführung, die
/// irgendwann auseinanderläuft.</para>
///
/// <para><b>Die Schonfrist ist kein Komfort.</b> Ein Fenster ist beim Verschieben zwischen zwei
/// Bildschirmen für den Bruchteil einer Sekunde ohne brauchbare Geometrie. Ohne Schonfrist
/// entstünde daraus eine Pause von einem Takt — und im Bericht an den Kunden eine
/// Unterbrechung, die es nie gab.</para>
/// </remarks>
public sealed class RecordingDirector
{
    private readonly RecordingOptions _options;

    private DirectorState _state = DirectorState.NotStarted;
    private CanvasLayout? _canvas;
    private DateTimeOffset? _windowsGoneSince;
    private ScreenInfo? _pendingScreen;
    private DateTimeOffset? _pendingSince;
    private DateTimeOffset? _lastMove;

    /// <summary>Baut den Direktor.</summary>
    /// <param name="options">Die geprüften Stellschrauben.</param>
    public RecordingDirector(RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <summary>Läuft gerade eine Aufzeichnung?</summary>
    public bool IsRecording => _state is DirectorState.Recording;

    /// <summary>Ist die Aufzeichnung angehalten?</summary>
    public bool IsPaused => _state is DirectorState.Paused;

    /// <summary>Ist die Aufzeichnung endgültig vorbei?</summary>
    public bool IsFinished => _state is DirectorState.Stopped;

    /// <summary>Die Leinwand der laufenden Datei; <c>null</c>, solange keine läuft.</summary>
    public CanvasLayout? Canvas => _canvas;

    /// <summary>
    /// Wie oft die Leinwand auf einen anderen Bildschirm geschoben wurde.
    /// </summary>
    /// <remarks>Gehört in die Begleitdatei: Sie erklärt einen Sprung im Bild.</remarks>
    public int CanvasMoves { get; private set; }

    /// <summary>
    /// Der Bildschirm, auf dem die Sitzung gerade liegt; <c>null</c>, solange keiner bekannt ist.
    /// </summary>
    /// <remarks>
    /// Der Direktor benutzt davon nur die Lage. Das Handle gibt er weiter, damit der Rekorder im
    /// Bildschirmbetrieb weiss, welchen Bildschirm er aufnehmen soll — er selbst kann damit
    /// nichts anfangen und soll es auch nicht.
    /// </remarks>
    public ScreenInfo? Screen { get; private set; }

    /// <summary>
    /// Ein Takt.
    /// </summary>
    /// <remarks>
    /// Die Reihenfolge der Prüfungen ist nicht beliebig. Das Ende der Sitzung schlägt alles
    /// andere, weil danach nichts mehr aufzuzeichnen ist. Der Platzmangel kommt als Zweites:
    /// Eine volle Platte nimmt auch der Warteschlange den Platz, und die trägt die
    /// Arbeitszeit — die Aufzeichnung ist das, was nachgeben muss.
    /// </remarks>
    /// <param name="input">Was dieser Takt vorfindet.</param>
    /// <returns>Die Handlung samt Begründung.</returns>
    public RecordingDecision Decide(RecordingInput input)
    {
        ArgumentNullException.ThrowIfNull(input.Windows);
        ArgumentNullException.ThrowIfNull(input.Screens);

        if (_state is DirectorState.Stopped)
        {
            return RecordingDecision.Nothing;
        }

        if (input.SessionEnded)
        {
            return Stop("Die Sitzung ist beendet.");
        }

        if (input.FreeMegabytes < _options.MinimumFreeMegabytes)
        {
            return Stop(
                $"Auf dem Zieldatenträger sind nur noch {input.FreeMegabytes} MB frei; "
                + $"unterhalb von {_options.MinimumFreeMegabytes} MB wird nicht mehr "
                + "aufgezeichnet. Die Warteschlange braucht den Platz dringender — sie trägt "
                + "die Arbeitszeit.");
        }

        List<WindowBox> visible = [.. input.Windows.Where(w => w.HasArea)];

        return _state switch
        {
            DirectorState.NotStarted => StartIfPossible(input, visible),
            DirectorState.Recording => WhileRecording(input, visible),
            DirectorState.Paused => WhilePaused(input, visible),
            _ => RecordingDecision.Nothing,
        };
    }

    private RecordingDecision StartIfPossible(RecordingInput input, List<WindowBox> visible)
    {
        if (CanvasLayout.ForScreens(visible, [.. input.Screens.Select(s => s.Box)])
            is not { } canvas)
        {
            // Noch kein Fenster mit Flaeche - etwa weil die Fernwartung erst startet. Es wird
            // nichts angelegt: eine leere Datei waere eine Aufzeichnung, die es nicht gibt.
            return RecordingDecision.Nothing;
        }

        _canvas = canvas;
        _state = DirectorState.Recording;
        _windowsGoneSince = null;
        Screen = Nearest(input.Screens, canvas);

        return new RecordingDecision(RecordingAction.Start, visible,
            $"Aufzeichnung begonnen, {canvas.Width}×{canvas.Height} Bildpunkte, "
            + $"{Describe(visible.Count)}.");
    }

    private RecordingDecision WhileRecording(RecordingInput input, List<WindowBox> visible)
    {
        if (visible.Count == 0)
        {
            _windowsGoneSince ??= input.Now;

            if (input.Now - _windowsGoneSince.Value < _options.PauseGrace)
            {
                // Noch in der Schonfrist: vermutlich ein Fenster im Umzug.
                return RecordingDecision.Nothing;
            }

            _state = DirectorState.Paused;
            _pendingScreen = null;
            _pendingSince = null;

            return new RecordingDecision(RecordingAction.Pause, [],
                "Angehalten: Kein Fenster der Sitzung ist gerade sichtbar — minimiert oder "
                + "geschlossen. Die Zeit der Pause zählt nicht zur Aufzeichnung.");
        }

        _windowsGoneSince = null;

        return FollowScreen(input, visible);
    }

    private RecordingDecision WhilePaused(RecordingInput input, List<WindowBox> visible)
    {
        if (visible.Count == 0)
        {
            return RecordingDecision.Nothing;
        }

        _windowsGoneSince = null;
        _state = DirectorState.Recording;
        _pendingScreen = null;
        _pendingSince = null;
        _lastMove = input.Now;

        return new RecordingDecision(RecordingAction.Resume, visible,
            "Fortgesetzt: Die Fenster sind wieder da.");
    }

    /// <summary>
    /// Schiebt die Leinwand nach, wenn die Sitzung auf einen anderen Bildschirm gewandert ist.
    /// </summary>
    /// <remarks>
    /// <para><b>Mit Beharrungszeit und Sperrfrist, und beides aus einem Grund.</b> Ein Fenster,
    /// das genau auf der Grenze zweier Bildschirme liegt, gehört bei jedem Takt ein bisschen
    /// anders zugeordnet. Ohne Beharrungszeit spränge das Bild im Sekundentakt hin und her, und
    /// das Video wäre unbrauchbar — schlimmer als eine zweite Datei.</para>
    ///
    /// <para>Entschieden wird über die <b>Fläche</b> und nicht über ein Hauptfenster: Wer das
    /// erste Fenster nähme, folgte einem Anmeldedialog, der längst geschlossen ist; wer den
    /// Tastaturfokus nähme, folgte dem Techniker in sein Mailfenster. Die Fläche ist das, was
    /// man sieht.</para>
    /// </remarks>
    private RecordingDecision FollowScreen(RecordingInput input, List<WindowBox> visible)
    {
        if (_canvas is not { } canvas || input.Screens.Count == 0)
        {
            return RecordingDecision.Nothing;
        }

        if (_lastMove is { } last && input.Now - last < _options.ScreenSwitchLockout)
        {
            // Sperrfrist nach einem Wechsel: Erst ankommen, dann weitersehen.
            return RecordingDecision.Nothing;
        }

        long total = visible.Sum(w => (long)w.Width * w.Height);

        if (total <= 0)
        {
            return RecordingDecision.Nothing;
        }

        ScreenInfo? best = null;
        long bestOverlap = 0;

        foreach (ScreenInfo screen in input.Screens.Where(s => s.Box.HasArea))
        {
            long overlap = visible.Sum(screen.Box.Overlap);

            if (overlap > bestOverlap)
            {
                best = screen;
                bestOverlap = overlap;
            }
        }

        if (best is not { } target
            || (target.Box.Left == canvas.OriginLeft && target.Box.Top == canvas.OriginTop))
        {
            _pendingScreen = null;
            _pendingSince = null;
            return RecordingDecision.Nothing;
        }

        if ((double)bestOverlap / total < _options.ScreenSwitchShare)
        {
            // Die Sitzung liegt noch zu gross zur Haelfte auf dem bisherigen Bildschirm.
            _pendingScreen = null;
            _pendingSince = null;
            return RecordingDecision.Nothing;
        }

        if (_pendingScreen != target)
        {
            _pendingScreen = target;
            _pendingSince = input.Now;
            return RecordingDecision.Nothing;
        }

        if (input.Now - (_pendingSince ?? input.Now) < _options.ScreenSwitchDelay)
        {
            return RecordingDecision.Nothing;
        }

        _canvas = canvas.MovedTo(target.Box.Left, target.Box.Top);
        _pendingScreen = null;
        _pendingSince = null;
        _lastMove = input.Now;
        Screen = target;
        CanvasMoves++;

        string note = target.Box.Width > canvas.Width || target.Box.Height > canvas.Height
            ? " Der neue Bildschirm ist grösser als das Bild; was darüber hinausragt, wird "
              + "beschnitten — die Bildgrösse einer laufenden Datei lässt sich nicht ändern."
            : string.Empty;

        return new RecordingDecision(RecordingAction.MoveCanvas, visible,
            $"Die Sitzung liegt jetzt auf dem Bildschirm bei {target.Box.Left}/{target.Box.Top}; das "
            + "Bild folgt ihr dorthin. Eine neue Datei kostet das nicht." + note);
    }

    /// <summary>Der Bildschirm, dessen Ecke links oben auf der Leinwand liegt.</summary>
    private static ScreenInfo? Nearest(IReadOnlyList<ScreenInfo> screens, CanvasLayout canvas)
    {
        foreach (ScreenInfo screen in screens)
        {
            if (screen.Box.Left == canvas.OriginLeft && screen.Box.Top == canvas.OriginTop)
            {
                return screen;
            }
        }

        return screens.Count > 0 ? screens[0] : null;
    }

    private RecordingDecision Stop(string reason)
    {
        _state = DirectorState.Stopped;
        _canvas = null;
        return new RecordingDecision(RecordingAction.Stop, [], reason);
    }

    private static string Describe(int windows) =>
        windows == 1 ? "ein Fenster" : $"{windows} Fenster";

    private enum DirectorState
    {
        NotStarted = 0,
        Recording,
        Paused,
        Stopped,
    }
}
