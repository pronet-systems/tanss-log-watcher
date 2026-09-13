namespace TanssLogWatcher.Recording;

/// <summary>
/// Entscheidet, ob ein Bild geschrieben wird — die Bildrate, von Hand getaktet.
/// </summary>
/// <remarks>
/// <para><b>Von Hand, weil Windows es nicht tut.</b> Die Fensteraufnahme liefert Bilder, sobald
/// sich etwas ändert, und zwar so oft, wie sich etwas ändert — gemessen wurden sechzig Bilder
/// je Sekunde an einem Fenster, das sich alle sechzehn Millisekunden neu zeichnete. Die
/// Schnittstelle hat dafür eine Drossel (<c>MinUpdateInterval</c>), aber die wirft auf
/// Windows 11 Build 22631 eine Ausnahme, obwohl sie in der Projektion steht. Also drosseln
/// wir selbst.</para>
///
/// <para><b>Zwei Gründe, ein Bild zu schreiben.</b> Es hat sich etwas geändert und der
/// Mindestabstand ist um — das ist der Normalfall. Oder es hat sich lange nichts geändert:
/// dann kommt ein Herzschlag, damit die Zeitachse weiterläuft. Ohne ihn hätte ein stehender
/// Bildschirm eine Lücke in der Datei, und der Abspieler spränge darüber hinweg, als wäre
/// dort nichts gewesen — dabei war dort eine Viertelstunde, in der jemand gelesen hat.</para>
///
/// <para>Ohne Windows, damit die Regel ohne Bildschirm prüfbar ist.</para>
/// </remarks>
public sealed class FrameCadence
{
    private readonly TimeSpan _minimumGap;
    private readonly TimeSpan _heartbeat;

    private TimeSpan? _lastWrite;

    /// <summary>Baut den Takt.</summary>
    /// <param name="framesPerSecond">Die Obergrenze in Bildern je Sekunde.</param>
    /// <param name="heartbeat">Nach welcher Ruhe trotzdem ein Bild fällig ist.</param>
    /// <exception cref="ArgumentOutOfRangeException">Bildrate oder Herzschlag sind unbrauchbar.</exception>
    public FrameCadence(int framesPerSecond, TimeSpan heartbeat)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(framesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(framesPerSecond, 60);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeat, TimeSpan.Zero);

        _minimumGap = TimeSpan.FromSeconds(1.0 / framesPerSecond);
        _heartbeat = heartbeat;
    }

    /// <summary>Der kleinste Abstand zweier Bilder.</summary>
    public TimeSpan MinimumGap => _minimumGap;

    /// <summary>Nach welcher Ruhe ein Bild auch ohne Änderung fällig ist.</summary>
    public TimeSpan Heartbeat => _heartbeat;

    /// <summary>
    /// Soll jetzt ein Bild geschrieben werden?
    /// </summary>
    /// <remarks>
    /// <paramref name="elapsed"/> ist die Zeit der <see cref="RecordingClock"/> und nicht die
    /// Wanduhr — während einer Pause steht sie, und damit wird auch kein Herzschlag fällig.
    /// Genau so soll es sein: Eine Pause hinterlässt keine Bilder, sonst wäre sie in der Datei
    /// keine.
    /// </remarks>
    /// <param name="elapsed">Die aufgezeichnete Zeit bis jetzt.</param>
    /// <param name="changed">Hat sich seit dem letzten Bild etwas verändert?</param>
    /// <returns>Der Grund, aus dem geschrieben wird — oder <see cref="FrameReason.Skip"/>.</returns>
    public FrameReason Decide(TimeSpan elapsed, bool changed)
    {
        if (_lastWrite is not { } last)
        {
            // Das erste Bild geht immer durch: Ohne es gaebe es keinen Anfang, gegen den sich
            // alles Weitere messen liesse.
            return FrameReason.First;
        }

        TimeSpan since = elapsed - last;

        if (changed && since >= _minimumGap)
        {
            return FrameReason.Changed;
        }

        if (!changed && since >= _heartbeat)
        {
            return FrameReason.Heartbeat;
        }

        return FrameReason.Skip;
    }

    /// <summary>Hält fest, dass zu diesem Zeitpunkt ein Bild geschrieben wurde.</summary>
    /// <param name="elapsed">Die aufgezeichnete Zeit des geschriebenen Bildes.</param>
    public void Accepted(TimeSpan elapsed) => _lastWrite = elapsed;

    /// <summary>
    /// Vergisst den letzten Zeitpunkt — nach einer Pause.
    /// </summary>
    /// <remarks>
    /// Damit das erste Bild nach der Fortsetzung sofort durchgeht. Es ist ohnehin ein
    /// Schlüsselbild: Was währenddessen geschah, hat die Aufzeichnung nicht gesehen, und der
    /// Abspieler soll an dieser Stelle sauber aufsetzen können.
    /// </remarks>
    public void Reset() => _lastWrite = null;
}

/// <summary>Warum ein Bild geschrieben wird.</summary>
public enum FrameReason
{
    /// <summary>Es wird keines geschrieben.</summary>
    Skip = 0,

    /// <summary>Das erste Bild der Aufzeichnung.</summary>
    First,

    /// <summary>Der Bildschirm hat sich geändert.</summary>
    Changed,

    /// <summary>Nichts hat sich geändert, aber die Zeitachse braucht einen Punkt.</summary>
    Heartbeat,
}
