using System.Security.Cryptography;

namespace TanssLogWatcher.Storage.Queue;

/// <summary>
/// Die Rückstauzeit zwischen zwei Sendeversuchen: exponentiell, gestreut, gedeckelt.
/// </summary>
/// <remarks>
/// <para><b>Exponentiell</b>, weil die häufigste Ursache eines misslungenen Uploads eine
/// Störung ist, die Minuten und nicht Sekunden dauert — ein Notebook im Zug, ein Neustart
/// der Instanz, ein abgelaufenes VPN. Ein starrer Takt von zehn Sekunden erzeugte dabei
/// hunderte sinnloser Versuche und träfe die Instanz genau dann, wenn sie ohnehin schwächelt.</para>
///
/// <para><b>Gestreut</b>, weil sonst alle Einträge einer Warteschlange im selben Augenblick
/// wieder fällig würden. Nach einem Ausfall der Instanz hätten alle denselben Rückstau — und
/// der erste Versuch danach wäre ein Stoß aus allen Sitzungen gleichzeitig.</para>
///
/// <para><b>Gedeckelt auf eine Stunde</b>, weil ein Eintrag sonst nach einem langen
/// Wochenende rechnerisch erst in Wochen wieder an die Reihe käme. Die Fernwartung soll beim
/// Kunden ankommen, auch wenn sie eine Weile liegengeblieben ist.</para>
/// </remarks>
public static class Backoff
{
    /// <summary>Rückstau vor dem ersten Wiederholungsversuch.</summary>
    public static readonly TimeSpan Base = TimeSpan.FromSeconds(15);

    /// <summary>Obergrenze. Wird niemals überschritten, auch nicht durch die Streuung.</summary>
    public static readonly TimeSpan Cap = TimeSpan.FromHours(1);

    /// <summary>Streubreite als Anteil, also plus/minus 20 Prozent.</summary>
    public const double JitterSpread = 0.2;

    /// <summary>Grösster berücksichtigter Verdopplungsschritt. Darüber gilt ohnehin der Deckel.</summary>
    private const int MaxDoublings = 20;

    /// <summary>
    /// Rückstau für den <paramref name="attempt"/>-ten Versuch bei vorgegebener Streuung.
    /// </summary>
    /// <param name="attempt">Bisherige Versuche, einschließlich des gerade misslungenen. Ab 1.</param>
    /// <param name="jitter">Streuwert zwischen 0 und 1. Bei 0,5 ergibt sich der ungestreute Wert.</param>
    /// <remarks>
    /// Die Streuung wird hier hereingereicht und nicht erzeugt, damit das Wachstum und der
    /// Deckel prüfbar sind. Ein Verfahren, dessen Obergrenze sich nur zufällig zeigt, ist
    /// keine Obergrenze.
    /// </remarks>
    public static TimeSpan For(int attempt, double jitter)
    {
        int doublings = Math.Clamp(attempt, 1, MaxDoublings + 1) - 1;
        double seconds = Base.TotalSeconds * Math.Pow(2, doublings);
        seconds = Math.Min(seconds, Cap.TotalSeconds);

        double factor = 1.0 - JitterSpread + (2.0 * JitterSpread * Math.Clamp(jitter, 0.0, 1.0));
        double spread = Math.Min(seconds * factor, Cap.TotalSeconds);

        return TimeSpan.FromSeconds(Math.Max(spread, 1.0));
    }

    /// <summary>Rückstau mit zufälliger Streuung.</summary>
    /// <remarks>
    /// Die Zufallszahl stammt aus <see cref="RandomNumberGenerator"/>. Nicht, weil hier etwas
    /// zu schützen wäre, sondern weil ein Prozess-weiter Startwert nach einem gemeinsamen
    /// Neustart aller Arbeiter wieder dieselbe Folge liefern könnte — und damit genau den
    /// gleichzeitigen Stoß, den die Streuung verhindern soll.
    /// </remarks>
    public static TimeSpan Next(int attempt) =>
        For(attempt, RandomNumberGenerator.GetInt32(0, 1001) / 1000.0);

    /// <summary>Zeitpunkt des nächsten Versuchs, von <paramref name="now"/> aus gerechnet.</summary>
    public static DateTimeOffset NextAttemptAfter(DateTimeOffset now, int attempt) =>
        now + Next(attempt);
}
