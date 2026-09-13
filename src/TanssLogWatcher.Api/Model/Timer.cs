using System.Text.Json.Serialization;

namespace TanssLogWatcher.Api.Model;

/// <summary>
/// Ein TANSS-Timer.
/// </summary>
/// <remarks>
/// Wer ein Feld <c>isRunning</c> erwartet, findet <b>keines</b>: ein Timer läuft genau dann,
/// wenn <see cref="StartTime"/> ungleich 0 ist. Kommentare hängen nicht am Timer, sondern an
/// seinen <see cref="Fragments"/>.
/// </remarks>
public sealed record TanssTimer
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("employeeId")] public int EmployeeId { get; init; }
    [JsonPropertyName("companyId")] public int CompanyId { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }

    /// <summary>Unix-Sekunden. 0 bedeutet: angehalten.</summary>
    [JsonPropertyName("startTime")] public long StartTime { get; init; }

    /// <summary>Bereits gelaufene Zeit in Sekunden.</summary>
    [JsonPropertyName("duration")] public long Duration { get; init; }

    [JsonPropertyName("ticketId")] public int TicketId { get; init; }
    [JsonPropertyName("linkTypeId")] public int LinkTypeId { get; init; }
    [JsonPropertyName("linkId")] public int LinkId { get; init; }
    [JsonPropertyName("callbackId")] public int CallbackId { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("fragments")] public IReadOnlyList<TimerFragment> Fragments { get; init; } = [];

    /// <summary>Läuft der Timer gerade? Abgeleitet, kein Serverfeld.</summary>
    [JsonIgnore] public bool IsRunning => StartTime != 0;

    /// <summary>Gesamtdauer einschließlich des laufenden Abschnitts.</summary>
    public TimeSpan Elapsed(DateTimeOffset now) =>
        // Ueber TanssTime, nicht ueber DateTimeOffset: StartTime kommt in TANSS-Sekunden vom
        // Server, und beide Seiten der Subtraktion muessen aus demselben Umrechner stammen.
        TimeSpan.FromSeconds(Duration + (IsRunning ? TanssTime.ToUnixSeconds(now) - StartTime : 0));
}

/// <summary>Ein neu anzulegender Timer — der Rumpf von <c>POST /api/v1/timers</c>.</summary>
/// <remarks>
/// <para>Die Felder sind genau die, die TANSS beim Anlegen entgegennimmt — ausgemessen
/// gegen eine Instanz der Fassung 10.10.0. Erwartet wird ein vollständiger Timer; geantwortet
/// wird mit 201 und dem angelegten Datensatz.</para>
/// <para><b>Drei Felder setzt der Server selbst und überschreibt sie:</b>
/// <see cref="StartTime"/> (immer „jetzt“), <see cref="Duration"/> (immer 0) und die
/// Mitarbeiterkennung (immer der angemeldete Techniker). Sie stehen hier trotzdem, weil der
/// Rumpf sonst kein vollständiger Timer wäre — verlassen darf man sich auf sie
/// nicht.</para>
/// <para><see cref="Note"/> ist keine Spalte, sondern der Text, mit dem TANSS den ersten
/// Laufabschnitt beschriftet. Bleibt er leer, nimmt der Server den Titel.</para>
/// </remarks>
public sealed record TimerDraft
{
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("ticketId")] public int TicketId { get; init; }
    [JsonPropertyName("companyId")] public int CompanyId { get; init; }
    [JsonPropertyName("callbackId")] public int CallbackId { get; init; }
    [JsonPropertyName("linkTypeId")] public int LinkTypeId { get; init; }
    [JsonPropertyName("linkId")] public int LinkId { get; init; }

    /// <summary>Wird von TANSS beim Anlegen auf den angemeldeten Techniker gesetzt.</summary>
    [JsonPropertyName("employeeId")] public int EmployeeId { get; init; }

    /// <summary>Wird von TANSS beim Anlegen auf den Anlegezeitpunkt gesetzt.</summary>
    [JsonPropertyName("startTime")] public long StartTime { get; init; }

    /// <summary>Wird von TANSS beim Anlegen auf 0 gesetzt.</summary>
    [JsonPropertyName("duration")] public long Duration { get; init; }

    /// <summary>Beschriftung des ersten Laufabschnitts; leer bedeutet: der Titel.</summary>
    [JsonPropertyName("note")] public string Note { get; init; } = string.Empty;
}

/// <summary>Ein Laufabschnitt eines Timers, mitsamt seiner Notiz.</summary>
public sealed record TimerFragment
{
    [JsonPropertyName("timerId")] public int TimerId { get; init; }
    [JsonPropertyName("startTime")] public long StartTime { get; init; }
    [JsonPropertyName("stopTime")] public long StopTime { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("hash")] public int Hash { get; init; }
}
