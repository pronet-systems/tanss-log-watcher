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

/// <summary>
/// Der Namensteil des <c>meta</c>-Blocks: macht aus einer Kennung einen Namen.
/// </summary>
/// <remarks>
/// <para><b>Warum es diesen Typ gibt.</b> TANSS liefert im Inhalt nur Zahlen —
/// <c>companyId</c>, <c>assignedToEmployeeId</c>, <c>statusId</c>. Der zugehörige Name steht
/// ausschließlich im Umschlag unter <c>meta.linkedEntities</c>. Nachgemessen am 13.09.2026
/// gegen eine Instanz der Fassung 10.10.0: <c>GET /api/v1/tickets/{id}</c> trägt dort
/// <c>companies</c>, <c>tickets</c>, <c>ticketStates</c>, <c>ticketTypes</c>,
/// <c>departments</c>, <c>employees</c>, <c>contracts</c>, <c>phases</c>, <c>orderBys</c> und
/// <c>costCenters</c>. Ohne diesen Weg bliebe dem Techniker die nackte Zahl.</para>
/// <para><b>Hausregel 2 steckt im Unterschied zwischen <see cref="Find(string, int)"/> und
/// <see cref="NameOf"/>.</b> <see cref="Find(string, int)"/> sagt mit <see langword="null"/>,
/// dass TANSS keinen Namen genannt hat. <see cref="NameOf"/> setzt an dessen Stelle
/// „Firma 886“ — die Kennung, sichtbar als solche. Es wird kein Name geraten und keiner
/// erfunden.</para>
/// <para><b>Was belegt ist und was nicht.</b> BELEGT (Beschreibung 10.10.0, Zeilen 286 ff. und
/// 748 ff.): die Objektform <c>{"companies":{"100000":{"name":"Firma #100000"}}}</c> — ein
/// Objekt je Bereich, die Kennung als Schlüssel, der Name im Feld <c>name</c>. Das Schema
/// selbst sagt zu <c>linkedEntities</c> nur <c>type: object</c>; verbindlich ist dort allein
/// das Beispiel, und die Messung deckt sich damit. NICHT BELEGT und deshalb nur abgefangen:
/// die Feldform <c>[{"id":100000,"name":"…"}]</c>. Sie wird gelesen, falls sie je auftaucht,
/// ist aber nirgends gemessen. Jede andere Form ergibt einen fehlenden Namen statt einer
/// Ausnahme: ein unlesbares Namensverzeichnis kostet den Namen, nie den Vorgang
/// (Hausregel 5).</para>
/// </remarks>
public sealed class LinkedEntities
{
    /// <summary>Bereichsname der Firmen.</summary>
    public const string Companies = "companies";

    /// <summary>Bereichsname der Mitarbeiter.</summary>
    public const string Employees = "employees";

    /// <summary>Bereichsname der Abteilungen.</summary>
    public const string Departments = "departments";

    /// <summary>Bereichsname der Ticketzustände.</summary>
    public const string TicketStates = "ticketStates";

    /// <summary>Bereichsname der Tickettypen.</summary>
    public const string TicketTypes = "ticketTypes";

    private readonly Dictionary<string, Dictionary<string, string>> _areas;

    private LinkedEntities(Dictionary<string, Dictionary<string, string>> areas) => _areas = areas;

    /// <summary>Ein leeres Verzeichnis — der Rückfall, wenn kein <c>meta</c> vorliegt.</summary>
    public static LinkedEntities Empty { get; } =
        new(new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Trägt dieses Verzeichnis überhaupt einen Namen?</summary>
    public bool IsEmpty => _areas.Count == 0;

    /// <summary>Die Bereiche, die tatsächlich mitgekommen sind — für die Fehlersuche.</summary>
    public IReadOnlyCollection<string> Areas => _areas.Keys;

    /// <summary>
    /// Liest das Namensverzeichnis aus einem <c>meta</c>-Block.
    /// </summary>
    /// <remarks>
    /// Wirft nicht. Fehlt <c>linkedEntities</c> oder hat es eine unerwartete Form, kommt
    /// <see cref="Empty"/> zurück, und <see cref="NameOf"/> zeigt anschließend die Kennung.
    /// Das ist die richtige Reihenfolge der Übel.
    /// </remarks>
    /// <param name="meta">Der <c>meta</c>-Block, so wie die Netzschicht ihn liefert.</param>
    /// <returns>Das Verzeichnis; niemals <see langword="null"/>.</returns>
    public static LinkedEntities From(IReadOnlyDictionary<string, object?>? meta)
    {
        if (meta is null || !meta.TryGetValue("linkedEntities", out object? linked)
            || linked is not JsonElement entities || entities.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        Dictionary<string, Dictionary<string, string>> areas = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty area in entities.EnumerateObject())
        {
            Dictionary<string, string>? names = ReadArea(area.Value);
            if (names is { Count: > 0 })
            {
                areas[area.Name] = names;
            }
        }

        return areas.Count == 0 ? Empty : new LinkedEntities(areas);
    }

    /// <summary>
    /// Sucht den Namen zu einer Kennung. <see langword="null"/> heißt: TANSS hat keinen genannt.
    /// </summary>
    /// <param name="area">Der Bereich, etwa <see cref="Companies"/>. Schreibweise gleichgültig.</param>
    /// <param name="id">Die Kennung aus dem Inhalt.</param>
    /// <returns>Der Name, oder <see langword="null"/>, wenn keiner vorliegt.</returns>
    public string? Find(string area, int id) =>
        Find(area, id.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Sucht den Namen zu einer Kennung in ihrer Textform.
    /// </summary>
    /// <remarks>
    /// Die Schlüssel in <c>linkedEntities</c> sind JSON-Zeichenketten. Diese Überladung greift
    /// sie unverändert ab, damit eine Kennung, die keine Zahl ist, nicht unterwegs verloren geht.
    /// </remarks>
    /// <param name="area">Der Bereich. Schreibweise gleichgültig.</param>
    /// <param name="id">Die Kennung als Text.</param>
    /// <returns>Der Name, oder <see langword="null"/>, wenn keiner vorliegt.</returns>
    public string? Find(string area, string id)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(id);

        return _areas.TryGetValue(area, out Dictionary<string, string>? names)
               && names.TryGetValue(id, out string? name)
            ? name
            : null;
    }

    /// <summary>
    /// Liefert den Namen — oder, wenn keiner vorliegt, die Kennung mit ihrer Bezeichnung.
    /// </summary>
    /// <remarks>
    /// Der Rückfall lautet „Firma 886“ und nicht „Unbekannt“: Die Kennung ist das einzige, was
    /// belegt ist, und sie lässt sich in TANSS nachschlagen. Ein Wort wie „Unbekannt“ wäre
    /// weniger wert als die Zahl selbst.
    /// </remarks>
    /// <param name="area">Der Bereich, etwa <see cref="Companies"/>.</param>
    /// <param name="id">Die Kennung aus dem Inhalt.</param>
    /// <param name="label">Die Bezeichnung für den Rückfall, etwa „Firma“.</param>
    /// <returns>Ein anzeigbarer Text; niemals leer.</returns>
    public string NameOf(string area, int id, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        return Find(area, id) ?? string.Create(CultureInfo.InvariantCulture, $"{label} {id}");
    }

    /// <summary>Der Firmenname zu einer <c>companyId</c>; sonst „Firma 886“.</summary>
    /// <param name="companyId">Die Firmenkennung aus dem Inhalt.</param>
    /// <returns>Ein anzeigbarer Text; niemals leer.</returns>
    public string CompanyName(int companyId) => NameOf(Companies, companyId, "Firma");

    /// <summary>Der Mitarbeitername zu einer Kennung; sonst „Mitarbeiter 8094“.</summary>
    /// <param name="employeeId">Die Mitarbeiterkennung aus dem Inhalt.</param>
    /// <returns>Ein anzeigbarer Text; niemals leer.</returns>
    public string EmployeeName(int employeeId) => NameOf(Employees, employeeId, "Mitarbeiter");

    /// <summary>Der Abteilungsname zu einer Kennung; sonst „Abteilung 1“.</summary>
    /// <param name="departmentId">Die Abteilungskennung aus dem Inhalt.</param>
    /// <returns>Ein anzeigbarer Text; niemals leer.</returns>
    public string DepartmentName(int departmentId) => NameOf(Departments, departmentId, "Abteilung");

    /// <summary>Der Zustandsname zu einer <c>statusId</c>; sonst „Status 2“.</summary>
    /// <param name="statusId">Die Zustandskennung aus dem Inhalt.</param>
    /// <returns>Ein anzeigbarer Text; niemals leer.</returns>
    public string TicketStateName(int statusId) => NameOf(TicketStates, statusId, "Status");

    /// <summary>Der Typname zu einer <c>typeId</c>; sonst „Typ 3“.</summary>
    /// <param name="typeId">Die Typkennung aus dem Inhalt.</param>
    /// <returns>Ein anzeigbarer Text; niemals leer.</returns>
    public string TicketTypeName(int typeId) => NameOf(TicketTypes, typeId, "Typ");

    /// <summary>
    /// Liest einen Bereich in beiden denkbaren Formen.
    /// </summary>
    /// <remarks>
    /// Objektform (belegt) und Feldform (nicht belegt, nur abgefangen) — siehe die Anmerkung
    /// am Typ. Alles andere ergibt <c>null</c> und damit einen fehlenden Bereich.
    /// </remarks>
    private static Dictionary<string, string>? ReadArea(JsonElement area)
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);

        if (area.ValueKind == JsonValueKind.Object)
        {
            // BELEGT: die Kennung ist der Schluessel, der Name steht im Feld "name".
            foreach (JsonProperty entry in area.EnumerateObject())
            {
                if (ReadName(entry.Value) is { } name)
                {
                    names[entry.Name] = name;
                }
            }

            return names;
        }

        if (area.ValueKind == JsonValueKind.Array)
        {
            // NICHT BELEGT: nirgends gemessen, nirgends beschrieben. Wird nur gelesen, damit
            // eine geaenderte TANSS-Fassung den Namen kostet und nicht den ganzen Vorgang.
            foreach (JsonElement entry in area.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || ReadKey(entry) is not { } key
                    || ReadName(entry) is not { } name)
                {
                    continue;
                }

                names[key] = name;
            }

            return names;
        }

        return null;
    }

    /// <summary>Liest das Feld <c>name</c>; Leertext gilt als kein Name.</summary>
    /// <remarks>
    /// Ein leeres <c>name</c> durchzureichen hiesse, eine leere Zeile anzuzeigen, wo die
    /// Kennung stehen koennte. Die Schreibweise des Feldes wird gleichgueltig behandelt -
    /// dieselbe Grosszuegigkeit, die TanssJson beim Lesen walten laesst, weil TANSS je nach
    /// Route anders schreibt.
    /// </remarks>
    private static string? ReadName(JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.String)
        {
            // NICHT BELEGT: ein Bereich, der die Kennung unmittelbar auf den Namen abbildet.
            string? direct = entry.GetString();
            return string.IsNullOrWhiteSpace(direct) ? null : direct;
        }

        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in entry.EnumerateObject())
        {
            if (!string.Equals(property.Name, "name", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? value = property.Value.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    /// <summary>Liest die Kennung eines Eintrags der Feldform — Zahl oder Zeichenkette.</summary>
    private static string? ReadKey(JsonElement entry)
    {
        foreach (JsonProperty property in entry.EnumerateObject())
        {
            if (!string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.String => property.Value.GetString(),
                _ => null,
            };
        }

        return null;
    }
}
