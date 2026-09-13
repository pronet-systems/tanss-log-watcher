using System.Text.RegularExpressions;

namespace TanssLogWatcher.Api.Diagnostics;

/// <summary>
/// Schwärzt Geheimnisse, bevor eine Zeichenkette in ein Protokoll, eine Fehlermeldung oder
/// eine Oberfläche gelangt.
/// </summary>
/// <remarks>
/// <para>Hausregel: Geheimnisse werden niemals protokolliert. Das ist keine Stilfrage — ein
/// TANSS-Token aus <c>/api/v1/jwts</c> läuft bis zu ein Jahr und berechtigt zu allem, was der
/// Techniker darf. Eine einzige Protokollzeile mit vollständigem Token ist ein dauerhafter
/// Schlüsselverlust.</para>
/// <para>Die Schwärzung arbeitet auf dem Text, nicht auf einer Datenstruktur: sie muss auch
/// dann greifen, wenn ein Token in einer Ausnahmemeldung, einem Rumpffragment oder einer
/// kopierten Kopfzeile mitgeschleppt wird.</para>
/// <para><b>Grob allein genügt aber nicht.</b> Derselbe Text beantwortet später die Frage,
/// warum ein Upload schiefgegangen ist. Ein Muster, das aus „Der Token: abgelaufen, bitte
/// erneuern.“ ein „Der Token: &lt;geschwaerzt&gt;, bitte erneuern.“ macht, schwärzt kein
/// Geheimnis, sondern die Begründung. Deshalb müssen <b>beide</b> Seiten stimmen: der Schlüssel
/// muss wie ein Feld aussehen, und der Wert muss wie ein Geheimnis aussehen. Im Zweifel wird
/// geschwärzt — ein gewöhnliches deutsches Wort ist aber kein Zweifelsfall.</para>
/// </remarks>
public static partial class Redaction
{
    /// <summary>Der Ersatztext, der an die Stelle des Geheimnisses tritt.</summary>
    public const string Mask = "<geschwaerzt>";

    /// <summary>
    /// Ab dieser Länge gilt ein Wert auch ohne Ziffer und ohne Sonderzeichen als undurchsichtig.
    /// </summary>
    /// <remarks>
    /// Bewusst hoch angesetzt: deutsche Zusammensetzungen wie „Wiederherstellungspunkt“ (23
    /// Zeichen) stehen durchaus einmal hinter einem Doppelpunkt, ein reines Buchstabengeheimnis
    /// dieser Länge ist dagegen selten. Kürzere Geheimnisse fängt die Prüfung auf Ziffern und
    /// Sonderzeichen ab.
    /// </remarks>
    private const int OpaqueLength = 24;

    /// <summary>Zeichen, die in einem Geheimnis vorkommen und in einem deutschen Wort nicht.</summary>
    private const string TokenPunctuation = "-_./+=~";

    /// <summary>
    /// Entfernt JWTs, <c>Bearer</c>-Werte und die Werte der Felder und Kopfzeilen
    /// <c>apiToken</c>, <c>refreshToken</c>, <c>Authorization</c>, <c>apiKey</c>,
    /// <c>password</c> und <c>token</c>.
    /// </summary>
    /// <param name="text">Beliebiger Text; <c>null</c> ergibt eine leere Zeichenkette.</param>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Reihenfolge: erst das Token selbst, dann sein Traeger. Umgekehrt bliebe der
        // Nutzteil eines JWT stehen, das ohne "Bearer " im Text steht.
        string result = JwtPattern().Replace(text, Mask);
        result = BearerPattern().Replace(result, "Bearer " + Mask);
        result = SecretFieldPattern().Replace(result, MaskFieldValue);
        return result;
    }

    /// <summary>Schwärzt die Meldung einer Ausnahme.</summary>
    public static string Scrub(Exception? error) => Scrub(error?.Message);

    /// <summary>
    /// Entscheidet für einen Treffer des Feldmusters, ob der Wert wirklich geschwärzt wird.
    /// </summary>
    /// <remarks>
    /// <para>Ein Wert in Anführungszeichen ist abgegrenzte Nutzlast — JSON, keine Prosa. Dort
    /// wird ohne weitere Prüfung geschwärzt, auch bei zwei Zeichen: ein gekürztes Token ist
    /// immer noch ein Geheimnis, und ein verlorener Zweizeichenwert kostet nichts.</para>
    /// <para>Ein Wert ohne Anführungszeichen steht dagegen möglicherweise mitten im Satz. Er
    /// wird nur geschwärzt, wenn er wie ein Geheimnis aussieht — siehe
    /// <see cref="LooksLikeSecret"/>.</para>
    /// </remarks>
    private static string MaskFieldValue(Match match)
    {
        string head = match.Groups["head"].Value;

        if (match.Groups["quoted"] is { Success: true } quoted)
        {
            return quoted.Length == 0 ? match.Value : head + '"' + Mask + '"';
        }

        string bare = match.Groups["bare"].Value;
        string separator = match.Groups["sep"].Value;

        // Satzzeichen gehoeren zum Satz, nicht zum Wert: "Password: falsch." endet auf einen
        // Punkt, und ein Punkt im Wert zaehlt sonst als Tokenzeichen.
        string value = bare.TrimEnd('.', '!', '?');
        string sentenceEnd = bare[value.Length..];

        return LooksLikeSecret(value, separator)
            ? head + Mask + sentenceEnd + separator
            : match.Value;
    }

    /// <summary>Sieht dieser Wert ohne Anführungszeichen nach einem Geheimnis aus?</summary>
    /// <param name="value">Der Wert ohne abschließende Satzzeichen.</param>
    /// <param name="separator">
    /// Das Zeichen, das den Wert beendet hat — <c>&amp;</c>, wenn er in einer
    /// Abfragezeichenkette steht, sonst leer.
    /// </param>
    /// <remarks>
    /// Drei Belege zählen, jeder für sich genügt:
    /// <list type="number">
    ///   <item>Ein <c>&amp;</c> dahinter. Dann steht der Wert in einer Abfragezeichenkette, und
    ///   die trägt Felder, keine Sätze — so verschwindet <c>password=kennwort&amp;x=1</c>,
    ///   obwohl der Wert ein gewöhnliches Wort ist.</item>
    ///   <item>Eine Ziffer oder ein Zeichen aus <see cref="TokenPunctuation"/>. Geheimnisse
    ///   mischen fast immer, deutsche Wörter nie.</item>
    ///   <item>Schiere Länge ab <see cref="OpaqueLength"/>.</item>
    /// </list>
    /// </remarks>
    private static bool LooksLikeSecret(string value, string separator)
    {
        if (value.Length == 0)
        {
            return false;
        }

        if (separator.Length != 0 || value.Length >= OpaqueLength)
        {
            return true;
        }

        foreach (char character in value)
        {
            if (char.IsAsciiDigit(character)
                || TokenPunctuation.Contains(character, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ein JWT im Klartext: drei mit Punkt getrennte Base64url-Abschnitte, beginnend mit
    /// <c>eyJ</c> (der Base64url-Form von <c>{"</c>).
    /// </summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    /// <summary>
    /// Ein <c>Bearer</c>-Wert. Die Zeichenklasse endet bewusst an Anführungszeichen, Komma und
    /// Klammer: ein gieriges <c>\S+</c> verschluckt in einer JSON-Zeile den ganzen Rest des
    /// Objekts und macht aus der Schwärzung einen Datenverlust im Protokoll.
    /// </summary>
    [GeneratedRegex("""Bearer\s+[^\s"',;}\]]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    /// <summary>
    /// Ein Feld- oder Kopfzeilenname, dem ein Wert folgt — in JSON (<c>"apiToken": "..."</c>),
    /// in einer Kopfzeile (<c>apiToken: ...</c>) und in einer Abfragezeichenkette
    /// (<c>apiToken=...</c>).
    /// </summary>
    /// <remarks>
    /// <para>Das Muster trifft nur die <b>Form</b>; ob geschwärzt wird, entscheidet
    /// <see cref="MaskFieldValue"/>. Deshalb sind die beiden Wertformen getrennt: <c>quoted</c>
    /// ist abgegrenzte Nutzlast, <c>bare</c> kann Prosa sein. <c>sep</c> hält fest, ob ein
    /// <c>&amp;</c> folgte.</para>
    /// <para><c>password</c> und <c>token</c> stehen mit auf der Liste, weil der Anmelderumpf
    /// (<c>POST /api/v1/login</c>) Kennwort und zweiten Faktor unter genau diesen Namen im
    /// Klartext trägt. Das <c>\b</c> davor schützt die längeren Namen: in <c>apiToken</c> und
    /// <c>refreshToken</c> steckt <c>token</c>, aber ohne Wortgrenze — sie greifen weiterhin
    /// über ihre eigene Alternative und verlieren ihren Namen nicht.</para>
    /// </remarks>
    [GeneratedRegex(
        """(?<head>"?\b(?:apiToken|refreshToken|Authorization|apiKey|password|token)"?\s*[:=]\s*)(?:"(?<quoted>[^"]*)"|(?<bare>[^\s"',;}\]&]+)(?<sep>&)?)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretFieldPattern();
}
