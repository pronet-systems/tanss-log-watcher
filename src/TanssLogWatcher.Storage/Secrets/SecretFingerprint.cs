using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TanssLogWatcher.Storage.Secrets;

/// <summary>
/// Kurzer Fingerabdruck eines Geheimnisses <b>mit hoher Entropie</b> — ein ungesalzener
/// SHA-256, auf acht Hexziffern gekürzt.
/// </summary>
/// <remarks>
/// <para>Damit lässt sich protokollieren, <i>dass</i> das Token gewechselt hat, ohne es zu
/// zeigen. Bei der Fehlersuche ist genau das die Frage: Arbeitet der Dienst noch mit dem
/// alten Token? Acht Hexziffern reichen, um zwei Werte zu unterscheiden, und sind zu kurz,
/// um daraus etwas zu gewinnen.</para>
///
/// <para><b>Was hier <i>nicht</i> zugesichert wird.</b> Der Abdruck ist ungesalzen. Er ist
/// deshalb nicht „nicht umkehrbar“ im allgemeinen Sinn, sondern nur so weit unumkehrbar,
/// wie der Eingabewert unratbar ist. Ein TANSS-Token ist ein JWT und damit unratbar — für
/// diesen Zweck ist der schlichte Hash richtig und ein Schlüssel wäre nur eine weitere
/// Datei, die verlorengehen kann. Bei einem Wert aus einem <b>überschaubaren Vorrat</b>
/// kehrt sich das um: Wer eine Liste von Kandidaten hat, rechnet jeden durch und vergleicht;
/// der Abdruck ist dann eine bestätigbare Festlegung statt einer Verschleierung.</para>
///
/// <para>Für ratbare Angaben — Fensterbeschriftungen, Rechnernamen, Betreffzeilen — gehört
/// deshalb <see cref="KeyedFingerprint"/> hierher und nicht diese Klasse. Sie salzt mit
/// einem je Installation erzeugten Schlüssel, der DPAPI-versiegelt neben dem Token liegt.</para>
///
/// <para>Das <b>Schwärzen</b> von Text gehört ohnehin nicht hierher, sondern nach
/// <see cref="TanssLogWatcher.Api.Diagnostics.Redaction"/>. Eine zweite Version in dieser
/// Schicht war nachweislich die schwächere: Sie kannte das Feldmuster <c>apiKey</c> nicht —
/// und genau in dieser Form liefert <c>POST /api/v1/login</c> das Token.</para>
/// </remarks>
public static class SecretFingerprint
{
    /// <summary>Der Fingerabdruck. Ein leerer Wert ergibt <c>(leer)</c>.</summary>
    /// <remarks>
    /// Nur für Werte mit hoher Entropie. Für alles, was sich erraten lässt, siehe
    /// <see cref="KeyedFingerprint.Of"/>.
    /// </remarks>
    public static string Of(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return KeyedFingerprint.EmptyMarker;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLower(CultureInfo.InvariantCulture);
    }
}
