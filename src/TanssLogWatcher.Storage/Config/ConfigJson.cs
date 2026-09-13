using System.Text.Json;
using System.Text.Json.Serialization;

namespace TanssLogWatcher.Storage.Config;

/// <summary>
/// Die eine Version der JSON-Einstellungen für <c>config.json</c>.
/// </summary>
/// <remarks>
/// <para>Bewusst genau ein Satz Einstellungen für Lesen und Schreiben. Zwei getrennte
/// Sätze wären der klassische Weg zu einer Datei, die sich schreiben, aber nicht mehr
/// lesen lässt.</para>
///
/// <para>Kommentare und nachgestellte Kommata sind beim Lesen erlaubt: Die Datei wird von
/// Hand gepflegt, und ein Techniker, der eine Zeile mit <c>//</c> stilllegt, soll dafür
/// keinen Ladefehler bekommen. Geschrieben werden sie nie — beim nächsten <c>Save</c> sind
/// sie fort, was in der Dokumentation des Einrichtungsassistenten steht.</para>
/// </remarks>
public static class ConfigJson
{
    /// <summary>Die Einstellungen. Nach der ersten Verwendung unveränderlich.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };
}
