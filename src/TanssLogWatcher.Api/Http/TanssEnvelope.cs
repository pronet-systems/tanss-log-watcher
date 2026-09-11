using System.Globalization;
using System.Text.Json;
using TanssLogWatcher.Api.Diagnostics;

namespace TanssLogWatcher.Api.Http;

/// <summary>
/// Packt den TANSS-Umschlag aus und übersetzt Fehlerantworten in die Ausnahmen dieses Moduls.
/// </summary>
/// <remarks>
/// <para>Erfolg trägt die Form <c>{"meta":{…},"content":…}</c>, Misserfolg die Form
/// <c>{"error":{"text","localizedText","type","traceId"}}</c>.</para>
/// <para><b>Der Status wird immer vor dem Rumpf befragt.</b> Stünde die Leerprüfung zuerst,
/// wäre eine leere 403 — die häufigste Antwort auf ein fehlendes <c>loggedInUserId</c> — ein
/// leerer Erfolg. Das Werkzeug würde die Sitzung als hochgeladen abhaken, obwohl TANSS sie
/// abgewiesen hat. Diese Reihenfolge ist die wichtigste Zeile Verhalten in der Netzschicht.</para>
/// </remarks>
internal static class TanssEnvelope
{
    /// <summary>Ein leerer <c>meta</c>-Block, damit Aufrufer nie gegen <c>null</c> prüfen müssen.</summary>
    public static IReadOnlyDictionary<string, object?> EmptyMeta { get; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Liest den Inhalt eines Erfolgsrumpfs. Ein leerer Rumpf ergibt den Standardwert —
    /// aber nur, weil der Aufrufer den Status bereits geprüft hat.
    /// </summary>
    public static (T? Content, IReadOnlyDictionary<string, object?> Meta) Unpack<T>(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return (default, EmptyMeta);
        }

        using JsonDocument document = Parse(payload);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            // Manche Routen antworten ohne Umschlag. Dann ist die Wurzel der Inhalt.
            return (Deserialize<T>(root), EmptyMeta);
        }

        IReadOnlyDictionary<string, object?> meta = root.TryGetProperty("meta", out JsonElement metaElement)
            ? ReadMeta(metaElement)
            : EmptyMeta;

        if (root.TryGetProperty("content", out JsonElement content))
        {
            return (content.ValueKind == JsonValueKind.Null ? default : Deserialize<T>(content), meta);
        }

        return (Deserialize<T>(root), meta);
    }

    /// <summary>
    /// Bildet eine Fehlerantwort auf die Ausnahme ab, die dem Aufrufer sagt, was zu tun ist.
    /// </summary>
    /// <remarks>
    /// Die Fallunterscheidung folgt der Aussagekraft, nicht dem Statuscode: <c>error.text</c>
    /// ist genauer als eine 403, die für ein halbes Dutzend Ursachen steht. Erst wenn der Text
    /// nichts hergibt, entscheidet der Status.
    /// </remarks>
    public static TanssException ToException(int status, string payload, string method, string path)
    {
        (string? text, string? localized, string? type, string? traceId) = ReadError(payload);
        string? detail = text ?? type;
        string where = string.Create(CultureInfo.InvariantCulture, $"{method} {path} -> HTTP {status}");
        string said = Describe(localized ?? text, type, traceId);

        // Text UND Typ befragen: TANSS meldet die fehlende Lizenz ueber error.type
        // (TnsModuleNotLicensedException), waehrend error.text nur eine Prosameldung traegt.
        bool Says(string needle) => Mentions(text, needle) || Mentions(type, needle);

        if (Says("TYPE_GREATER_1000"))
        {
            return new TanssRemoteSupportTypeException(
                where + ": Der Fernwartungstyp ist kleiner als 1000. TANSS nimmt über "
                + "/api/tanss.x/v1/remoteSupports nur externe Anbindungen ab 1000 an. "
                + "Die richtige typeId steht in /remoteSupports/systems." + said, status, detail);
        }

        if (Says("TYPE_DOESNT_EXIST"))
        {
            return new TanssRemoteSupportTypeException(
                where + ": Diesen Fernwartungstyp gibt es in TANSS nicht. Die Anbindung muss in "
                + "TANSS unter den externen Fernwartungs-Anbindungen angelegt sein, bevor dieses "
                + "Werkzeug sie benutzen kann." + said, status, detail);
        }

        if (Says("ModuleNotLicensed") || Says("MODULE_NOT_LICENSED"))
        {
            return new TanssModuleNotLicensedException(
                where + ": Das Modul Fernwartung ist auf dieser Instanz nicht lizenziert. Ohne "
                + "Lizenz kann dieses Werkzeug keine Fernwartungen schreiben; das Modul muss "
                + "erst freigeschaltet werden." + said, status, detail);
        }

        if (status == 404 || Says("OBJECT_NOT_FOUND") || Says("NOT_FOUND"))
        {
            return new TanssNotFoundException(
                where + ": TANSS kennt dieses Objekt nicht. Entweder ist die Kennung veraltet, "
                + "oder die Route gibt es in dieser TANSS-Fassung nicht mehr — die hier benutzten "
                + "Routen sind überwiegend undokumentiert und können mit einem Update wegfallen."
                + said, status, detail);
        }

        if (status is 401 or 403)
        {
            return new TanssAuthException(
                where + ": Zugriff verweigert. Vier Ursachen kommen in Frage, in dieser "
                + "Reihenfolge zu prüfen: (1) die Kopfzeile apiToken trägt nicht das Präfix "
                + "\"Bearer \" — ohne das überspringt TANSS die Prüfung und weist ab; (2) es "
                + "wurde das kurzlebige Token aus /api/v1/login benutzt statt eines über "
                + "/api/v1/jwts/tanss_app geprägten — jenes gilt auf /api/tanss.x/v1 nicht; "
                + "(3) auf /api/v1 fehlt der Parameter loggedInUserId, er ist dort zwingend; "
                + "(4) das Token ist abgelaufen, oder dem Mitarbeiter fehlt das Recht für diese "
                + "Aktion." + said, status, detail);
        }

        return new TanssException(where + ": TANSS hat die Anfrage abgewiesen." + said, status, detail);
    }

    /// <summary>Liest den <c>meta</c>-Block als flache Abbildung; die Werte bleiben JSON.</summary>
    /// <remarks>
    /// Die Werte werden bewusst als <see cref="JsonElement"/> durchgereicht statt in eine
    /// Objektstruktur gewandelt: <c>linkedEntities</c> ist eine nach Kennung geschlüsselte
    /// Abbildung wechselnder Tiefe, und jede feste Modellierung wäre nach dem nächsten
    /// TANSS-Update falsch. Der Aufrufer fragt gezielt ab, was er braucht.
    /// </remarks>
    private static IReadOnlyDictionary<string, object?> ReadMeta(JsonElement meta)
    {
        if (meta.ValueKind != JsonValueKind.Object)
        {
            return EmptyMeta;
        }

        Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in meta.EnumerateObject())
        {
            // Clone, weil das JsonDocument gleich wieder freigegeben wird.
            values[property.Name] = property.Value.Clone();
        }

        return values;
    }

    private static (string? Text, string? Localized, string? Type, string? TraceId) ReadError(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return (null, null, null, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null, null);
            }

            JsonElement error = root.TryGetProperty("error", out JsonElement nested) ? nested : root;
            if (error.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null, null);
            }

            return (ReadString(error, "text"), ReadString(error, "localizedText"),
                    ReadString(error, "type"), ReadString(error, "traceId"));
        }
        catch (JsonException)
        {
            // Ein unlesbarer Fehlerrumpf darf den Fehler selbst nicht verschlucken.
            return (null, null, null, null);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Mentions(string? detail, string needle) =>
        detail is not null && detail.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string Describe(string? message, string? type, string? traceId)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(message))
        {
            parts.Add(Redaction.Scrub(message));
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            parts.Add(type);
        }

        if (!string.IsNullOrWhiteSpace(traceId))
        {
            parts.Add("traceId " + traceId);
        }

        return parts.Count == 0 ? string.Empty : " TANSS meldet: " + string.Join(" / ", parts) + ".";
    }

    private static T? Deserialize<T>(JsonElement element)
    {
        try
        {
            return element.Deserialize<T>(TanssJson.Options);
        }
        catch (JsonException ex)
        {
            throw new TanssException(
                "TANSS hat geantwortet, aber der Inhalt passt nicht zum erwarteten Modell. Das "
                + "deutet auf eine geänderte TANSS-Fassung hin, nicht auf einen Bedienfehler: "
                + Redaction.Scrub(ex.Message), inner: ex);
        }
    }

    private static JsonDocument Parse(string payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new TanssException(
                "TANSS hat mit Erfolg geantwortet, aber der Rumpf ist kein JSON. Zeigt die "
                + "Basisadresse auf die Weboberfläche statt auf /backend, kommt hier HTML an: "
                + Redaction.Scrub(ex.Message), inner: ex);
        }
    }
}
