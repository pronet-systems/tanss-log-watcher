using System.Net;
using System.Text.Json;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Api.Repository;
using TanssLogWatcher.Api.Tests.Fakes;
using Xunit;

namespace TanssLogWatcher.Api.Tests;

/// <summary>Prüft die Repositories gegen eine Attrappe der TANSS-Antworten.</summary>
public sealed class RepositoryTests
{
    private const string SessionId = "6f1b0e4a-0000-4000-8000-000000000001";

    private static RemoteSupportWrite Session(long start = 1_757_500_000) => new()
    {
        TypeId = 1003,
        EmployeeId = 42,
        StartTime = start,
        EndTime = start + 600,
        RemoteMaintenanceId = SessionId,
        Comment = "PuTTY-Sitzung",
    };

    [Fact]
    public async Task Anlegen_Geht_Ohne_LoggedInUserId_Auf_Tanss_X()
    {
        RecordingHandler handler = new(HttpStatusCode.Created, TestEnvironment.Envelope(
            """{"id":38584,"remoteMaintenanceId":"6f1b0e4a-0000-4000-8000-000000000001"}""",
            """{"linkedEntities":{"employees":{"42":{"id":42}}}}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client, expectedEmployeeId: 42);

        RemoteSupportRead created = await repository.CreateAsync(Session());

        Assert.Equal(38584, created.Id);
        Assert.Equal("POST", handler.Last.Method);
        Assert.Equal(TestEnvironment.BaseUrl + "/api/tanss.x/v1/remoteSupports",
                     handler.Last.Uri.ToString());
        Assert.Null(handler.Last.Query("loggedInUserId"));
    }

    [Fact]
    public async Task Anlegen_Bestaetigt_Die_Attribution_Aus_Dem_Meta_Block()
    {
        RecordingHandler handler = new(HttpStatusCode.Created, TestEnvironment.Envelope(
            """{"id":38584,"employeeId":42}""",
            """{"linkedEntities":{"employees":{"42":{"id":42}},"remoteSupportTypes":{"1003":{"name":"Putty / Kitty"}}}}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client, expectedEmployeeId: 42);

        RemoteSupportCreateResult result = await repository.CreateWithDiagnosticsAsync(Session());

        Assert.True(result.AttributionConfirmed);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task Fehlende_Attribution_Ist_Eine_Warnung_Und_Kein_Fehler()
    {
        // Werfen waere hier falsch: der Aufrufer wuerde wiederholen, und TANSS legte einen
        // zweiten Datensatz an.
        RecordingHandler handler = new(HttpStatusCode.Created, TestEnvironment.Envelope(
            """{"id":38585,"employeeId":0}""",
            """{"linkedEntities":{"employees":{"7":{"id":7}}}}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client, expectedEmployeeId: 42);

        RemoteSupportCreateResult result = await repository.CreateWithDiagnosticsAsync(Session());

        Assert.False(result.AttributionConfirmed);
        Assert.NotNull(result.Warning);
        Assert.Contains("38585", result.Warning, StringComparison.Ordinal);
        Assert.Contains("Nicht erneut senden", result.Warning, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Ein_Meta_Block_Ganz_Ohne_LinkedEntities_Gilt_Als_Unbestaetigt()
    {
        RecordingHandler handler = new(HttpStatusCode.Created,
                                       TestEnvironment.Envelope("""{"id":38586}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client, expectedEmployeeId: 42);

        RemoteSupportCreateResult result = await repository.CreateWithDiagnosticsAsync(Session());

        Assert.False(result.AttributionConfirmed);
    }

    [Fact]
    public async Task Anlegen_Ohne_Inhalt_Wirft_Und_Verbietet_Die_Wiederholung()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.Created));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client, expectedEmployeeId: 42);

        TanssException error = await Assert.ThrowsAsync<TanssException>(
            () => repository.CreateAsync(Session()));

        Assert.Contains("dedupliziert nicht", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Der_Gesendete_Rumpf_Traegt_Alle_Felder()
    {
        RecordingHandler handler = new(HttpStatusCode.Created,
                                       TestEnvironment.Envelope("""{"id":1}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client, expectedEmployeeId: 42);

        await repository.CreateAsync(Session());

        using JsonDocument body = JsonDocument.Parse(handler.Last.Body ?? "{}");
        Assert.Equal(1003, body.RootElement.GetProperty("typeId").GetInt32());
        Assert.Equal(42, body.RootElement.GetProperty("employeeId").GetInt32());
        Assert.Equal(SessionId, body.RootElement.GetProperty("remoteMaintenanceId").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("ticketId").GetInt32());
        // Ohne Geraetekennung gehoert das Feld nicht in den Rumpf: Ein leeres "deviceId"
        // ginge sonst bei JEDER Fernwartung hinaus und luede zu einer Zuordnung auf die
        // leere Zeichenkette ein. Bis heute war genau das der Fall.
        Assert.False(body.RootElement.TryGetProperty("deviceId", out _));
    }

    [Fact]
    public async Task Existenzpruefung_Spannt_Ein_Fenster_Von_Plus_Minus_Einem_Tag()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope(
            $$"""[{"id":38584,"remoteMaintenanceId":"{{SessionId}}"}]"""));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client);
        DateTimeOffset around = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

        bool exists = await repository.ExistsAsync(SessionId, around);

        Assert.True(exists);
        Assert.Equal("PUT", handler.Last.Method);
        Assert.Equal("42", handler.Last.Query("loggedInUserId"));

        using JsonDocument body = JsonDocument.Parse(handler.Last.Body ?? "{}");
        JsonElement timeframe = body.RootElement.GetProperty("timeframe");
        Assert.Equal(TanssTime.ToUnixSeconds(around.AddDays(-1)),
                     timeframe.GetProperty("from").GetInt64());
        Assert.Equal(TanssTime.ToUnixSeconds(around.AddDays(1)),
                     timeframe.GetProperty("till").GetInt64());
        Assert.Equal(SessionId, body.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Ein_Treffer_Nur_Im_Kommentar_Gilt_Nicht_Als_Vorhanden()
    {
        // Der Textfilter durchsucht comment UND remoteMaintenanceId. Ohne den genauen
        // Vergleich wuerde eine GUID im Kommentar einen Upload vortaeuschen - und die
        // Sitzung ginge verloren.
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope(
            $$"""[{"id":1,"remoteMaintenanceId":"eine-andere-id","comment":"siehe {{SessionId}}"}]"""));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client);

        Assert.False(await repository.ExistsAsync(SessionId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Eine_Leere_Liste_Heisst_Nicht_Vorhanden()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client);

        Assert.False(await repository.ExistsAsync(SessionId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Die_Leseabfrage_Wird_Bei_Fuenfhundert_Wiederholt()
    {
        // Sie ist ein PUT, aber lesend - das Repository weiss das, der Client kann es nicht.
        RecordingHandler handler = new(request => request.Attempt < 3
            ? RecordingHandler.Respond(HttpStatusCode.InternalServerError, TestEnvironment.Error("BOOM"))
            : RecordingHandler.Respond(HttpStatusCode.OK, TestEnvironment.Envelope("""[{"id":1}]""")));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client, 42, TestEnvironment.FastRetry);

        IReadOnlyList<RemoteSupportRead> found = await repository.ListAsync(
            new Timeframe { From = 1, Till = 2 });

        Assert.Equal(3, handler.Calls);
        Assert.Single(found);
    }

    [Fact]
    public async Task Ohne_Textfilter_Wird_Das_Feld_Weggelassen()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client);

        await repository.ListAsync(new Timeframe { From = 1, Till = 2 });

        using JsonDocument body = JsonDocument.Parse(handler.Last.Body ?? "{}");
        Assert.False(body.RootElement.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task Ein_Objekt_Statt_Eines_Feldes_Ist_Ein_Fehler_Und_Keine_Leere_Liste()
    {
        // Gemessen am 11.09.2026: content ist ein schlichtes Feld. Jede andere Form waere eine
        // geaenderte TANSS-Fassung - und stillschweigend als "nichts gefunden" durchgewinkt
        // liesse sie die Existenzpruefung in einen doppelten Upload laufen.
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope(
            """{"remoteSupports":[{"id":4711}]}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client, 42, TestEnvironment.FastRetry);

        TanssException error = await Assert.ThrowsAsync<TanssException>(
            () => repository.ListAsync(new Timeframe { From = 1, Till = 2 }));

        Assert.Contains("ein Objekt", error.Message, StringComparison.Ordinal);
        Assert.Contains("dedupliziert nicht", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Eine_Unlesbare_Antwort_Beendet_Die_Existenzpruefung_Mit_Einem_Fehler()
    {
        // Der gefaehrliche Fall: "false" hiesse "noch nicht hochgeladen" und wuerde den
        // zweiten Upload ausloesen. Lieber gar keine Antwort als eine falsche.
        RecordingHandler handler = new(HttpStatusCode.OK,
                                       TestEnvironment.Envelope("""{"text":"Object found"}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client, 42, TestEnvironment.FastRetry);

        await Assert.ThrowsAsync<TanssException>(
            () => repository.ExistsAsync(SessionId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Die_Existenzpruefung_Umspannt_Die_Ganze_Sitzung()
    {
        // Eine Fernwartung ueber drei Tage faellt aus jedem festen Tagesfenster heraus. Das
        // Fenster kommt deshalb aus der Sitzung selbst, mit grosszuegigem Zuschlag.
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope(
            $$"""[{"id":38584,"remoteMaintenanceId":"{{SessionId}}"}]"""));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client);

        DateTimeOffset start = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset end = start.AddDays(3);
        RemoteSupportWrite session = Session(TanssTime.ToUnixSeconds(start)) with
        {
            EndTime = TanssTime.ToUnixSeconds(end),
        };

        Assert.True(await repository.ExistsAsync(session));

        using JsonDocument body = JsonDocument.Parse(handler.Last.Body ?? "{}");
        JsonElement timeframe = body.RootElement.GetProperty("timeframe");
        Assert.Equal(TanssTime.ToUnixSeconds(start.AddDays(-1)),
                     timeframe.GetProperty("from").GetInt64());
        Assert.Equal(TanssTime.ToUnixSeconds(end.AddDays(1)),
                     timeframe.GetProperty("till").GetInt64());
    }

    [Fact]
    public async Task Eine_Noch_Laufende_Sitzung_Wird_Bis_Jetzt_Gesucht()
    {
        // endTime 0 heisst in TANSS "laeuft noch". Als Sekunde 0 gelesen spannte das Fenster
        // bis 1970 zurueck und der Server suchte den halben Datenbestand ab.
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client);
        DateTimeOffset start = DateTimeOffset.UtcNow.AddHours(-2);

        await repository.ExistsAsync(Session(TanssTime.ToUnixSeconds(start)) with { EndTime = 0 });

        using JsonDocument body = JsonDocument.Parse(handler.Last.Body ?? "{}");
        JsonElement timeframe = body.RootElement.GetProperty("timeframe");
        Assert.Equal(TanssTime.ToUnixSeconds(start.AddDays(-1)),
                     timeframe.GetProperty("from").GetInt64());
        Assert.True(timeframe.GetProperty("till").GetInt64()
                    >= TanssTime.ToUnixSeconds(DateTimeOffset.UtcNow.AddDays(1).AddMinutes(-1)));
    }

    [Fact]
    public async Task Vertauschte_Grenzen_Ergeben_Trotzdem_Ein_Fenster()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope(
            $$"""[{"id":38584,"remoteMaintenanceId":"{{SessionId}}"}]"""));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client);
        DateTimeOffset early = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset late = early.AddHours(6);

        Assert.True(await repository.ExistsAsync(SessionId, late, early));

        using JsonDocument body = JsonDocument.Parse(handler.Last.Body ?? "{}");
        JsonElement timeframe = body.RootElement.GetProperty("timeframe");
        Assert.True(timeframe.GetProperty("from").GetInt64()
                    < timeframe.GetProperty("till").GetInt64());
    }

    [Fact]
    public async Task Anbindungen_Werden_Von_Der_Integrationsroute_Gelesen()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope(
            """[{"id":1001,"name":"Microsoft Outlook","backgroundColor":"00bfff","supportTypeId":53}]"""));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client);

        IReadOnlyList<RemoteSupportSystem> systems = await repository.ListSystemsAsync();

        RemoteSupportSystem system = Assert.Single(systems);
        Assert.Equal(1001, system.Id);
        Assert.Equal("00bfff", system.BackgroundColor);
        Assert.Equal(53, system.SupportTypeId);
        Assert.Null(handler.Last.Query("loggedInUserId"));
    }

    [Fact]
    public async Task Eigene_Tickets_Kommen_Mit_LoggedInUserId()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope(
            """[{"id":5000,"title":"Drucker streikt"}]"""));
        using TanssClient client = TestEnvironment.Client(handler);
        TicketRepository repository = new(client);

        IReadOnlyList<Ticket> tickets = await repository.ListOwnAsync();

        Assert.Equal("#5000 Drucker streikt", Assert.Single(tickets).ToString());
        Assert.Equal("42", handler.Last.Query("loggedInUserId"));
    }

    [Fact]
    public async Task Techniker_Kommen_Ohne_LoggedInUserId()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope(
            """[{"id":42,"name":"Michel","emailAddress":"technik@example.invalid"}]"""));
        using TanssClient client = TestEnvironment.Client(handler);
        TechnicianRepository repository = new(client);

        IReadOnlyList<Technician> technicians = await repository.ListAsync();

        Assert.Equal(42, Assert.Single(technicians).Id);
        Assert.Null(handler.Last.Query("loggedInUserId"));
    }

    [Fact]
    public async Task Eine_Leere_Antwort_Ergibt_Eine_Leere_Liste_Statt_Null()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.OK));
        using TanssClient client = TestEnvironment.Client(handler);

        Assert.Empty(await new TicketRepository(client).ListOwnAsync());
        Assert.Empty(await new TechnicianRepository(client).ListAsync());
        Assert.Empty(await new TimerRepository(client).ListAsync());
    }

    [Fact]
    public async Task Der_Umschalter_Spricht_Den_Timer_Ueber_Den_Pfad_An()
    {
        RecordingHandler handler = new(HttpStatusCode.Accepted, TestEnvironment.Envelope(
            """{"id":9,"startTime":1757500000,"duration":30}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        TimerRepository repository = new(client);

        TanssTimer timer = await repository.ToggleAsync(9);

        Assert.True(timer.IsRunning);
        Assert.Equal("PUT", handler.Last.Method);
        Assert.Equal(TestEnvironment.BaseUrl + "/api/v1/timers/9?loggedInUserId=42",
                     handler.Last.Uri.ToString());
    }

    [Fact]
    public async Task Loeschen_Schickt_Die_Kennung_Im_Rumpf()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.NoContent));
        using TanssClient client = TestEnvironment.Client(handler);
        TimerRepository repository = new(client);

        await repository.DeleteAsync(9);

        Assert.Equal("DELETE", handler.Last.Method);
        using JsonDocument body = JsonDocument.Parse(handler.Last.Body ?? "{}");
        Assert.Equal(9, body.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Notizen_Haengen_Am_Timer_Und_Nicht_Am_Kommentar()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope(
            """[{"timerId":9,"startTime":1757500000,"stopTime":1757500600,"note":"Neustart"}]"""));
        using TanssClient client = TestEnvironment.Client(handler);
        TimerRepository repository = new(client);

        IReadOnlyList<TimerFragment> notes = await repository.ListNotesAsync(9);

        Assert.Equal("Neustart", Assert.Single(notes).Note);
        Assert.Equal(TestEnvironment.BaseUrl + "/api/v1/timers/notes/9?loggedInUserId=42",
                     handler.Last.Uri.ToString());
    }

    [Fact]
    public async Task Ohne_Rumpfloeschen_Meldet_Der_Timer_Einen_Programmierfehler()
    {
        // TANSS nimmt die Kennung ausschliesslich im Rumpf entgegen. Ein
        // Rueckfallweg ueber ?id= wuerde still nichts loeschen - das faellt niemandem auf.
        LimitedClient client = new();
        TimerRepository repository = new(client);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.DeleteAsync(9));

        Assert.Contains("Programmierfehler", error.Message, StringComparison.Ordinal);
        Assert.Contains("ITanssBodyDelete", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Ein_Einzelner_Timer_Kommt_Ueber_Den_Pfad()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope(
            """{"id":9,"title":"Fernwartung"}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        TimerRepository repository = new(client);

        TanssTimer timer = await repository.GetAsync(9);

        Assert.Equal(9, timer.Id);
        Assert.Equal("GET", handler.Last.Method);
        Assert.Equal(TestEnvironment.BaseUrl + "/api/v1/timers/9?loggedInUserId=42",
                     handler.Last.Uri.ToString());
    }

    [Fact]
    public async Task Der_Umschalter_Sendet_Keinen_Rumpf()
    {
        // PUT /api/v1/timers/{id} nimmt keinen @RequestBody. Ein mitgeschickter Rumpf waere
        // bestenfalls wirkungslos.
        RecordingHandler handler = new(HttpStatusCode.Accepted, TestEnvironment.Envelope(
            """{"id":9,"startTime":0,"duration":600}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        TimerRepository repository = new(client);

        await repository.ToggleAsync(9);

        Assert.True(string.IsNullOrEmpty(handler.Last.Body));
    }

    [Fact]
    public async Task Ein_Timer_Traegt_Alle_Setzbaren_Felder()
    {
        RecordingHandler handler = new(HttpStatusCode.Created, TestEnvironment.Envelope(
            """{"id":9}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        TimerRepository repository = new(client);

        await repository.CreateAsync(new TimerDraft
        {
            Title = "Fernwartung",
            TicketId = 5000,
            CompanyId = 77,
            CallbackId = 3,
            LinkTypeId = 8,
            LinkId = 12,
            Note = "PuTTY",
        });

        Assert.Equal("POST", handler.Last.Method);
        using JsonDocument body = JsonDocument.Parse(handler.Last.Body ?? "{}");
        JsonElement sent = body.RootElement;
        Assert.Equal("Fernwartung", sent.GetProperty("title").GetString());
        Assert.Equal(5000, sent.GetProperty("ticketId").GetInt32());
        Assert.Equal(77, sent.GetProperty("companyId").GetInt32());
        Assert.Equal(3, sent.GetProperty("callbackId").GetInt32());
        Assert.Equal(8, sent.GetProperty("linkTypeId").GetInt32());
        Assert.Equal(12, sent.GetProperty("linkId").GetInt32());
        Assert.Equal("PuTTY", sent.GetProperty("note").GetString());
        Assert.Equal(0, sent.GetProperty("employeeId").GetInt32());
        Assert.Equal(0, sent.GetProperty("startTime").GetInt64());
        Assert.Equal(0, sent.GetProperty("duration").GetInt64());
    }

    [Fact]
    public async Task Eine_Notiz_Geht_Per_Post_An_Den_Timerpfad()
    {
        // Anhaengen laeuft ueber POST /api/v1/timers/notes/{id}; PUT /api/v1/timers/notes
        // aenderte stattdessen einen bestehenden Abschnitt.
        RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.Accepted));
        using TanssClient client = TestEnvironment.Client(handler);
        TimerRepository repository = new(client);

        await repository.AddNoteAsync(9, "Fernwartung beendet");

        Assert.Equal("POST", handler.Last.Method);
        Assert.Equal(TestEnvironment.BaseUrl + "/api/v1/timers/notes/9?loggedInUserId=42",
                     handler.Last.Uri.ToString());

        using JsonDocument body = JsonDocument.Parse(handler.Last.Body ?? "{}");
        // Die timerId steht auch im Rumpf: der Handler prueft die Berechtigung darauf, bevor
        // er sie aus dem Pfad ueberschreibt - die Zugriffspruefung liest den Rumpfwert.
        Assert.Equal(9, body.RootElement.GetProperty("timerId").GetInt32());
        Assert.Equal("Fernwartung beendet", body.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Eine_Leere_Notiz_Erreicht_Den_Server_Gar_Nicht()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.Accepted));
        using TanssClient client = TestEnvironment.Client(handler);
        TimerRepository repository = new(client);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddNoteAsync(9, "   "));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Ein_Bestehender_Abschnitt_Wird_Mit_Seiner_Pruefsumme_Geaendert()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.Accepted));
        using TanssClient client = TestEnvironment.Client(handler);
        TimerRepository repository = new(client);

        TimerFragment fragment = new()
        {
            TimerId = 9,
            StartTime = 1_757_500_000,
            StopTime = 1_757_500_600,
            Note = "alt",
            Hash = 4711,
        };

        await repository.UpdateNoteAsync(fragment, "neu");

        Assert.Equal("PUT", handler.Last.Method);
        Assert.Equal(TestEnvironment.BaseUrl + "/api/v1/timers/notes?loggedInUserId=42",
                     handler.Last.Uri.ToString());

        using JsonDocument body = JsonDocument.Parse(handler.Last.Body ?? "{}");
        JsonElement sent = body.RootElement;
        Assert.Equal(9, sent.GetProperty("timerId").GetInt32());
        Assert.Equal(1_757_500_000, sent.GetProperty("startTime").GetInt64());
        Assert.Equal(1_757_500_600, sent.GetProperty("stopTime").GetInt64());
        Assert.Equal("neu", sent.GetProperty("note").GetString());
        // Ohne die gelesene Pruefsumme lehnt TANSS mit TIMER_NOTE_HAS_BEEN_CHANGED ab.
        Assert.Equal(4711, sent.GetProperty("hash").GetInt32());
    }

    [Fact]
    public async Task Ein_Timer_Wird_Mit_Titel_Und_Ticket_Angelegt()
    {
        RecordingHandler handler = new(HttpStatusCode.Created, TestEnvironment.Envelope(
            """{"id":9,"title":"Fernwartung","ticketId":5000}"""));
        using TanssClient client = TestEnvironment.Client(handler);
        TimerRepository repository = new(client);

        TanssTimer created = await repository.CreateAsync("Fernwartung", 5000);

        Assert.Equal(9, created.Id);
        using JsonDocument body = JsonDocument.Parse(handler.Last.Body ?? "{}");
        Assert.Equal("Fernwartung", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(5000, body.RootElement.GetProperty("ticketId").GetInt32());
    }

    [Fact]
    public async Task Ein_Typfehler_Beim_Anlegen_Erreicht_Den_Aufrufer_Unveraendert()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.Forbidden, TestEnvironment.Error("TYPE_DOESNT_EXIST")));
        using TanssClient client = TestEnvironment.Client(handler);
        RemoteSupportRepository repository = new(client, 42);

        TanssRemoteSupportTypeException error =
            await Assert.ThrowsAsync<TanssRemoteSupportTypeException>(
                () => repository.CreateAsync(Session() with { TypeId = 4711 }));

        Assert.Equal("TYPE_DOESNT_EXIST", error.Detail);
        Assert.Equal(1, handler.Calls);
    }
}
