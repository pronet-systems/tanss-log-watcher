namespace TanssLogWatcher.Recording;

/// <summary>
/// Was aufgenommen wird.
/// </summary>
public enum CaptureScope
{
    /// <summary>
    /// Nur die Fenster der Sitzung; alles andere bleibt schwarz.
    /// </summary>
    /// <remarks>
    /// Die Voreinstellung, und das ist keine Bequemlichkeit: Wer den ganzen Bildschirm
    /// aufzeichnen will, entscheidet das und verantwortet es. Wer nichts entscheidet, bekommt
    /// nicht ungefragt die grössere Datenmenge — und nicht ungefragt das Mailfenster des
    /// Technikers in der Dokumentation des Kunden.
    /// </remarks>
    SessionWindows = 0,

    /// <summary>Der ganze Bildschirm, auf dem die Sitzung liegt.</summary>
    Screen,
}

/// <summary>
/// Die geprüften Stellschrauben der Aufzeichnung.
/// </summary>
/// <remarks>
/// <para><b>Ohne Kenntnis von JSON, Dateipfaden und <c>AppConfig</c>.</b> Das Aufnahmeprojekt
/// soll sich gegen erfundene Werte prüfen lassen, ohne dass eine Konfigurationsdatei im Spiel
/// ist. Die Abbildung von <c>AppConfig.Recording</c> auf diesen Typ macht die Anwendung — dort,
/// wo auch die Einwilligung geprüft wird.</para>
/// <para>Die Prüfung der Werte steht trotzdem hier: Wer dieses Projekt einzeln benutzt, soll
/// nicht an einer unsinnigen Bildrate scheitern, die niemand beanstandet hat.</para>
/// </remarks>
public sealed record RecordingOptions
{
    /// <summary>Was aufgenommen wird.</summary>
    public CaptureScope Scope { get; init; } = CaptureScope.SessionWindows;

    /// <summary>Bilder je Sekunde, Obergrenze.</summary>
    public int FramesPerSecond { get; init; } = 4;

    /// <summary>Nach welcher Ruhe trotzdem ein Bild geschrieben wird.</summary>
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Unterhalb wie vieler freier Megabyte nicht mehr aufgezeichnet wird.</summary>
    public long MinimumFreeMegabytes { get; init; } = 2048;

    /// <summary>
    /// Wie lange ein Fenster fehlen darf, bevor pausiert wird.
    /// </summary>
    /// <remarks>
    /// Ein Fenster ist beim Verschieben zwischen zwei Bildschirmen für den Bruchteil einer
    /// Sekunde ohne brauchbare Geometrie. Ohne diese Schonfrist entstünde daraus eine Pause von
    /// einem Takt — und im Bericht eine Unterbrechung, die es nie gab.
    /// </remarks>
    public TimeSpan PauseGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Wie lange die Sitzung auf dem anderen Bildschirm liegen muss, bevor das Bild folgt.
    /// </summary>
    /// <remarks>
    /// Anderthalb Sekunden. Ein Fenster, das gerade über die Bildschirmgrenze gezogen wird,
    /// gehört unterwegs mal hierhin und mal dorthin; ohne diese Beharrungszeit spränge das Bild
    /// im Takt der Maus. Ein Video, das springt, ist unbrauchbarer als eines mit einem schwarzen
    /// Rand.
    /// </remarks>
    public TimeSpan ScreenSwitchDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Wie lange nach einem Wechsel kein weiterer stattfindet.</summary>
    /// <remarks>
    /// Drei Sekunden. Sie verhindern das Hin und Her, wenn jemand ein Fenster zwischen zwei
    /// Bildschirmen sucht.
    /// </remarks>
    public TimeSpan ScreenSwitchLockout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Welcher Anteil der Fensterfläche auf dem neuen Bildschirm liegen muss.
    /// </summary>
    /// <remarks>
    /// Sechzig Prozent. Bei der Hälfte wäre jede Grenzlage eine Zufallsentscheidung; deutlich
    /// darüber bliebe das Bild auf dem alten Bildschirm, obwohl dort kaum noch etwas liegt.
    /// </remarks>
    public double ScreenSwitchShare { get; init; } = 0.6;

    /// <summary>Prüft die Werte und wirft, wenn einer unbrauchbar ist.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Ein Wert liegt ausserhalb.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(FramesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FramesPerSecond, 60);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Heartbeat, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumFreeMegabytes);
        ArgumentOutOfRangeException.ThrowIfNegative(PauseGrace.Ticks);
        ArgumentOutOfRangeException.ThrowIfNegative(ScreenSwitchDelay.Ticks);
        ArgumentOutOfRangeException.ThrowIfNegative(ScreenSwitchLockout.Ticks);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ScreenSwitchShare, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ScreenSwitchShare, 1);
    }
}
