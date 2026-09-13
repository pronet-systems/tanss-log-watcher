using System.Net;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Api.Repository;
using TanssLogWatcher.Api.Tests.Fakes;
using Xunit;

namespace TanssLogWatcher.Api.Tests;

/// <summary>
/// Prüft die Ticketprüfung gegen eine nachgebildete Verbindungsschicht.
/// </summary>
/// <remarks>
/// <para>Der Kern ist die Dreiteilung: <b>gefunden</b>, <b>gibt es nicht</b>, <b>nicht
/// prüfbar</b>. Der dritte Fall trägt die Last — nach Hausregel 5 darf eine misslungene Prüfung
/// das Buchen nicht verhindern, und im Abschlussdialog läuft dabei eine Schonfrist von fünf
/// Minuten.</para>
/// <para>Der 404-Rumpf in <see cref="ObjectNotFound"/> ist die <b>wörtliche</b> Antwort einer
/// Instanz der Fassung 10.10.0 vom 13.09.2026 auf <c>GET /api/v1/tickets/999999999</c>. Ein
/// ausgedachter Rumpf prüfte hier nichts: Die Unterscheidung „gibt es nicht“ gegen „nicht
/// ermittelt“ hängt genau an dem Wort <c>OBJECT_NOT_FOUND</c>.</para>
/// </remarks>
public sealed class TicketCheckTests
{
    /// <summary>Der gemessene Fehlerrumpf, Zeichen für Zeichen.</summary>
    private const string ObjectNotFound =
        """{"error":{"text":"OBJECT_NOT_FOUND","localizedText":"Das Objekt wurde nicht gefunden","type":"DataNotFoundException","traceId":"7f3c1a90-2b44-4f0e-9a21-6d5c8e0b1234"}}""";

    /// <summary>Ein Ticket, wie es die Messung zeigte — nur die Felder, die dieses Modell kennt.</summary>
    private const string TicketContent =
        """{"id":5000,"companyId":886,"title":"Serverstörung","statusId":2,"typeId":3}""";

    /// <summary>Der Umschlag der Messung: der Firmenname steht <b>nur</b> hier.</summary>
    private const string MetaWithCompany =
        """{"text":"ok","linkedEntities":{"companies":{"886":{"name":"Müller GmbH"}},"ticketStates":{"2":{"name":"In Arbeit"}}}}""";

    [Fact]
    public async Task Ein_Vorhandenes_Ticket_Kommt_Mit_Titel_Und_Firma()
    {
        RecordingHandler handler = new(HttpStatusCode.OK,
            TestEnvironment.Envelope(TicketContent, MetaWithCompany));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketRepository repository = new(client);

        TicketCheck check = await repository.CheckAsync(5000);

        Assert.Equal(TicketCheckOutcome.Exists, check.Outcome);
        Assert.True(check.IsCertain);
        Assert.False(check.BlocksBooking);
        Assert.Equal(886, check.Ticket?.CompanyId);
        Assert.Equal("Müller GmbH", check.CompanyName);

        // Das ist der eigentliche Zweck: Diese Zeile faengt den Zahlendreher auf ein anderes
        // vorhandenes Ticket. "Ticket 5000 gibt es" faengt ihn nicht.
        Assert.Equal("#5000 Serverstörung — Müller GmbH", check.Summary);
        Assert.EndsWith("/api/v1/tickets/5000", handler.Last.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("42", handler.Last.Query("loggedInUserId"));
    }

    [Fact]
    public async Task Der_Gemessene_404_Rumpf_Heisst_Gibt_Es_Nicht()
    {
        RecordingHandler handler = new(HttpStatusCode.NotFound, ObjectNotFound);
        using TanssClient client = TestEnvironment.Client(handler);
        TicketRepository repository = new(client);

        TicketCheck check = await repository.CheckAsync(999999999);

        Assert.Equal(TicketCheckOutcome.DoesNotExist, check.Outcome);
        Assert.True(check.IsCertain);
        Assert.True(check.BlocksBooking);
        Assert.Null(check.Ticket);
        Assert.Equal(404, check.Status);
        Assert.Contains("gibt es in TANSS nicht", check.Explanation, StringComparison.Ordinal);

        // Eine Absage ist kein Netzfehler und wird nicht wiederholt.
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Eine_403_Ist_Nicht_Pruefbar_Und_Haelt_Das_Buchen_Nicht_An()
    {
        // NICHT GEMESSEN, was eine 403 zu dieser Route bedeutet. Genau deshalb darf sie nicht
        // als "gibt es nicht" durchgehen - sonst erklaerte das Werkzeug ein vorhandenes Ticket
        // fuer erfunden und verweigerte die Buchung darauf.
        RecordingHandler handler = new(HttpStatusCode.Forbidden,
            TestEnvironment.Error("NO_RIGHT", "TnsNoRightException"));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketRepository repository = new(client);

        TicketCheck check = await repository.CheckAsync(5000);

        Assert.Equal(TicketCheckOutcome.Undetermined, check.Outcome);
        Assert.False(check.IsCertain);
        Assert.False(check.BlocksBooking);
        Assert.Null(check.Ticket);
        Assert.Equal(403, check.Status);
        Assert.Contains("nicht ermittelt", check.Explanation, StringComparison.Ordinal);
        Assert.Contains("Gebucht werden kann trotzdem", check.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ein_Netzfehler_Wirft_Nicht_Sondern_Bleibt_Unermittelt()
    {
        // Hausregel 5 in einem Satz: Die Leitung ist weg, der Bericht des Technikers bleibt.
        FailingHandler handler = new(static () => new HttpRequestException("Kein Netz"));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketRepository repository = new(client);

        TicketCheck check = await repository.CheckAsync(5000);

        Assert.Equal(TicketCheckOutcome.Undetermined, check.Outcome);
        Assert.False(check.BlocksBooking);
        Assert.Contains("nicht erreichbar", check.Explanation, StringComparison.Ordinal);
        Assert.NotNull(check.Failure);
    }

    [Fact]
    public async Task Eine_500_Wird_Wiederholt_Und_Endet_Dann_Unermittelt()
    {
        RecordingHandler handler = new(HttpStatusCode.InternalServerError,
            TestEnvironment.Error("BOOM"));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketRepository repository = new(client);

        TicketCheck check = await repository.CheckAsync(5000);

        Assert.Equal(TicketCheckOutcome.Undetermined, check.Outcome);
        Assert.False(check.BlocksBooking);
        Assert.Equal(500, check.Status);
        Assert.True(handler.Calls > 1, "Eine 5xx ist vorübergehend und gehört wiederholt.");
    }

    [Fact]
    public async Task Ein_404_Ohne_Object_Not_Found_Bleibt_Unermittelt()
    {
        // Ein 404 ohne den gemessenen Fehlertext kann auch heissen, dass es die Route nicht
        // mehr gibt. Daraus "Ticket gibt es nicht" zu machen, waere geraten (Hausregel 2).
        RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.NotFound));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketRepository repository = new(client);

        TicketCheck check = await repository.CheckAsync(5000);

        Assert.Equal(TicketCheckOutcome.Undetermined, check.Outcome);
        Assert.False(check.BlocksBooking);
        Assert.Contains("OBJECT_NOT_FOUND", check.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ohne_Firmennamen_Im_Umschlag_Steht_Die_Kennung_Statt_Eines_Namens()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope(TicketContent));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketRepository repository = new(client);

        TicketCheck check = await repository.CheckAsync(5000);

        Assert.Equal(TicketCheckOutcome.Exists, check.Outcome);
        Assert.Equal("Firma 886", check.CompanyName);
        Assert.Equal("#5000 Serverstörung — Firma 886", check.Summary);
    }

    [Fact]
    public async Task Ein_Ticket_Ohne_Firma_Bekommt_Keine_Erfundene()
    {
        RecordingHandler handler = new(HttpStatusCode.OK,
            TestEnvironment.Envelope("""{"id":5000,"companyId":0,"title":"Serverstörung"}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketRepository repository = new(client);

        TicketCheck check = await repository.CheckAsync(5000);

        Assert.Null(check.CompanyName);
        Assert.Equal("#5000 Serverstörung", check.Summary);
    }

    [Fact]
    public async Task Ohne_Ticketnummer_Wird_TANSS_Gar_Nicht_Erst_Gefragt()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope(TicketContent));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketRepository repository = new(client);

        TicketCheck check = await repository.CheckAsync(0);

        Assert.Equal(TicketCheckOutcome.NoTicketNumber, check.Outcome);
        Assert.False(check.BlocksBooking);
        Assert.Equal(0, handler.Calls);
        Assert.Empty(check.Summary);
    }

    [Fact]
    public async Task Ein_Leerer_Erfolg_Ist_Kein_Ticket()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.OK));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketRepository repository = new(client);

        TicketCheck check = await repository.CheckAsync(5000);

        Assert.Equal(TicketCheckOutcome.Undetermined, check.Outcome);
        Assert.Null(check.Ticket);
        Assert.False(check.BlocksBooking);
    }

    [Fact]
    public async Task Eine_Fremde_Ticketnummer_In_Der_Antwort_Bleibt_Unermittelt()
    {
        RecordingHandler handler = new(HttpStatusCode.OK,
            TestEnvironment.Envelope("""{"id":5001,"companyId":886,"title":"Etwas anderes"}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketRepository repository = new(client);

        TicketCheck check = await repository.CheckAsync(5000);

        Assert.Equal(TicketCheckOutcome.Undetermined, check.Outcome);
        Assert.Null(check.Ticket);
    }

    [Fact]
    public async Task Ein_Abbruch_Durch_Den_Aufrufer_Wird_Nicht_Verschluckt()
    {
        // Der Dialog bricht die Pruefung ab, wenn die Schonfrist ablaeuft. Wuerde der Abbruch
        // hier zu "nicht ermittelt", meldete das Werkzeug ein Pruefergebnis fuer einen Vorgang,
        // den der Aufrufer laengst fallengelassen hat.
        // Der Abbruch wird als Ausnahme der Verbindungsschicht nachgestellt UND die Marke ist
        // gesetzt: nur dann unterscheidet TanssClient ihn von einer Zeitueberschreitung, die zu
        // Recht als "nicht erreichbar" gilt.
        FailingHandler handler = new(static () => new TaskCanceledException());
        using TanssClient client = TestEnvironment.Client(handler);
        TicketRepository repository = new(client);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.CheckAsync(5000, cancelled.Token));
    }

    [Fact]
    public async Task Der_Schmale_Weg_Verhaelt_Sich_Unveraendert()
    {
        // FindAsync bleibt, wie es war: null heisst "gibt es nicht", alles andere wirft.
        RecordingHandler missing = new(HttpStatusCode.NotFound, ObjectNotFound);
        using TanssClient client = TestEnvironment.Client(missing);

        Assert.Null(await new TicketRepository(client).FindAsync(999999999));
        Assert.Null(await new TicketRepository(client).FindAsync(0));

        RecordingHandler denied = new(HttpStatusCode.Forbidden, TestEnvironment.Error("NO_RIGHT"));
        using TanssClient other = TestEnvironment.Client(denied);

        await Assert.ThrowsAsync<TanssAuthException>(
            () => new TicketRepository(other).FindAsync(5000));
    }
}
