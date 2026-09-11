using System.Net;
using System.Text.Json;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Api.Tests.Fakes;
using Xunit;

namespace TanssLogWatcher.Api.Tests;

/// <summary>Prüft die Netzschicht dort, wo Fehler teuer werden.</summary>
public sealed class TanssClientTests
{
    [Fact]
    public async Task LoggedInUserId_Wird_Auf_Api_V1_Angehaengt()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        using TanssClient client = TestEnvironment.Client(handler);

        await client.GetAsync<List<Ticket>>(TanssRoutes.OwnTickets);

        Assert.Equal(TestEnvironment.EmployeeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     handler.Last.Query("loggedInUserId"));
    }

    [Fact]
    public async Task LoggedInUserId_Fehlt_Auf_Tanss_X()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        using TanssClient client = TestEnvironment.Client(handler);

        await client.GetAsync<List<Technician>>(TanssRoutes.Technicians);

        Assert.Null(handler.Last.Query("loggedInUserId"));
    }

    [Fact]
    public async Task LoggedInUserId_Wird_Auf_Tanss_X_Sogar_Entfernt()
    {
        // Selbst wenn der Aufrufer ihn von Hand setzt, darf er die Integrationsroute nicht erreichen.
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        using TanssClient client = TestEnvironment.Client(handler);
        Dictionary<string, string?> query = new(StringComparer.Ordinal) { ["loggedInUserId"] = "9999" };

        await client.GetAsync<List<Technician>>(TanssRoutes.Technicians, query);

        Assert.Null(handler.Last.Query("loggedInUserId"));
    }

    [Fact]
    public async Task Token_Steht_Im_Kopf_ApiToken_Und_Nicht_In_Authorization()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        FakeTokenStore tokens = new("Bearer eyJhbGciOiJIUzI1NiJ9.e30.abc");
        using TanssClient client = TestEnvironment.Client(handler, tokens);

        await client.GetAsync<List<Technician>>(TanssRoutes.Technicians);

        Assert.Equal("Bearer eyJhbGciOiJIUzI1NiJ9.e30.abc", handler.Last.ApiToken);
        Assert.False(handler.Last.HasAuthorizationHeader);
    }

    [Fact]
    public async Task Token_Wird_Bei_Jedem_Aufruf_Frisch_Gelesen()
    {
        // Rotation kann zwischen zwei Aufrufen zuschlagen; ein gemerktes Token erzeugt 403.
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        FakeTokenStore tokens = new(read => $"Bearer token-{read}");
        using TanssClient client = TestEnvironment.Client(handler, tokens);

        await client.GetAsync<List<Technician>>(TanssRoutes.Technicians);
        await client.GetAsync<List<Technician>>(TanssRoutes.Technicians);

        Assert.Equal(2, tokens.Reads);
        Assert.Collection(handler.Captured,
            first => Assert.Equal("Bearer token-1", first.ApiToken),
            second => Assert.Equal("Bearer token-2", second.ApiToken));
    }

    [Fact]
    public async Task Nebenlaeufige_Aufrufe_Lesen_Jeder_Sein_Eigenes_Token()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        FakeTokenStore tokens = new(read => $"Bearer token-{read}");
        using TanssClient client = TestEnvironment.Client(handler, tokens);

        Task[] calls = [.. Enumerable.Range(0, 16)
            .Select(_ => client.GetAsync<List<Technician>>(TanssRoutes.Technicians))];
        await Task.WhenAll(calls);

        Assert.Equal(16, handler.Calls);
        Assert.Equal(16, tokens.Reads);

        // Kein Token darf doppelt verwendet worden sein: sonst waere es zwischengespeichert.
        IEnumerable<string?> used = handler.Captured.Select(request => request.ApiToken);
        Assert.Equal(16, used.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Leere_403_Ist_Kein_Leerer_Erfolg()
    {
        // Die wichtigste Pruefung des Moduls: Status vor Rumpf. Andernfalls gaelte die
        // haeufigste Fehlerantwort von TANSS als geglueckter, leerer Abruf.
        RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.Forbidden));
        using TanssClient client = TestEnvironment.Client(handler);

        TanssAuthException error = await Assert.ThrowsAsync<TanssAuthException>(
            () => client.GetAsync<List<Ticket>>(TanssRoutes.OwnTickets));

        Assert.Equal(403, error.Status);
        Assert.Contains("loggedInUserId", error.Message, StringComparison.Ordinal);
        Assert.Contains("abgelaufen", error.Message, StringComparison.Ordinal);
        Assert.Contains("Recht", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leere_401_Ist_Ebenfalls_Ein_Fehler()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.Unauthorized));
        using TanssClient client = TestEnvironment.Client(handler);

        TanssAuthException error = await Assert.ThrowsAsync<TanssAuthException>(
            () => client.PostAsync<RemoteSupportRead>(TanssRoutes.RemoteSupportsCreate, new { a = 1 }));

        Assert.Equal(401, error.Status);
    }

    [Fact]
    public async Task Leere_200_Ist_Ein_Leerer_Erfolg()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.OK));
        using TanssClient client = TestEnvironment.Client(handler);

        List<Ticket>? result = await client.GetAsync<List<Ticket>>(TanssRoutes.OwnTickets);

        Assert.Null(result);
    }

    [Fact]
    public async Task Leere_204_Beim_Loeschen_Wirft_Nicht()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.NoContent));
        using TanssClient client = TestEnvironment.Client(handler);

        await client.DeleteAsync(TanssRoutes.Timers);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Umschlag_Wird_Ausgepackt()
    {
        const string content = """{"id":38584,"remoteMaintenanceId":"abc","typeId":1003}""";
        RecordingHandler handler = new(HttpStatusCode.Created, TestEnvironment.Envelope(content));
        using TanssClient client = TestEnvironment.Client(handler);

        RemoteSupportRead? read = await client.PostAsync<RemoteSupportRead>(
            TanssRoutes.RemoteSupportsCreate, new { typeId = 1003 });

        Assert.NotNull(read);
        Assert.Equal(38584, read.Id);
        Assert.Equal("abc", read.RemoteMaintenanceId);
    }

    [Fact]
    public async Task PostWithMeta_Liefert_Den_Meta_Block()
    {
        const string meta = """{"linkedEntities":{"employees":{"42":{"id":42}}}}""";
        RecordingHandler handler = new(HttpStatusCode.Created,
                                       TestEnvironment.Envelope("""{"id":1}""", meta));
        using TanssClient client = TestEnvironment.Client(handler);

        (RemoteSupportRead? content, IReadOnlyDictionary<string, object?> block) =
            await client.PostWithMetaAsync<RemoteSupportRead>(TanssRoutes.RemoteSupportsCreate,
                                                              new { typeId = 1003 });

        Assert.NotNull(content);
        Assert.True(block.ContainsKey("linkedEntities"));
        Assert.IsType<JsonElement>(block["linkedEntities"]);
    }

    [Fact]
    public async Task Typfehler_1000_Wird_Zur_Typausnahme()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.Forbidden, TestEnvironment.Error("TYPE_GREATER_1000")));
        using TanssClient client = TestEnvironment.Client(handler);

        TanssRemoteSupportTypeException error =
            await Assert.ThrowsAsync<TanssRemoteSupportTypeException>(
                () => client.PostAsync<RemoteSupportRead>(TanssRoutes.RemoteSupportsCreate,
                                                          new { typeId = 3 }));

        Assert.Equal("TYPE_GREATER_1000", error.Detail);
        Assert.Equal(403, error.Status);
    }

    [Fact]
    public async Task Unbekannter_Typ_Wird_Zur_Typausnahme()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.Forbidden, TestEnvironment.Error("TYPE_DOESNT_EXIST")));
        using TanssClient client = TestEnvironment.Client(handler);

        await Assert.ThrowsAsync<TanssRemoteSupportTypeException>(
            () => client.PostAsync<RemoteSupportRead>(TanssRoutes.RemoteSupportsCreate,
                                                      new { typeId = 4711 }));
    }

    [Fact]
    public async Task Fehlende_Lizenz_Wird_Erkannt()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.Forbidden,
            TestEnvironment.Error("Modul fehlt", type: "TnsModuleNotLicensedException")));
        using TanssClient client = TestEnvironment.Client(handler);

        TanssModuleNotLicensedException error =
            await Assert.ThrowsAsync<TanssModuleNotLicensedException>(
                () => client.PostAsync<RemoteSupportRead>(TanssRoutes.RemoteSupportsCreate,
                                                          new { typeId = 1003 }));

        Assert.Contains("nicht lizenziert", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vierhundertvier_Wird_Zur_Nichtgefunden_Ausnahme()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.NotFound));
        using TanssClient client = TestEnvironment.Client(handler);

        TanssNotFoundException error = await Assert.ThrowsAsync<TanssNotFoundException>(
            () => client.GetAsync<TanssTimer>(TanssRoutes.Timers + "/7"));

        Assert.Equal(404, error.Status);
    }

    [Fact]
    public async Task Unbekannter_Fehler_Bleibt_Basisausnahme_Mit_Status()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.BadRequest, TestEnvironment.Error("ANYTHING_ELSE")));
        using TanssClient client = TestEnvironment.Client(handler);

        TanssException error = await Assert.ThrowsAsync<TanssException>(
            () => client.PostAsync<RemoteSupportRead>(TanssRoutes.RemoteSupportsCreate, new { a = 1 }));

        Assert.Equal(400, error.Status);
        Assert.Equal("ANYTHING_ELSE", error.Detail);
    }

    [Fact]
    public async Task Netzfehler_Wird_Zur_Unerreichbar_Ausnahme()
    {
        FailingHandler handler = new(() => new HttpRequestException("Der Rechner ist offline."));
        using TanssClient client = TestEnvironment.Client(handler);

        TanssUnreachableException error = await Assert.ThrowsAsync<TanssUnreachableException>(
            () => client.PostAsync<RemoteSupportRead>(TanssRoutes.RemoteSupportsCreate, new { a = 1 }));

        Assert.Contains("/backend", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_Antwort_Nennt_Die_Falsche_Basisadresse()
    {
        // Zeigt BaseUrl auf die Weboberflaeche statt auf /backend, kommt HTML mit 200 zurueck.
        RecordingHandler handler = new(HttpStatusCode.OK, "<html><body>TANSS</body></html>");
        using TanssClient client = TestEnvironment.Client(handler);

        TanssException error = await Assert.ThrowsAsync<TanssException>(
            () => client.GetAsync<List<Ticket>>(TanssRoutes.OwnTickets));

        Assert.Contains("kein JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUri_Haengt_Den_Pfad_Woertlich_An()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        using TanssClient client = TestEnvironment.Client(handler);

        Uri uri = client.BuildUri(TanssRoutes.RemoteSupportSystems);

        Assert.Equal(TestEnvironment.BaseUrl + "/api/tanss.x/v1/remoteSupports/systems",
                     uri.ToString());
    }

    [Fact]
    public void BuildUri_Kodiert_Werte()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        using TanssClient client = TestEnvironment.Client(handler);
        Dictionary<string, string?> query = new(StringComparer.Ordinal) { ["info"] = "Log Watcher & Co" };

        Uri uri = client.BuildUri(TanssRoutes.MintToken, query);

        // AbsoluteUri zeigt die kodierte Form - ToString() entschluesselt sie wieder.
        Assert.Contains("info=Log%20Watcher%20%26%20Co", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("loggedInUserId=42", uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUri_Laesst_Null_Werte_Weg()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        using TanssClient client = TestEnvironment.Client(handler);
        Dictionary<string, string?> query = new(StringComparer.Ordinal) { ["text"] = null };

        Uri uri = client.BuildUri(TanssRoutes.RemoteSupportsList, query);

        Assert.DoesNotContain("text=", uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoggedInUserId_Wird_Auf_Tanss_X_In_Jeder_Schreibweise_Entfernt()
    {
        // Die Abbildung vergleicht ordinal; die gemessene Regel lautet aber "niemals
        // mitschicken". TANSS wertet den Parameter aus, sobald er ankommt.
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        using TanssClient client = TestEnvironment.Client(handler);
        Dictionary<string, string?> query = new(StringComparer.Ordinal)
        {
            ["LoggedInUserId"] = "9999",
            ["LOGGEDINUSERID"] = "8888",
        };

        await client.GetAsync<List<Technician>>(TanssRoutes.Technicians, query);

        Assert.DoesNotContain("oggedInUserId", handler.Last.Uri.Query,
                              StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("9999", handler.Last.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("8888", handler.Last.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void LoggedInUserId_Wird_Auf_Api_V1_Nicht_Verdoppelt()
    {
        // Eine abweichende Schreibweise des Aufrufers darf nicht neben dem selbst gesetzten
        // Parameter stehen bleiben - TANSS bekaeme sonst zwei Werte fuer dieselbe Frage.
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        using TanssClient client = TestEnvironment.Client(handler);
        Dictionary<string, string?> query = new(StringComparer.Ordinal)
        {
            ["loggedinuserid"] = "9999",
        };

        string address = client.BuildUri(TanssRoutes.OwnTickets, query).Query;

        Assert.DoesNotContain("9999", address, StringComparison.Ordinal);
        Assert.Contains("loggedInUserId=42", address, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(address, "loggedinuserid"));
    }

    [Fact]
    public void Eine_Basisadresse_Ohne_Schema_Nennt_Die_Abhilfe()
    {
        // Der erwartete Bedienfehler. Eine rohe UriFormatException saehe wie ein
        // Programmfehler aus und sagte dem Aufrufer nicht, was zu tun ist.
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        TanssOptions options = new() { BaseUrl = "tanss.example.invalid/backend", EmployeeId = 42 };
        using TanssClient client = new(options, new FakeTokenStore("Bearer a.b.c"),
                                       TestEnvironment.FastRetry, handler);

        TanssConfigurationException error = Assert.Throws<TanssConfigurationException>(
            () => client.BuildUri(TanssRoutes.OwnTickets));

        Assert.Contains("/backend", error.Message, StringComparison.Ordinal);
        Assert.Contains("https://", error.Message, StringComparison.Ordinal);
        Assert.Contains("400", error.Message, StringComparison.Ordinal);
        Assert.False(RetryPolicy.IsTransient(error));
    }

    [Fact]
    public async Task Eine_Falsche_Basisadresse_Wird_Nicht_Wiederholt()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("[]"));
        TanssOptions options = new() { BaseUrl = "was ist das denn", EmployeeId = 42 };
        using TanssClient client = new(options, new FakeTokenStore("Bearer a.b.c"),
                                       TestEnvironment.FastRetry, handler);

        await Assert.ThrowsAsync<TanssConfigurationException>(
            () => client.GetAsync<List<Ticket>>(TanssRoutes.OwnTickets));

        Assert.Equal(0, handler.Calls);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int found = 0;
        for (int at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.OrdinalIgnoreCase))
        {
            found++;
        }

        return found;
    }
}
