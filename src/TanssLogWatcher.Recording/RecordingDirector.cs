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

    /// <summary>Die laufende Datei abschliessen und eine neue beginnen.</summary>
    RollSegment,

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
public readonly record struct RecordingInput(
    DateTimeOffset Now,
    IReadOnlyList<WindowBox> Windows,
    bool SessionEnded,
    long FreeMegabytes);

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
/// <para><b>Er führt einen Zustand.</b> Anders als eine reine Rechenvorschrift merkt er sich,
/// ob gerade aufgezeichnet wird, seit wann das Segment läuft und wann die Fenster
/// verschwunden sind. Genau diese drei Angaben entscheiden über Pause, Abschnittswechsel und
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
    private DateTimeOffset _segmentStartedAt;
    private DateTimeOffset? _windowsGoneSince;

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
        if (CanvasLayout.For(visible) is not { } canvas)
        {
            // Noch kein Fenster mit Flaeche - etwa weil die Fernwartung erst startet. Es wird
            // nichts angelegt: eine leere Datei waere eine Aufzeichnung, die es nicht gibt.
            return RecordingDecision.Nothing;
        }

        _canvas = canvas;
        _state = DirectorState.Recording;
        _segmentStartedAt = input.Now;
        _windowsGoneSince = null;

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
            return new RecordingDecision(RecordingAction.Pause, [],
                "Angehalten: Kein Fenster der Sitzung ist gerade sichtbar — minimiert oder "
                + "geschlossen. Die Zeit der Pause zählt nicht zur Aufzeichnung.");
        }

        _windowsGoneSince = null;

        if (_canvas is { } canvas && !canvas.Fits(visible))
        {
            return Roll(input, visible,
                "Die Fenster passen nicht mehr auf die Leinwand — verschoben, vergrössert oder "
                + "auf einen anderen Bildschirm gewandert. Die Grösse liegt in der Datei fest, "
                + "deshalb beginnt hier eine neue; skaliert wird nicht.");
        }

        if (input.Now - _segmentStartedAt >= _options.SegmentLength)
        {
            return Roll(input, visible,
                $"Abschnitt nach {Describe(_options.SegmentLength)} abgeschlossen. Eine lange "
                + "Aufzeichnung in einer einzigen Datei wäre bei einem Fehler ganz verloren.");
        }

        return RecordingDecision.Nothing;
    }

    private RecordingDecision WhilePaused(RecordingInput input, List<WindowBox> visible)
    {
        if (visible.Count == 0)
        {
            return RecordingDecision.Nothing;
        }

        _windowsGoneSince = null;

        if (_canvas is { } canvas && canvas.Fits(visible))
        {
            _state = DirectorState.Recording;
            return new RecordingDecision(RecordingAction.Resume, visible,
                "Fortgesetzt: Die Fenster sind wieder da und passen auf die bestehende "
                + "Leinwand.");
        }

        return Roll(input, visible,
            "Fortgesetzt, aber die Fenster liegen jetzt anders — die alte Leinwand passt "
            + "nicht mehr. Es beginnt eine neue Datei.");
    }

    private RecordingDecision Roll(RecordingInput input, List<WindowBox> visible, string reason)
    {
        if (CanvasLayout.For(visible) is not { } canvas)
        {
            return RecordingDecision.Nothing;
        }

        _canvas = canvas;
        _state = DirectorState.Recording;
        _segmentStartedAt = input.Now;

        return new RecordingDecision(RecordingAction.RollSegment, visible,
            $"{reason} Neue Leinwand: {canvas.Width}×{canvas.Height} Bildpunkte.");
    }

    private RecordingDecision Stop(string reason)
    {
        _state = DirectorState.Stopped;
        _canvas = null;
        return new RecordingDecision(RecordingAction.Stop, [], reason);
    }

    private static string Describe(int windows) =>
        windows == 1 ? "ein Fenster" : $"{windows} Fenster";

    private static string Describe(TimeSpan span) =>
        span.TotalMinutes >= 1
            ? $"{span.TotalMinutes:0} Minuten"
            : $"{span.TotalSeconds:0} Sekunden";

    private enum DirectorState
    {
        NotStarted = 0,
        Recording,
        Paused,
        Stopped,
    }
}
