namespace TanssLogWatcher.Recording;

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
    /// <summary>Bilder je Sekunde, Obergrenze.</summary>
    public int FramesPerSecond { get; init; } = 4;

    /// <summary>Nach welcher Ruhe trotzdem ein Bild geschrieben wird.</summary>
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Nach welcher Zeit eine neue Datei begonnen wird.</summary>
    public TimeSpan SegmentLength { get; init; } = TimeSpan.FromMinutes(10);

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

    /// <summary>Prüft die Werte und wirft, wenn einer unbrauchbar ist.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Ein Wert liegt ausserhalb.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(FramesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FramesPerSecond, 60);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Heartbeat, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SegmentLength, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumFreeMegabytes);
        ArgumentOutOfRangeException.ThrowIfNegative(PauseGrace.Ticks);
    }
}
