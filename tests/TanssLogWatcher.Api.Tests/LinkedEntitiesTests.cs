using System.Net;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Api.Tests.Fakes;
using Xunit;

namespace TanssLogWatcher.Api.Tests;

/// <summary>
/// Prüft den Zugriff auf <c>meta.linkedEntities</c> — den einzigen Ort, an dem der Name zu
/// einer Kennung steht.
/// </summary>
/// <remarks>
/// Zwei Dinge stehen hier auf dem Spiel. Erstens darf der neue Weg die bestehenden nicht
/// anfassen: <c>GetAsync</c> muss sich Zeichen für Zeichen verhalten wie zuvor. Zweitens ist
/// die Form von <c>linkedEntities</c> nur als Objekt belegt; die Feldform ist geraten und darf
/// deshalb höchstens den Namen kosten, niemals den Vorgang.
/// </remarks>
public sealed class LinkedEntitiesTests
{
    /// <summary>
    /// Der <c>meta</c>-Block, wie <c>GET /api/v1/tickets/{id}</c> ihn am 13.09.2026 lieferte.
    /// </summary>
    /// <remarks>
    /// Die Schlüsselnamen stammen aus der Messung, die Werte sind erfunden — erfunden ist hier
    /// zulässig, weil der Test die <b>Form</b> prüft und nicht einen Datenbestand behauptet.
    /// </remarks>
    private const string TicketMeta = """
        {"text":"Object found",
         "linkedEntities":{
           "companies":{"886":{"name":"Beispiel Systems GmbH"}},
           "employees":{"8094":{"initials":"tm","name":"Mitarbeiter, Test"}},
           "ticketStates":{"2":{"image":"auge.gif","name":"gesichtet"}},
           "ticketTypes":{"3":{"name":"Störung"}},
           "departments":{"1":{"name":"Technik"}},
           "tickets":{}},
         "properties":{"editable":true}}
        """;

    private const string TicketContent = """
        {"id":142953,"title":"Drucker klemmt","companyId":886,"statusId":2,"typeId":3}
        """;

    [Fact]
    public async Task GetWithMeta_Liefert_Inhalt_Und_Den_Firmennamen()
    {
        // Der ganze Zweck: die companyId steht im Inhalt, der Name nur im Umschlag.
        RecordingHandler handler = new(HttpStatusCode.OK,
            TestEnvironment.Envelope(TicketContent, TicketMeta));
        using TanssClient client = TestEnvironment.Client(handler);

        (Ticket? ticket, IReadOnlyDictionary<string, object?> meta) =
            await client.GetWithMetaAsync<Ticket>(TanssRoutes.TicketById(142953));

        Assert.NotNull(ticket);
        Assert.Equal(886, ticket.CompanyId);
        Assert.Equal("Beispiel Systems GmbH", LinkedEntities.From(meta).CompanyName(ticket.CompanyId));
    }

    [Fact]
    public async Task GetWithMeta_Haelt_Sich_An_Die_Praefixregel()
    {
        RecordingHandler handler = new(HttpStatusCode.OK,
            TestEnvironment.Envelope(TicketContent, TicketMeta));
        using TanssClient client = TestEnvironment.Client(handler);

        await client.GetWithMetaAsync<Ticket>(TanssRoutes.TicketById(142953));

        Assert.Equal(
            TestEnvironment.EmployeeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            handler.Last.Query("loggedInUserId"));
    }

    [Fact]
    public async Task GetAsync_Bleibt_Unveraendert_Und_Ignoriert_Meta()
    {
        // Der bestehende Weg wurde nicht angefasst: derselbe Umschlag, nur der Inhalt kommt an.
        RecordingHandler handler = new(HttpStatusCode.OK,
            TestEnvironment.Envelope(TicketContent, TicketMeta));
        using TanssClient client = TestEnvironment.Client(handler);

        Ticket? ticket = await client.GetAsync<Ticket>(TanssRoutes.TicketById(142953));

        Assert.Equal(142953, ticket?.Id);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GetWithMeta_Wird_Wie_GetAsync_Wiederholt()
    {
        // Die Wiederholungsregel darf nicht davon abhaengen, ob der Aufrufer meta haben will.
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.InternalServerError, TestEnvironment.Error("BOOM")));
        using TanssClient client = TestEnvironment.Client(handler);

        await Assert.ThrowsAsync<TanssException>(
            () => client.GetWithMetaAsync<Ticket>(TanssRoutes.TicketById(142953)));

        Assert.Equal(5, handler.Calls);
    }

    [Fact]
    public async Task GetWithMeta_Meldet_Das_Gemessene_404_Als_Nicht_Gefunden()
    {
        // Gemessen: GET /api/v1/tickets/999999999 -> 404 mit OBJECT_NOT_FOUND. Der Status wird
        // vor dem Rumpf befragt, sonst waere eine leere Fehlerantwort ein leerer Erfolg.
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.NotFound,
            TestEnvironment.Error("OBJECT_NOT_FOUND", "DataNotFoundException")));
        using TanssClient client = TestEnvironment.Client(handler);

        await Assert.ThrowsAsync<TanssNotFoundException>(
            () => client.GetWithMetaAsync<Ticket>(TanssRoutes.TicketById(999999999)));
    }

    [Fact]
    public async Task PutWithMeta_Liefert_Meta_Und_Laeuft_Genau_Einmal()
    {
        // Die Firmensuche ist ein PUT, das liest - wiederholt wird trotzdem nicht.
        RecordingHandler handler = new(HttpStatusCode.OK,
            TestEnvironment.Envelope("[" + TicketContent + "]", TicketMeta));
        using TanssClient client = TestEnvironment.Client(handler);

        (List<Ticket>? tickets, IReadOnlyDictionary<string, object?> meta) =
            await client.PutWithMetaAsync<List<Ticket>>(
                TanssRoutes.TicketSearch, new TicketSearch { Staff = [42] });

        Assert.Single(tickets!);
        Assert.Equal("gesichtet", LinkedEntities.From(meta).TicketStateName(2));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PutWithMeta_Wird_Nach_Einem_Fehler_Nicht_Wiederholt()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.InternalServerError, TestEnvironment.Error("BOOM")));
        using TanssClient client = TestEnvironment.Client(handler);

        await Assert.ThrowsAsync<TanssException>(
            () => client.PutWithMetaAsync<List<Ticket>>(TanssRoutes.TicketSearch, new { }));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Objektform_Ist_Die_Belegte_Form()
    {
        // Beschreibung 10.10.0, Zeile 286 ff.: Kennung als Schluessel, Name im Feld "name".
        LinkedEntities names = await ReadAsync(TicketMeta);

        Assert.False(names.IsEmpty);
        Assert.Equal("Beispiel Systems GmbH", names.CompanyName(886));
        Assert.Equal("Mitarbeiter, Test", names.EmployeeName(8094));
        Assert.Equal("Technik", names.DepartmentName(1));
        Assert.Equal("gesichtet", names.TicketStateName(2));
        Assert.Equal("Störung", names.TicketTypeName(3));
    }

    [Fact]
    public async Task Feldform_Stuerzt_Nicht_Ab_Obwohl_Sie_Nicht_Belegt_Ist()
    {
        // NICHT gemessen. Der Test haelt nur fest, dass diese Form den Namen nicht kostet -
        // und vor allem keine Ausnahme wirft.
        LinkedEntities names = await ReadAsync(
            """{"linkedEntities":{"companies":[{"id":886,"name":"Erste GmbH"},{"id":"887","name":"Zweite GmbH"}]}}""");

        Assert.Equal("Erste GmbH", names.CompanyName(886));
        Assert.Equal("Zweite GmbH", names.CompanyName(887));
    }

    [Fact]
    public async Task Fehlender_Name_Wird_Zur_Kennung_Und_Nicht_Erfunden()
    {
        // Hausregel 2: lieber "Firma 886" als ein geratener Name.
        LinkedEntities names = await ReadAsync("""{"linkedEntities":{"companies":{"1":{"name":"Eine GmbH"}}}}""");

        Assert.Null(names.Find(LinkedEntities.Companies, 886));
        Assert.Equal("Firma 886", names.CompanyName(886));
        Assert.Equal("Mitarbeiter 8094", names.EmployeeName(8094));
    }

    [Fact]
    public async Task Leerer_Name_Gilt_Nicht_Als_Name()
    {
        // Eine leere Zeile anzuzeigen waere weniger wert als die Kennung.
        LinkedEntities names = await ReadAsync("""{"linkedEntities":{"companies":{"886":{"name":"   "}}}}""");

        Assert.Equal("Firma 886", names.CompanyName(886));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"text":"Object found"}""")]
    [InlineData("""{"linkedEntities":null}""")]
    [InlineData("""{"linkedEntities":[]}""")]
    [InlineData("""{"linkedEntities":"nichts"}""")]
    [InlineData("""{"linkedEntities":{"companies":5}}""")]
    [InlineData("""{"linkedEntities":{"companies":{"886":7}}}""")]
    public async Task Unerwartete_Formen_Kosten_Den_Namen_Und_Nicht_Den_Vorgang(string meta)
    {
        // Hausregel 5. Keine dieser Formen ist gemessen; keine darf werfen.
        LinkedEntities names = await ReadAsync(meta);

        Assert.True(names.IsEmpty);
        Assert.Equal("Firma 886", names.CompanyName(886));
    }

    [Fact]
    public void Ohne_Meta_Kommt_Das_Leere_Verzeichnis()
    {
        Assert.True(LinkedEntities.From(null).IsEmpty);
        Assert.Same(LinkedEntities.Empty, LinkedEntities.From(null));
        Assert.Equal("Firma 886", LinkedEntities.Empty.CompanyName(886));
    }

    [Fact]
    public async Task Bereichsnamen_Werden_Ohne_Ruecksicht_Auf_Schreibweise_Gefunden()
    {
        // TANSS schreibt je nach Route unterschiedlich; ein verlorener Name waere schlimmer
        // als eine grosszuegige Zuordnung - dieselbe Linie wie in TanssJson.
        LinkedEntities names = await ReadAsync("""{"linkedEntities":{"Companies":{"886":{"Name":"Eine GmbH"}}}}""");

        Assert.Equal("Eine GmbH", names.CompanyName(886));
        Assert.Equal("Eine GmbH", names.Find("COMPANIES", 886));
    }

    [Fact]
    public async Task Areas_Nennt_Nur_Die_Bereiche_Mit_Inhalt()
    {
        // "tickets":{} kam leer mit - ein leerer Bereich ist kein Bereich.
        LinkedEntities names = await ReadAsync(TicketMeta);

        Assert.Contains(LinkedEntities.Companies, names.Areas);
        Assert.DoesNotContain("tickets", names.Areas);
    }

    [Fact]
    public async Task Nicht_Zahlige_Kennungen_Gehen_Nicht_Verloren()
    {
        LinkedEntities names = await ReadAsync("""{"linkedEntities":{"companies":{"A-12":{"name":"Eine GmbH"}}}}""");

        Assert.Equal("Eine GmbH", names.Find(LinkedEntities.Companies, "A-12"));
    }

    /// <summary>
    /// Holt einen meta-Block auf demselben Weg, den auch die Anwendung nimmt.
    /// </summary>
    /// <remarks>
    /// Bewusst ueber die nachgebildete Verbindungsschicht und nicht ueber den Auspacker
    /// unmittelbar: geprueft werden soll, was bei einem echten Aufruf ankommt.
    /// </remarks>
    private static async Task<LinkedEntities> ReadAsync(string meta)
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("{}", meta));
        using TanssClient client = TestEnvironment.Client(handler);

        (Ticket? _, IReadOnlyDictionary<string, object?> unpacked) =
            await client.GetWithMetaAsync<Ticket>(TanssRoutes.TicketById(1));

        return LinkedEntities.From(unpacked);
    }
}
