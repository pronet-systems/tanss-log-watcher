using System.Text.Json.Serialization;

namespace TanssLogWatcher.Api.Model;

/// <summary>
/// Eine Fernwartung, so wie TANSS sie entgegennimmt.
/// </summary>
/// <remarks>
/// Die Feldnamen sind die der Schnittstelle und damit bindend.
/// Nicht enthalten und bewusst nicht sendbar:
/// <list type="bullet">
///   <item><c>id</c> — könnte serverseitig auf eine bestehende Zeile binden.</item>
///   <item><c>fee</c> — TeamViewer-Altlast.</item>
///   <item><c>typeName</c> — reines Anzeigefeld des Servers.</item>
/// </list>
/// <para><c>companyId</c>, <c>linkTypeId</c> und <c>linkId</c> bleiben auf 0: sie sind der
/// <b>Geräteplatz</b>, nicht der Ticketplatz, und werden serverseitig gemeinsam überschrieben,
/// sobald die Geräteübersetzung greift. Der Ticketbezug läuft über <see cref="TicketId"/>.</para>
/// </remarks>
public sealed record RemoteSupportWrite
{
    /// <summary>Kennung der externen Anbindung, muss mindestens 1000 sein und in TANSS existieren.</summary>
    [JsonPropertyName("typeId")]
    public required int TypeId { get; init; }

    /// <summary>TANSS-Mitarbeiter-ID. Ein Wert ungleich 0 gewinnt immer und trägt die gesamte Attribution.</summary>
    [JsonPropertyName("employeeId")]
    public required int EmployeeId { get; init; }

    /// <summary>Unix-Sekunden. Siehe <see cref="TanssTime"/>.</summary>
    [JsonPropertyName("startTime")]
    public required long StartTime { get; init; }

    /// <summary>Unix-Sekunden. 0 speichert SQL NULL und bedeutet: läuft noch.</summary>
    [JsonPropertyName("endTime")]
    public required long EndTime { get; init; }

    /// <summary>Unsere Sitzungskennung. Grundlage der Wiederholungsprüfung in der Warteschlange.</summary>
    [JsonPropertyName("remoteMaintenanceId")]
    public required string RemoteMaintenanceId { get; init; }

    [JsonPropertyName("comment")]
    public string Comment { get; init; } = string.Empty;

    /// <summary>Undokumentiert, aber wirksam: erzeugt einen echten Ticketbezug. 0 = keiner.</summary>
    [JsonPropertyName("ticketId")]
    public int TicketId { get; init; }

    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; init; } = string.Empty;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = string.Empty;
}

/// <summary>Eine Fernwartung, wie TANSS sie zurückgibt.</summary>
public sealed record RemoteSupportRead
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("remoteMaintenanceId")] public string RemoteMaintenanceId { get; init; } = string.Empty;
    [JsonPropertyName("typeId")] public int TypeId { get; init; }
    [JsonPropertyName("typeName")] public string? TypeName { get; init; }
    [JsonPropertyName("employeeId")] public int EmployeeId { get; init; }
    [JsonPropertyName("userId")] public string? UserId { get; init; }
    [JsonPropertyName("userName")] public string? UserName { get; init; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; init; }
    [JsonPropertyName("deviceName")] public string? DeviceName { get; init; }
    [JsonPropertyName("companyId")] public int CompanyId { get; init; }
    [JsonPropertyName("linkTypeId")] public int LinkTypeId { get; init; }
    [JsonPropertyName("linkId")] public int LinkId { get; init; }
    [JsonPropertyName("startTime")] public long StartTime { get; init; }
    [JsonPropertyName("endTime")] public long EndTime { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
    [JsonPropertyName("ticketId")] public int TicketId { get; init; }
}

/// <summary>
/// Eine externe Fernwartungsanbindung, wie sie in TANSS unter
/// „Externe Fernwartungs-Anbindungen verwalten“ gepflegt wird.
/// </summary>
public sealed record RemoteSupportSystem
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    /// <summary>Hintergrundfarbe als sechsstelliger Hexwert ohne Raute, etwa <c>00bfff</c>.</summary>
    [JsonPropertyName("backgroundColor")] public string? BackgroundColor { get; init; }

    /// <summary>Leistungstyp, mit dem TANSS diese Fernwartung in eine Leistung wandelt.</summary>
    [JsonPropertyName("supportTypeId")] public int SupportTypeId { get; init; }
}

/// <summary>
/// Das Ergebnis eines Anlegevorgangs samt Befund zur Attribution.
/// </summary>
/// <remarks>
/// Steht beim Modell und nicht beim Repository: Seit die Gegenprobe zum Vertrag gehört
/// (<c>IRemoteSupportRepository.CreateWithDiagnosticsAsync</c>), darf die
/// Vertragsschicht nicht auf eine Umsetzung zeigen müssen, um ihren eigenen Rückgabetyp
/// zu kennen.
/// </remarks>
/// <param name="Support">Der angelegte Datensatz, so wie TANSS ihn zurückgibt.</param>
/// <param name="AttributionConfirmed">
/// Hat TANSS den Mitarbeiter in <c>meta.linkedEntities.employees</c> ausgewiesen?
/// </param>
/// <param name="Warning">
/// Der Hinweistext, falls die Attribution nicht bestätigt ist — sonst <c>null</c>. Er ist zum
/// Protokollieren und Anzeigen gedacht, nicht zum Wiederholen des Aufrufs.
/// </param>
public sealed record RemoteSupportCreateResult(RemoteSupportRead Support, bool AttributionConfirmed,
                                               string? Warning);

/// <summary>Zeitfenster für Filterabfragen. Grenzen in Unix-Sekunden.</summary>
public sealed record Timeframe
{
    [JsonPropertyName("from")] public required long From { get; init; }
    [JsonPropertyName("till")] public required long Till { get; init; }
}
