using System.Net;
using System.Text.Json;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Api.Repository;
using TanssLogWatcher.Api.Tests.Fakes;
using Xunit;

namespace TanssLogWatcher.Api.Tests;

/// <summary>
/// Prüft das Anlegen eines Tickets gegen eine nachgebildete Gegenstelle.
/// </summary>
/// <remarks>
/// Die drei Fragen, um die es geht: Geht genau das hinaus, was hinausgehen darf — und nichts,
/// was ein Recht voraussetzt? Kommt die neue Nummer beim Aufrufer an? Und ergibt ein
/// Fehlschlag einen Satz, mit dem der Techniker etwas anfangen kann?
/// </remarks>
public sealed class TicketCreateTests
{
    /// <summary>Die Felder mit Rechtenummer. Keines davon darf jemals gesendet werden.</summary>
    private static readonly string[] Forbidden =
    [
        "assignedToDepartmentId", "deadlineDate", "dueDate", "serviceCapAmount", "reminder",
    ];

    [Fact]
    public async Task Der_Rumpf_Traegt_Genau_Die_Fuenf_Rechtefreien_Felder()
    {
        using RecordingHandler handler = new(HttpStatusCode.Created, Created(4711));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketCreator creator = new(client);

        await creator.CreateAsync(new TicketDraft
        {
            CompanyId = 886,
            Title = "Drucker druckt nicht",
            Content = "Seit heute früh kommt nichts mehr heraus.",
            RemitterId = 8094,
            AssignedToEmployeeId = 42,
        });

        CapturedRequest sent = handler.Last;
        Assert.Equal("POST", sent.Method);
        Assert.StartsWith(TestEnvironment.BaseUrl + "/api/v1/tickets", sent.Uri.ToString(),
                          StringComparison.Ordinal);

        // Auf /api/v1 ist der Parameter zwingend - ohne ihn antwortet TANSS mit 403.
        Assert.Equal("42", sent.Query("loggedInUserId"));

        using JsonDocument body = JsonDocument.Parse(sent.Body!);
        string[] fields = [.. body.RootElement.EnumerateObject().Select(field => field.Name)];

        Assert.Equal(5, fields.Length);
        Assert.Contains("companyId", fields, StringComparer.Ordinal);
        Assert.Contains("title", fields, StringComparer.Ordinal);
        Assert.Contains("content", fields, StringComparer.Ordinal);
        Assert.Contains("remitterId", fields, StringComparer.Ordinal);
        Assert.Contains("assignedToEmployeeId", fields, StringComparer.Ordinal);

        Assert.Equal(886, body.RootElement.GetProperty("companyId").GetInt32());
        Assert.Equal(8094, body.RootElement.GetProperty("remitterId").GetInt32());
        Assert.Equal(42, body.RootElement.GetProperty("assignedToEmployeeId").GetInt32());
        Assert.Equal("Drucker druckt nicht", body.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Kein_Feld_Mit_Rechtenummer_Geht_Hinaus()
    {
        // Ein fehlendes Recht liesse sonst den ganzen POST scheitern, und der Techniker saehe
        // nur einen Fehler - ohne zu erfahren, dass es an einem Feld lag, das er nie wollte.
        using RecordingHandler handler = new(HttpStatusCode.Created, Created(4711));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketCreator creator = new(client);

        await creator.CreateAsync(new TicketDraft
        {
            CompanyId = 886,
            Title = "Drucker druckt nicht",
            Content = "Seit heute früh kommt nichts mehr heraus.",
            RemitterId = 8094,
            AssignedToEmployeeId = 42,
        });

        using JsonDocument body = JsonDocument.Parse(handler.Last.Body!);
        foreach (string field in Forbidden)
        {
            Assert.False(body.RootElement.TryGetProperty(field, out _),
                         "Das Feld " + field + " setzt ein Recht voraus und darf nicht mitgehen.");
        }
    }

    [Fact]
    public async Task Was_Nicht_Gesetzt_Ist_Geht_Gar_Nicht_Erst_Hinaus()
    {
        // "remitterId": 0 waere die Behauptung, es gebe einen Auftraggeber mit der Kennung 0.
        // Ein fehlendes Feld behauptet nichts - und was TANSS aus einer 0 macht, ist unbelegt.
        using RecordingHandler handler = new(HttpStatusCode.Created, Created(4711));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketCreator creator = new(client);

        await creator.CreateAsync(new TicketDraft { CompanyId = 886, Title = "Nur das Nötigste" });

        using JsonDocument body = JsonDocument.Parse(handler.Last.Body!);
        string[] fields = [.. body.RootElement.EnumerateObject().Select(field => field.Name)];

        Assert.Equal(2, fields.Length);
        Assert.Contains("companyId", fields, StringComparer.Ordinal);
        Assert.Contains("title", fields, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Die_Antwort_Liefert_Die_Neue_Ticketnummer_Und_Den_Firmennamen()
    {
        using RecordingHandler handler = new(HttpStatusCode.Created, Created(4711));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketCreator creator = new(client);

        TicketCreateResult result = await creator.CreateAsync(
            new TicketDraft { CompanyId = 886, Title = "Drucker druckt nicht" });

        Assert.Equal(4711, result.TicketId);
        Assert.True(result.HasTicketId);
        Assert.Null(result.Warning);

        // Der Firmenname steht NUR im Umschlag - im Inhalt steht bloss die Kennung.
        Assert.Equal("ProNet Systems GmbH", result.CompanyName);
        Assert.Contains("4711", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ohne_Firmennamen_Im_Umschlag_Bleibt_Die_Kennung_Stehen()
    {
        using RecordingHandler handler = new(
            HttpStatusCode.Created,
            TestEnvironment.Envelope("""{"id":4711,"companyId":886}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketCreator creator = new(client);

        TicketCreateResult result = await creator.CreateAsync(
            new TicketDraft { CompanyId = 886, Title = "Drucker druckt nicht" });

        // Kein geratener Name, sondern die Kennung - sie laesst sich in TANSS nachschlagen.
        Assert.Equal("Firma 886", result.CompanyName);
    }

    [Fact]
    public async Task Die_Bemerkung_Aus_Meta_Properties_Kommt_Beim_Aufrufer_An()
    {
        using RecordingHandler handler = new(
            HttpStatusCode.Created,
            TestEnvironment.Envelope(
                """{"id":4711,"companyId":886}""",
                """{"properties":{"message":"Der Auftraggeber wurde ergänzt.","editable":false}}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketCreator creator = new(client);

        TicketCreateResult result = await creator.CreateAsync(
            new TicketDraft { CompanyId = 886, Title = "Drucker druckt nicht" });

        Assert.Equal("Der Auftraggeber wurde ergänzt.", result.Note);
        Assert.False(result.Editable);
        Assert.Contains("Der Auftraggeber wurde ergänzt.", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ohne_Meta_Properties_Wird_Nichts_Behauptet()
    {
        using RecordingHandler handler = new(HttpStatusCode.Created, Created(4711));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketCreator creator = new(client);

        TicketCreateResult result = await creator.CreateAsync(
            new TicketDraft { CompanyId = 886, Title = "Drucker druckt nicht" });

        Assert.Null(result.Note);
        Assert.Null(result.Editable);
    }

    [Fact]
    public async Task Eine_Antwort_Ohne_Nummer_Warnt_Statt_Zum_Zweiten_Versuch_Zu_Raten()
    {
        // Eine Ausnahme lese sich hier wie "nichts passiert" - und der zweite Versuch legte ein
        // zweites Ticket an. Es gibt keine Kennung, an der die Dublette zu erkennen waere.
        using RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.Created));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketCreator creator = new(client);

        TicketCreateResult result = await creator.CreateAsync(
            new TicketDraft { CompanyId = 886, Title = "Drucker druckt nicht" });

        Assert.Equal(0, result.TicketId);
        Assert.False(result.HasTicketId);
        Assert.NotNull(result.Warning);
        Assert.Contains("erneut senden", result.Warning!, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Ein_Fehlschlag_Ergibt_Eine_Brauchbare_Deutsche_Meldung()
    {
        using RecordingHandler handler = new(
            HttpStatusCode.Forbidden, TestEnvironment.Error("NO_RIGHT_FOR_COMPANY"));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketCreator creator = new(client);

        TanssException error = await Assert.ThrowsAnyAsync<TanssException>(
            () => creator.CreateAsync(new TicketDraft { CompanyId = 886, Title = "Drucker" }));

        // Der Typ bleibt erhalten - andernorts haengt die Tokenerneuerung daran.
        Assert.IsType<TanssAuthException>(error);

        string message = TicketCreator.Explain(error);
        Assert.Contains("Das Ticket wurde nicht angelegt", message, StringComparison.Ordinal);
        Assert.Contains(error.Message, message, StringComparison.Ordinal);

        // Die Ursachen sind ausdruecklich als ungeprueft gekennzeichnet (Hausregel 2).
        Assert.Contains("nicht belegt", message, StringComparison.Ordinal);
        Assert.Contains("ungeprüft", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ohne_Firma_Wird_Gar_Nicht_Erst_Gesendet()
    {
        // TANSS fuehrt Tickets ohne Firmenzuordnung in einer eigenen Liste
        // (/api/v1/tickets/notIdentified), in der sie niemand sieht.
        using RecordingHandler handler = new(HttpStatusCode.Created, Created(4711));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketCreator creator = new(client);

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => creator.CreateAsync(new TicketDraft { CompanyId = 0, Title = "Ohne Firma" }));

        Assert.Equal(0, handler.Calls);
        Assert.Contains("Ohne Firma", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ohne_Betreff_Wird_Gar_Nicht_Erst_Gesendet()
    {
        using RecordingHandler handler = new(HttpStatusCode.Created, Created(4711));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketCreator creator = new(client);

        await Assert.ThrowsAsync<ArgumentException>(
            () => creator.CreateAsync(new TicketDraft { CompanyId = 886, Title = "   " }));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Ein_Vollstaendiger_Entwurf_Nennt_Kein_Hindernis()
    {
        TicketDraft draft = new() { CompanyId = 886, Title = "Drucker druckt nicht" };

        Assert.True(draft.IsComplete);
        Assert.Null(draft.Problem);
    }

    /// <summary>Der Umschlag, wie TANSS ihn auf ein angelegtes Ticket schickt.</summary>
    /// <remarks>
    /// Die Nummer steht in <c>content.id</c>, der Firmenname ausschließlich im Umschlag unter
    /// <c>meta.linkedEntities.companies</c>.
    /// </remarks>
    private static string Created(int ticketId) =>
        TestEnvironment.Envelope(
            $$"""{"id":{{ticketId}},"companyId":886,"title":"Drucker druckt nicht","statusId":2,"typeId":3}""",
            """{"linkedEntities":{"companies":{"886":{"name":"ProNet Systems GmbH"}}}}""");
}
