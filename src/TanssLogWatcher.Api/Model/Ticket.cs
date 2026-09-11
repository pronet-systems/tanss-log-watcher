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

/// <summary>Ein Techniker aus <c>/api/tanss.x/v1/technicians</c>.</summary>
public sealed record Technician
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("firstName")] public string? FirstName { get; init; }
    [JsonPropertyName("lastName")] public string? LastName { get; init; }
    [JsonPropertyName("emailAddress")] public string? EmailAddress { get; init; }
}
