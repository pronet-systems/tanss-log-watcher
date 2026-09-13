using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TanssLogWatcher.Api.Model;

/// <summary>
/// Woraus eine Leistung vorbelegt werden soll — für uns immer ein Timer.
/// </summary>
/// <remarks>
/// TANSS kennt weitere Typen; hier steht nur der, der nachgemessen ist. Ein erfundener Wert
/// käme als Leistung ohne Zeitabschnitte zurück, und das fiele erst beim Buchen auf.
/// </remarks>
/// <param name="Type">Die Quelle; <c>TIMER</c>.</param>
/// <param name="Id">Deren Kennung.</param>
public sealed record SupportInitializer(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] int Id)
{
    /// <summary>Ein Timer als Quelle.</summary>
    /// <param name="timerId">Die Timerkennung.</param>
    public static SupportInitializer Timer(int timerId) => new("TIMER", timerId);
}

/// <summary>Der Rumpf von <c>POST /api/v1/supports/properties</c>.</summary>
public sealed record SupportPropertiesRequest
{
    /// <summary>Die Quellen, aus denen TANSS die Leistung vorbelegen soll.</summary>
    [JsonPropertyName("initializers")]
    public required IReadOnlyList<SupportInitializer> Initializers { get; init; }
}

/// <summary>
/// Eine noch nicht gebuchte Leistung, so wie TANSS sie vorbelegt hat.
/// </summary>
/// <remarks>
/// <para><b>Warum hier ein <see cref="JsonObject"/> steht und kein Record mit Feldern.</b>
/// TANSS belegt beim Vorbereiten über achtzig Felder vor — Stundensatz, Abrechnungsart,
/// Fahrzeug, Zone, Kostenstelle, ein Dutzend Kennzeichen zur Fahrtabrechnung. Beim Anlegen
/// erwartet es sie zurück. Ein Record mit den fünf Feldern, die wir verstehen, hiesse: die
/// übrigen fünfundsiebzig fallen weg, und die Leistung wird mit Stundensatz 0 und ohne
/// Abrechnungsart gebucht. Der Fehler fiele in der Rechnungsstellung auf, nicht hier.</para>
///
/// <para>Deshalb bleibt der Block unangetastet, und geändert wird ausschliesslich, was der
/// Techniker im Dialog wirklich bearbeitet: der Text der Leistung, das Ticket und die
/// Beschreibung je Zeitabschnitt. Alles andere reisen wir unverändert durch.</para>
///
/// <para>Veränderlich und kein <c>record</c>: Der Dialog schreibt in die Abschnitte, während er
/// offen ist. Eine Kopie je Tastendruck wäre hier Zeremonie ohne Nutzen.</para>
/// </remarks>
public sealed class SupportDraft
{
    private const string TextField = "text";
    private const string TicketField = "ticketId";
    private const string TimerField = "usesTimerId";
    private const string ItemsField = "multiTextItems";

    private readonly JsonObject _root;

    private SupportDraft(JsonObject root)
    {
        _root = root;

        List<SupportSegment> segments = [];

        if (root[ItemsField] is JsonArray items)
        {
            foreach (JsonNode? item in items)
            {
                if (item is JsonObject entry)
                {
                    segments.Add(new SupportSegment(entry));
                }
            }
        }

        Segments = segments;
    }

    /// <summary>Übernimmt, was TANSS vorbelegt hat.</summary>
    /// <param name="content">Der <c>content</c>-Block der Antwort.</param>
    /// <exception cref="ArgumentException">Der Block ist kein Objekt.</exception>
    public static SupportDraft From(JsonNode content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return content is JsonObject root
            ? new SupportDraft(root)
            : throw new ArgumentException(
                "TANSS hat auf die Vorbereitung der Leistung kein Objekt geantwortet. Ohne die "
                + "vorbelegten Felder liesse sich die Leistung nur unvollständig anlegen.",
                nameof(content));
    }

    /// <summary>Der Timer, aus dem diese Leistung entstanden ist; 0, wenn keiner.</summary>
    public int TimerId => (int?)_root[TimerField] ?? 0;

    /// <summary>Die Zeitabschnitte des Timers, jeder mit eigener Beschreibung.</summary>
    /// <remarks>
    /// Einer je Laufabschnitt — genau die Gliederung, die der Techniker beim Starten und
    /// Anhalten erzeugt hat. Die Liste kommt aus TANSS und wird nicht ergänzt: Ein Abschnitt,
    /// den es dort nicht gibt, hätte auch keine Zeit, die er abrechnen könnte.
    /// </remarks>
    public IReadOnlyList<SupportSegment> Segments { get; }

    /// <summary>Der Text der Leistung — das, was auf der Rechnung steht.</summary>
    public string Text
    {
        get => (string?)_root[TextField] ?? string.Empty;
        set => _root[TextField] = value ?? string.Empty;
    }

    /// <summary>Das Ticket; 0 heisst „ohne Ticket“.</summary>
    public int TicketId
    {
        get => (int?)_root[TicketField] ?? 0;
        set => _root[TicketField] = value;
    }

    /// <summary>Die Gesamtdauer, wie TANSS sie gerechnet hat.</summary>
    public TimeSpan Duration => TimeSpan.FromMinutes((int?)_root["duration"] ?? 0);

    /// <summary>Der Beginn der Leistung; <c>null</c>, wenn TANSS keinen gesetzt hat.</summary>
    public DateTimeOffset? Date => TanssTime.FromUnixSeconds((long?)_root["date"] ?? 0);

    /// <summary>Der Block, so wie er an TANSS zurückgeht.</summary>
    public JsonObject Payload => _root;
}

/// <summary>
/// Ein Zeitabschnitt einer Leistung — in TANSS ein Eintrag in <c>multiTextItems</c>.
/// </summary>
/// <remarks>
/// <para>Ein Abschnitt entspricht einem Lauf des Timers: einmal gestartet, einmal angehalten.
/// Genau darum geht es beim Überführen — jede Unterbrechung ist ein eigener Arbeitsschritt, und
/// der gehört eigens beschrieben. Ein einziger Text über alles zusammen wäre für den Kunden
/// nicht nachvollziehbar.</para>
/// <para><b>Zwei Dauern, und beide sind echt.</b> <see cref="Duration"/> ist die gemessene Zeit,
/// <see cref="RoundedDuration"/> die, die TANSS abrechnet — gerundet nach den Regeln der
/// Instanz. Die Anzeige zeigt beide, wenn sie auseinandergehen; sonst hielte der Techniker die
/// Rundung für einen Fehler.</para>
/// </remarks>
public sealed class SupportSegment
{
    private readonly JsonObject _item;

    internal SupportSegment(JsonObject item) => _item = item;

    /// <summary>Die Stelle in der Reihenfolge, von TANSS vergeben.</summary>
    public int Index => (int?)_item["index"] ?? 0;

    /// <summary>Wann dieser Abschnitt begonnen hat; <c>null</c>, wenn ohne Zeitstempel.</summary>
    public DateTimeOffset? Date => TanssTime.FromUnixSeconds((long?)_item["date"] ?? 0);

    /// <summary>Die gemessene Dauer.</summary>
    public TimeSpan Duration => TimeSpan.FromMinutes((int?)_item["duration"] ?? 0);

    /// <summary>Die Dauer, die TANSS abrechnet.</summary>
    public TimeSpan RoundedDuration => TimeSpan.FromMinutes((int?)_item["roundedDuration"] ?? 0);

    /// <summary>Was in diesem Abschnitt getan wurde.</summary>
    public string Text
    {
        get => (string?)_item["text"] ?? string.Empty;
        set => _item["text"] = value ?? string.Empty;
    }

    /// <summary>Der Abschnitt in einer Zeile, für Anzeige und Protokoll.</summary>
    public override string ToString() => string.Create(CultureInfo.CurrentCulture,
        $"{Date?.ToLocalTime():t} ({Duration.TotalMinutes:0} min): {Text}");
}
