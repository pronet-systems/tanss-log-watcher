using System.Text.Json.Serialization;

namespace TanssLogWatcher.Api.Model;

/// <summary>Ein Ticket, reduziert auf das, was die Ticketauswahl beim Sitzungsende braucht.</summary>
public sealed record Ticket
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("companyId")] public int CompanyId { get; init; }
    [JsonPropertyName("statusId")] public int StatusId { get; init; }
    [JsonPropertyName("typeId")] public int TypeId { get; init; }

    public override string ToString() => $"#{Id} {Title}";
}

/// <summary>
/// Der Filter der Ticketsuche.
/// </summary>
/// <remarks>
/// <para>Nur die Felder, die dieses Werkzeug braucht. Die Schnittstelle nimmt weit mehr
/// entgegen — Abteilungen, Projekte, Zeitfenster, Kennzeichen —, aber was nicht gesendet wird,
/// gilt als nicht gefiltert, und ein Feld abzubilden, das niemand setzt, ist ein Feld, das
/// irgendwann falsch gesetzt wird.</para>
/// <para><b>Ohne <see cref="Staff"/> kommt alles zurück, was der Anmeldende sehen darf</b> —
/// nachgemessen 101 Tickets gegenüber 20 eigenen. Für eine Auswahl beim Buchen ist das zu
/// viel; gebucht wird auf das eigene Ticket.</para>
/// </remarks>
public sealed record TicketSearch
{
    /// <summary>Die Mitarbeiter, deren Tickets gesucht werden.</summary>
    [JsonPropertyName("staff")] public IReadOnlyList<int> Staff { get; init; } = [];

    /// <summary>
    /// Sollen auch erledigte Tickets mitkommen?
    /// </summary>
    /// <remarks>
    /// Vorbelegt mit <c>false</c>, und das ist keine Sparsamkeit: Auf ein erledigtes Ticket zu
    /// buchen ist fast immer ein Versehen. Nachgemessen kamen mit <c>true</c> 572 statt 20
    /// Tickets zurück — die Auswahl wäre unbrauchbar.
    /// </remarks>
    [JsonPropertyName("includeDoneTickets")] public bool IncludeDoneTickets { get; init; }
}

/// <summary>Ein Techniker aus <c>/api/tanss.x/v1/technicians</c>.</summary>
public sealed record Technician
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("firstName")] public string? FirstName { get; init; }
    [JsonPropertyName("lastName")] public string? LastName { get; init; }
    [JsonPropertyName("emailAddress")] public string? EmailAddress { get; init; }
}
