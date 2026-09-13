namespace TanssLogWatcher.Recording;

/// <summary>
/// Die Uhr der Aufzeichnung — sie läuft nur, während tatsächlich geschrieben wird.
/// </summary>
/// <remarks>
/// <para><b>Sie ist der Grund, warum die Datei nicht lügt.</b> Eine Fernwartung dauert
/// fünfundvierzig Minuten, davon fünfundzwanzig mit minimiertem Fenster. Liefe die Zeitachse
/// der Datei an der Wanduhr, stünde am Ende ein Video von fünfundvierzig Minuten, dessen
/// zweite Hälfte ein Standbild ist — und der Abspieler zeigte eine Dauer, die so nie
/// stattgefunden hat. Diese Uhr steht während der Pause, und heraus kommt ein Video von
/// zwanzig Minuten, das genau die zwanzig Minuten zeigt, in denen etwas geschah.</para>
///
/// <para><b>Der Zeitstempel wird gerechnet, nicht aufaddiert.</b> Der n-te geschriebene
/// Bildpunkt liegt bei <c>n / Bildrate</c>. Wer statt dessen bei jedem Bild einen Abstand
/// aufaddierte, sammelte bei krummen Bildraten — vier Bilder je Sekunde sind 2 500 000 Ticks,
/// drei sind 3 333 333,33 — mit jeder Stunde mehr Abweichung an. Gerechnet driftet nichts.</para>
///
/// <para><b>Ohne Windows und ohne Media Foundation.</b> Das ist Absicht: Die Zeitachse ist der
/// Teil, der im Streitfall zählt, und er muss ohne Bildschirm prüfbar sein.</para>
/// </remarks>
public sealed class RecordingClock
{
    private readonly List<RecordingPause> _pauses = [];
    private readonly long _ticksPerFrame;

    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _pausedAt;
    private TimeSpan _pausedTotal;
    private long _frames;

    /// <summary>Baut die Uhr für eine Bildrate.</summary>
    /// <param name="framesPerSecond">Bilder je Sekunde; muss zwischen 1 und 60 liegen.</param>
    /// <exception cref="ArgumentOutOfRangeException">Die Bildrate liegt ausserhalb.</exception>
    public RecordingClock(int framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(framesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(framesPerSecond, 60);

        FramesPerSecond = framesPerSecond;
        _ticksPerFrame = TimeSpan.TicksPerSecond / framesPerSecond;
    }

    /// <summary>Die Bildrate, mit der die Zeitstempel gerechnet werden.</summary>
    public int FramesPerSecond { get; }

    /// <summary>Läuft die Uhr gerade?</summary>
    public bool IsRunning => _startedAt is not null && _pausedAt is null;

    /// <summary>Ist die Aufzeichnung angehalten?</summary>
    public bool IsPaused => _pausedAt is not null;

    /// <summary>Wie viele Bilder bisher geschrieben wurden.</summary>
    public long WrittenFrames => _frames;

    /// <summary>Die Pausen, in der Reihenfolge ihres Auftretens.</summary>
    /// <remarks>
    /// Sie gehören in die Begleitdatei. Ohne sie wäre die kurze Datei zu einer langen Sitzung
    /// unerklärlich, und genau diese Erklärung ist im Zweifel die entscheidende.
    /// </remarks>
    public IReadOnlyList<RecordingPause> Pauses => _pauses;

    /// <summary>Startet die Uhr.</summary>
    /// <param name="now">Der Zeitpunkt des Beginns.</param>
    /// <exception cref="InvalidOperationException">Die Uhr läuft bereits.</exception>
    public void Start(DateTimeOffset now)
    {
        if (_startedAt is not null)
        {
            throw new InvalidOperationException(
                "Die Aufzeichnungsuhr läuft bereits. Ein zweiter Start würde den Beginn "
                + "verschieben und damit alle bisher geschriebenen Zeitstempel entwerten.");
        }

        _startedAt = now;
    }

    /// <summary>
    /// Hält die Uhr an. Ein zweiter Aufruf ohne Fortsetzen tut nichts.
    /// </summary>
    /// <remarks>
    /// Duldsam und nicht streng: Die Pause wird an zwei Stellen festgestellt — daran, dass
    /// keine Bilder mehr kommen, und daran, dass das Fenster minimiert ist. Beide dürfen
    /// dasselbe melden, ohne dass es zum Fehler wird.
    /// </remarks>
    /// <param name="now">Der Zeitpunkt der Unterbrechung.</param>
    public void Pause(DateTimeOffset now)
    {
        if (_startedAt is null || _pausedAt is not null)
        {
            return;
        }

        _pausedAt = now;
    }

    /// <summary>Setzt die Uhr fort. Ein Aufruf ohne vorherige Pause tut nichts.</summary>
    /// <param name="now">Der Zeitpunkt der Fortsetzung.</param>
    public void Resume(DateTimeOffset now)
    {
        if (_pausedAt is not { } since)
        {
            return;
        }

        // Eine rueckwaerts laufende Wanduhr - Zeitumstellung, Zeitabgleich - darf die
        // Aufzeichnung nicht verkuerzen. Negative Pausen gibt es nicht.
        TimeSpan length = now > since ? now - since : TimeSpan.Zero;

        _pauses.Add(new RecordingPause(since, length));
        _pausedTotal += length;
        _pausedAt = null;
    }

    /// <summary>
    /// Die aufgezeichnete Zeit bis jetzt — ohne die Pausen.
    /// </summary>
    /// <param name="now">Der Bezugszeitpunkt.</param>
    public TimeSpan Elapsed(DateTimeOffset now)
    {
        if (_startedAt is not { } started)
        {
            return TimeSpan.Zero;
        }

        TimeSpan wall = now > started ? now - started : TimeSpan.Zero;
        TimeSpan paused = _pausedTotal;

        if (_pausedAt is { } since)
        {
            paused += now > since ? now - since : TimeSpan.Zero;
        }

        TimeSpan recorded = wall - paused;
        return recorded > TimeSpan.Zero ? recorded : TimeSpan.Zero;
    }

    /// <summary>
    /// Meldet ein geschriebenes Bild und liefert dessen Zeitstempel in der Datei.
    /// </summary>
    /// <remarks>
    /// Aus dem Zähler gerechnet und nicht aus der Wanduhr gelesen: Der Abstand zweier Bilder in
    /// der Datei ist konstant, auch wenn der Rechner einen Takt lang beschäftigt war.
    /// </remarks>
    /// <returns>Der Zeitstempel des Bildes, gemessen ab Beginn der Aufzeichnung.</returns>
    /// <exception cref="InvalidOperationException">Die Uhr läuft nicht.</exception>
    public TimeSpan NextFrameTimestamp()
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException(
                "Während einer Pause wird kein Bild geschrieben. Käme hier eines durch, "
                + "entstünde in der Datei Zeit, die es nicht gab.");
        }

        return TimeSpan.FromTicks(_frames++ * _ticksPerFrame);
    }

    /// <summary>Die Dauer, die die fertige Datei haben wird.</summary>
    /// <remarks>
    /// Das letzte Bild steht am Anfang seines Zeitabschnitts; die Datei ist deshalb um genau
    /// einen Bildabstand länger als der Zeitstempel des letzten Bildes.
    /// </remarks>
    public TimeSpan Duration => TimeSpan.FromTicks(_frames * _ticksPerFrame);
}

/// <summary>Ein Abschnitt, in dem nicht aufgezeichnet wurde.</summary>
/// <param name="StartedAt">Wann die Pause begann, nach der Wanduhr.</param>
/// <param name="Length">Wie lange sie dauerte.</param>
public readonly record struct RecordingPause(DateTimeOffset StartedAt, TimeSpan Length);
