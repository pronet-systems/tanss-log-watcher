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
/// <para><c>linkTypeId</c> und <c>linkId</c> sind weiterhin <b>nicht</b> abgebildet, und das
/// ist Absicht: Sie sind der <b>Geräteplatz</b>, und die ganze Zahl, die dort für „PC“ steht,
/// ist nicht gemessen. Die Aufzählung <c>PC | COMPANY | EMPLOYEE | …</c> steht in der
/// Beschreibung nur als <i>Zeichenkette</i> (unter <c>/api/v1/tanssEvents/rules</c>), während
/// diese Route eine Zahl verlangt; das Beispiel bei <c>assignDevice</c> legt die 1 nahe und
/// beweist sie nicht. Eine geratene Zahl verknüpfte die Fernwartung mit einem beliebigen
/// anderen Datensatz — der Ticketbezug läuft über <see cref="TicketId"/>, das Gerät steht in
/// <see cref="DeviceName"/>.</para>
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

    /// <summary>
    /// Die Kennung, unter der TANSS das Gerät wiedererkennt — oder <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>TANSS übersetzt sie in eine Firma.</b> Ist zu dieser Kennung eine Zuordnung
    /// hinterlegt (<c>/api/remoteSupports/v1/assignDevice</c>), setzt TANSS beim Anlegen die
    /// <c>companyId</c> selbst ein. Nachgemessen am 13.09.2026 gegen eine Instanz der Fassung
    /// 10.10.0, und zwar über die hier benutzte Route <c>/api/tanss.x/v1/remoteSupports</c>:
    /// mit hinterlegter Zuordnung kam die Firma zurück, ohne Zuordnung eine 0.</para>
    ///
    /// <para><b>Ausdrücklich <see langword="null"/>-fähig, und ausdrücklich ausgelassen, wenn
    /// sie fehlt.</b> Der Serialisierer dieses Hauses schreibt sonst jedes Feld mit
    /// (<c>DefaultIgnoreCondition.Never</c>) — es ging bis heute bei <b>jeder</b> Fernwartung
    /// ein leeres <c>"deviceId"</c> hinaus. Ein leerer Wert ist keine Kennung, und ihn zu
    /// senden hiesse, eine Zuordnung auf die leere Zeichenkette zu ermöglichen.</para>
    ///
    /// <para>Gebildet wird sie an genau einer Stelle:
    /// <c>TanssLogWatcher.Monitoring.DeviceIdentity</c>. Dort steht auch der Riegel gegen den
    /// Platzhalter und gegen Profile, deren Ziel kein Gerät benennt.</para>
    /// </remarks>
    [JsonPropertyName("deviceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceId { get; init; }

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>
    /// Die Firma, auf die gebucht wird — oder 0, und dann geht das Feld gar nicht hinaus.
    /// </summary>
    /// <remarks>
    /// <para><b>Dieses Werkzeug setzt es nicht.</b> Es bildet die Schnittstelle ab, mehr nicht.
    /// Der Abschlussdialog hat eine Zeit lang nach der Firma gefragt; das ist wieder entfernt
    /// worden, weil an der Firma in TANSS der Weg zur Leistung und damit zur Rechnung hängt —
    /// und in einen fakturierenden Vorgang greift dieses Werkzeug nicht hinein.</para>
    ///
    /// <para>Die Firma setzt <b>TANSS selbst</b>, sofern zur Gerätekennung eine Zuordnung
    /// hinterlegt ist (<c>/api/remoteSupports/v1/assignDevice</c>). Ohne sie kam gemessen eine
    /// 0 zurück — die Fernwartung hängt dann an keinem Kunden, und nachzutragen ist sie in
    /// TANSS.</para>
    ///
    /// <para><b><c>WhenWritingDefault</c> und nicht <c>Never</c>:</b> Der Serialisierer dieses
    /// Hauses schriebe sonst bei <i>jeder</i> Fernwartung ein <c>"companyId": 0</c> mit, und
    /// eine gesendete 0 ist etwas anderes als ein fehlendes Feld — sie könnte eine vorhandene
    /// serverseitige Zuordnung überschreiben.</para>
    ///
    /// <para><b>NICHT GEMESSEN</b> ist, ob die benutzte Route
    /// <c>/api/tanss.x/v1/remoteSupports</c> dieses Feld übernimmt oder es zugunsten ihrer
    /// eigenen Geräteübersetzung verwirft. Belegt ist zweierlei: Das Anlegeschema des
    /// dokumentierten Weges <c>POST /api/remoteSupports/v1</c> nimmt <c>companyId</c> entgegen
    /// (Beschreibung 10.10.0, Zeile 2601–2609), und die Antwort der hier benutzten Route trägt
    /// <c>companyId</c> (siehe <see cref="RemoteSupportRead.CompanyId"/>). Der Abschlussdialog
    /// sagt dem Techniker beides — er soll nicht glauben, eine Zusage zu bekommen, die niemand
    /// nachgemessen hat (Hausregel 2).</para>
    /// </remarks>
    [JsonPropertyName("companyId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CompanyId { get; init; }
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

/// <summary>
/// Wie die Suche nach der Firma zu einer Gerätekennung ausgegangen ist.
/// </summary>
/// <remarks>
/// <b>Drei Ausgänge und nicht zwei.</b> Der Unterschied zwischen <see cref="NoMatch"/> und
/// <see cref="Undetermined"/> ist der ganze Zweck dieser Aufzählung: „zu diesem Gerät gab es
/// noch keine Fernwartung mit Firma“ und „die Frage liess sich nicht klären“ sehen beide wie
/// eine leere Antwort aus und bedeuten das Gegenteil.
/// </remarks>
public enum DeviceCompanyOutcome
{
    /// <summary>Es wurde gar nicht gefragt — die Sitzung führt keine Gerätekennung mit.</summary>
    /// <remarks>
    /// Kein Fehler, sondern der Regelfall bei Profilen, deren Fenstertitel morgen etwas anderes
    /// bezeichnet als heute; siehe <c>MonitoringProfile.YieldsDeviceIdentity</c>.
    /// </remarks>
    NoDeviceId,

    /// <summary>TANSS hat dieses Gerät schon einmal einer Firma zugeordnet.</summary>
    Found,

    /// <summary>
    /// Gefragt, und keine frühere Fernwartung dieses Geräts trug eine Firma.
    /// </summary>
    /// <remarks>
    /// <b>Das heisst nicht „es gibt keine Zuordnung“.</b> Ein Gerät, das gerade erst in TANSS
    /// hinterlegt wurde, steht in der Übersetzungstabelle und trotzdem in keiner vergangenen
    /// Fernwartung.
    /// </remarks>
    NoMatch,

    /// <summary><b>Nicht ermittelt.</b> Die Abfrage ist gescheitert.</summary>
    Undetermined,
}

/// <summary>
/// Der Befund zu einer Gerätekennung: welche Firma TANSS ihr zugeordnet hat — und wie belastbar
/// das ist.
/// </summary>
/// <param name="Outcome">Wie die Antwort zu lesen ist.</param>
/// <param name="CompanyId">Die gefundene Firma; 0 in jedem anderen Ausgang.</param>
/// <param name="Explanation">Ein fertiger deutscher Satz für die Oberfläche.</param>
/// <param name="SeenAt">
/// Wann die Fernwartung stattfand, aus der die Firma stammt — oder <see langword="null"/>. Sie
/// steht im Dialog, weil eine Zuordnung von gestern anders wiegt als eine von vor einem Jahr.
/// </param>
public sealed record DeviceCompanyMatch(DeviceCompanyOutcome Outcome, int CompanyId,
                                        string Explanation, DateTimeOffset? SeenAt = null)
{
    /// <summary>Steht eine Firma fest?</summary>
    public bool HasCompany => Outcome == DeviceCompanyOutcome.Found && CompanyId > 0;
}
