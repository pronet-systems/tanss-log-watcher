using System.Text.Json;
using System.Text.Json.Serialization;

namespace TanssLogWatcher.Api.Http;

/// <summary>Die eine JSON-Einstellung, mit der dieses Werkzeug mit TANSS spricht.</summary>
/// <remarks>
/// <para><b>Nichts wird beim Schreiben weggelassen.</b> <see cref="JsonIgnoreCondition.Never"/>
/// ist hier kein Standardwert aus Bequemlichkeit, sondern Absicht: TANSS unterscheidet beim
/// Anlegen einer Fernwartung zwischen „Feld fehlt“ und „Feld ist 0 oder leer“. Ein
/// weggelassenes <c>endTime</c> ist nicht dasselbe wie <c>endTime: 0</c> (letzteres speichert
/// SQL NULL und heißt „läuft noch“), und ein weggelassenes <c>ticketId</c> nimmt serverseitig
/// einen anderen Zweig als <c>ticketId: 0</c>.</para>
/// <para>Beim Lesen ist die Groß- und Kleinschreibung gleichgültig, weil TANSS je nach Route
/// unterschiedlich schreibt und ein stillschweigend leer gebliebenes Feld schlimmer ist als
/// eine großzügige Zuordnung.</para>
/// </remarks>
public static class TanssJson
{
    /// <summary>Die gemeinsamen Einstellungen für Lesen und Schreiben.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Erzeugt einen unabhängigen Satz Einstellungen, etwa für Tests.</summary>
    public static JsonSerializerOptions Create() => new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Serialisiert einen Rumpf über seinen <b>Laufzeittyp</b>.
    /// </summary>
    /// <remarks>
    /// Der Umweg über <see cref="object.GetType"/> ist zwingend: <c>Serialize&lt;object&gt;</c>
    /// schreibt nur die Eigenschaften des statischen Typs und liefert bei einem als
    /// <c>object</c> übergebenen Datensatz ein leeres <c>{}</c> — eine Fernwartung ohne Inhalt,
    /// die TANSS anstandslos mit 201 quittieren würde.
    /// </remarks>
    public static string Serialize(object body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return JsonSerializer.Serialize(body, body.GetType(), Options);
    }
}
