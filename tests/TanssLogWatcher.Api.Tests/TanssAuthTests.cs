using System.Net;
using System.Text.Json;
using TanssLogWatcher.Api.Auth;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Tests.Fakes;
using Xunit;

namespace TanssLogWatcher.Api.Tests;

/// <summary>Prüft das Lesen, Prägen und Erneuern von Token.</summary>
public sealed class TanssAuthTests
{
    [Fact]
    public void Ein_Selbstgebautes_Jwt_Ohne_Gueltige_Signatur_Wird_Gelesen()
    {
        // Es wird bewusst keine Signatur geprueft: das tut der Server, und nur er kann es.
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddDays(30);
        string token = TestEnvironment.Jwt(expiry, subject: "42");

        TanssTokenClaims claims = TanssAuth.DecodeClaims(token);

        Assert.Equal(expiry.ToUnixTimeSeconds(), claims.ExpiresAt?.ToUnixTimeSeconds());
        Assert.Equal("42", claims.Subject);
        Assert.NotNull(claims.IssuedAt);
    }

    [Fact]
    public void Das_Praefix_Bearer_Stoert_Nicht()
    {
        string token = TestEnvironment.Jwt(DateTimeOffset.UtcNow.AddDays(10));

        Assert.Equal(TanssAuth.DecodeClaims(token).ExpiresAt,
                     TanssAuth.DecodeClaims(TanssAuth.StripBearer(token)).ExpiresAt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Base64url_Wird_Auch_Ohne_Auffuellung_Gelesen(int padding)
    {
        // Je nach Laenge des Nutzteils fehlen ein oder zwei "="; ohne Auffuellung scheitert
        // Convert.FromBase64String ausgerechnet an manchen Token.
        string claims = $$"""{"exp":1800000000,"sub":"{{new string('x', padding)}}"}""";
        string token = $"{TestEnvironment.Base64Url("{}")}.{TestEnvironment.Base64Url(claims)}.sig";

        Assert.Equal(1800000000L, TanssAuth.DecodeClaims(token).TryGetNumber("exp"));
    }

    [Fact]
    public void Kein_Jwt_Wirft_Eine_Authausnahme()
    {
        TanssAuthException error = Assert.Throws<TanssAuthException>(
            () => TanssAuth.DecodeClaims("das ist kein token"));

        Assert.Contains("kein JWT", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Leerer_Tokenspeicher_Wirft_Eine_Authausnahme()
    {
        Assert.Throws<TanssAuthException>(() => TanssAuth.DecodeClaims("   "));
        Assert.Throws<TanssAuthException>(() => TanssAuth.DecodeClaims(null));
    }

    [Fact]
    public void Unlesbarer_Nutzteil_Wirft_Eine_Authausnahme()
    {
        string token = $"{TestEnvironment.Base64Url("{}")}.{TestEnvironment.Base64Url("kein json")}.sig";

        Assert.Throws<TanssAuthException>(() => TanssAuth.DecodeClaims(token));
    }

    [Fact]
    public void Restlaufzeit_Wird_Aus_Exp_Berechnet()
    {
        DateTimeOffset now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        string token = TestEnvironment.Jwt(now.AddDays(90));

        Assert.Equal(90d, TanssAuth.DaysRemaining(token, now), 3);
    }

    [Fact]
    public void Abgelaufene_Token_Haben_Eine_Negative_Restlaufzeit()
    {
        DateTimeOffset now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        string token = TestEnvironment.Jwt(now.AddDays(-2));

        Assert.True(TanssAuth.DaysRemaining(token, now) < 0);
    }

    [Fact]
    public void Ohne_Exp_Gilt_Das_Token_Als_Unbefristet()
    {
        string token = TestEnvironment.Jwt(expires: null);

        Assert.Null(TanssAuth.ExpiresAt(token));
        Assert.Equal(double.PositiveInfinity, TanssAuth.DaysRemaining(token));
    }

    [Fact]
    public async Task Praegen_Rechnet_Tage_In_Millisekunden()
    {
        string minted = TestEnvironment.Jwt(DateTimeOffset.UtcNow.AddDays(365));
        RecordingHandler handler = new(HttpStatusCode.OK,
            TestEnvironment.Envelope($$"""{"apiToken":"{{minted}}"}"""));
        using TanssClient client = TestEnvironment.Client(handler);

        string token = await TanssAuth.MintAsync(client, durationDays: 365, info: "Log-Watcher");

        Assert.Equal(minted, token);
        Assert.Equal("31536000000", handler.Last.Query("duration"));
        Assert.Equal("false", handler.Last.Query("isForTesting"));
        Assert.Equal("Log-Watcher", handler.Last.Query("info"));

        // Die Praegeroute liegt unter /api/v1 und braucht deshalb loggedInUserId.
        Assert.Equal("42", handler.Last.Query("loggedInUserId"));
    }

    [Fact]
    public async Task Der_Trockentest_Laeuft_Sechzig_Sekunden_Und_Ist_Als_Test_Gekennzeichnet()
    {
        string minted = TestEnvironment.Jwt(DateTimeOffset.UtcNow.AddMinutes(1));
        RecordingHandler handler = new(HttpStatusCode.OK,
            TestEnvironment.Envelope($$"""{"apiToken":"{{minted}}"}"""));
        using TanssClient client = TestEnvironment.Client(handler);

        bool? allowed = await TanssAuth.CanRotateAsync(client);

        Assert.True(allowed);
        Assert.Equal("60000", handler.Last.Query("duration"));
        Assert.Equal("true", handler.Last.Query("isForTesting"));
    }

    [Fact]
    public async Task Ohne_Recht_Meldet_Der_Trockentest_Nein_Statt_Zu_Werfen()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.Forbidden, TestEnvironment.Error("NOT_ALLOWED_TO_CREATE_JWTS")));
        using TanssClient client = TestEnvironment.Client(handler);

        Assert.False(await TanssAuth.CanRotateAsync(client));
    }

    [Fact]
    public async Task Ein_Noch_Lange_Gueltiges_Token_Wird_Nicht_Angefasst()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("{}"));
        FakeTokenStore store = new(TestEnvironment.Jwt(DateTimeOffset.UtcNow.AddDays(300)));
        using TanssClient client = TestEnvironment.Client(handler, store);

        TokenRotationResult result = await TanssAuth.RotateIfNeededAsync(
            client, store, verify: (_, _) => Task.FromResult(true));

        Assert.False(result.Rotated);
        Assert.Empty(store.Written);
        Assert.Equal(0, handler.Calls);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Ein_Bald_Ablaufendes_Token_Wird_Nach_Bestandener_Probe_Uebernommen()
    {
        string minted = TestEnvironment.Jwt(DateTimeOffset.UtcNow.AddDays(365));
        RecordingHandler handler = new(HttpStatusCode.OK,
            TestEnvironment.Envelope($$"""{"apiToken":"{{minted}}"}"""));
        FakeTokenStore store = new(TestEnvironment.Jwt(DateTimeOffset.UtcNow.AddDays(10)));
        using TanssClient client = TestEnvironment.Client(handler, store);

        string? probed = null;
        TokenRotationResult result = await TanssAuth.RotateIfNeededAsync(
            client, store,
            verify: (token, _) =>
            {
                probed = token;
                return Task.FromResult(true);
            });

        Assert.True(result.Rotated);
        Assert.Equal(minted, probed);
        Assert.Equal(minted, Assert.Single(store.Written));
        Assert.NotNull(result.NewExpiry);
        Assert.True(result.NewExpiry > result.OldExpiry);
    }

    [Fact]
    public async Task Ein_Neues_Token_Ohne_Bestandene_Probe_Wird_Verworfen()
    {
        // Das alte Token ist ja noch gueltig - lieber nichts tun als den Zugang verlieren.
        string minted = TestEnvironment.Jwt(DateTimeOffset.UtcNow.AddDays(365));
        RecordingHandler handler = new(HttpStatusCode.OK,
            TestEnvironment.Envelope($$"""{"apiToken":"{{minted}}"}"""));
        FakeTokenStore store = new(TestEnvironment.Jwt(DateTimeOffset.UtcNow.AddDays(10)));
        using TanssClient client = TestEnvironment.Client(handler, store);

        TokenRotationResult result = await TanssAuth.RotateIfNeededAsync(
            client, store, verify: (_, _) => Task.FromResult(false));

        Assert.False(result.Rotated);
        Assert.Empty(store.Written);
        Assert.Contains("bisherige bleibt in Kraft", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Eine_Werfende_Probe_Laesst_Das_Alte_Token_Ebenfalls_Stehen()
    {
        string minted = TestEnvironment.Jwt(DateTimeOffset.UtcNow.AddDays(365));
        RecordingHandler handler = new(HttpStatusCode.OK,
            TestEnvironment.Envelope($$"""{"apiToken":"{{minted}}"}"""));
        FakeTokenStore store = new(TestEnvironment.Jwt(DateTimeOffset.UtcNow.AddDays(10)));
        using TanssClient client = TestEnvironment.Client(handler, store);

        TokenRotationResult result = await TanssAuth.RotateIfNeededAsync(
            client, store,
            verify: (_, _) => throw new TanssAuthException("Token abgelehnt", 403));

        Assert.False(result.Rotated);
        Assert.Empty(store.Written);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Misslungenes_Praegen_Laesst_Das_Alte_Token_Stehen()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.Forbidden, TestEnvironment.Error("NOT_ALLOWED_TO_CREATE_JWTS")));
        FakeTokenStore store = new(TestEnvironment.Jwt(DateTimeOffset.UtcNow.AddDays(10)));
        using TanssClient client = TestEnvironment.Client(handler, store);

        TokenRotationResult result = await TanssAuth.RotateIfNeededAsync(
            client, store, verify: (_, _) => Task.FromResult(true));

        Assert.False(result.Rotated);
        Assert.Empty(store.Written);
        Assert.Contains("bleibt in Kraft", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ein_Unlesbares_Token_Wird_Gemeldet_Statt_Ersetzt()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope("{}"));
        FakeTokenStore store = new("kaputt");
        using TanssClient client = TestEnvironment.Client(handler, store);

        TokenRotationResult result = await TanssAuth.RotateIfNeededAsync(
            client, store, verify: (_, _) => Task.FromResult(true));

        Assert.False(result.Rotated);
        Assert.Empty(store.Written);
        Assert.Equal(0, handler.Calls);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Das_Fehlerbild_Der_Probe_Wird_Geschwaerzt()
    {
        string minted = TestEnvironment.Jwt(DateTimeOffset.UtcNow.AddDays(365));
        RecordingHandler handler = new(HttpStatusCode.OK,
            TestEnvironment.Envelope($$"""{"apiToken":"{{minted}}"}"""));
        FakeTokenStore store = new(TestEnvironment.Jwt(DateTimeOffset.UtcNow.AddDays(10)));
        using TanssClient client = TestEnvironment.Client(handler, store);

        TokenRotationResult result = await TanssAuth.RotateIfNeededAsync(
            client, store,
            verify: (token, _) => throw new TanssAuthException("Abgelehnt mit " + token));

        Assert.NotNull(result.Error);
        Assert.DoesNotContain("eyJ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anmeldung_Sendet_Kein_Token_Und_Liest_Die_Sitzungsdaten()
    {
        const string content =
            """{"employeeId":42,"apiKey":"geheim","expire":1800000000,"refresh":"r","employeeType":"TECHNICIAN"}""";
        RecordingHandler handler = new(HttpStatusCode.OK, TestEnvironment.Envelope(content));

        LoginResult result = await TanssAuth.LoginAsync(TestEnvironment.BaseUrl, "sebastian",
                                                        "kennwort", twoFactorToken: "123456",
                                                        handler: handler);

        Assert.Equal(42, result.EmployeeId);
        Assert.Equal("geheim", result.ApiKey);
        Assert.Equal("TECHNICIAN", result.EmployeeType);
        Assert.Equal(1800000000L, result.ExpiresAt?.ToUnixTimeSeconds());

        CapturedRequest sent = handler.Last;
        Assert.Null(sent.ApiToken);
        Assert.False(sent.HasAuthorizationHeader);
        Assert.Equal(TestEnvironment.BaseUrl + "/api/v1/login", sent.Uri.ToString());

        using JsonDocument body = JsonDocument.Parse(sent.Body ?? "{}");
        Assert.Equal("sebastian", body.RootElement.GetProperty("username").GetString());
        Assert.Equal("kennwort", body.RootElement.GetProperty("password").GetString());
        Assert.Equal("123456", body.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public async Task Falsche_Anmeldedaten_Werden_Zur_Authausnahme()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.Unauthorized, TestEnvironment.Error("LOGIN_FAILED")));

        await Assert.ThrowsAsync<TanssAuthException>(
            () => TanssAuth.LoginAsync(TestEnvironment.BaseUrl, "sebastian", "falsch",
                                       handler: handler));
    }

    [Fact]
    public async Task Praegen_Wird_Bei_Einer_Fuenfhundert_Nur_Ein_Einziges_Mal_Versucht()
    {
        // GET /api/v1/jwts/tanss_app ist kein Lesevorgang: jeder Versuch stellt ein Token aus
        // und protokolliert es serverseitig. Fuenf Versuche waeren vier verwaiste, bis zu
        // 365 Tage gueltige Token - TANSS 10.10.0 kennt keinen Widerruf.
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.InternalServerError, TestEnvironment.Error("BOOM")));
        using TanssClient client = TestEnvironment.Client(handler);

        TanssException error = await Assert.ThrowsAsync<TanssException>(
            () => TanssAuth.MintAsync(client, durationDays: 365));

        Assert.Equal(1, handler.Calls);
        Assert.Equal(500, error.Status);
    }

    [Fact]
    public async Task Praegen_Wird_Nach_Einer_Zeitueberschreitung_Nicht_Wiederholt()
    {
        // Der gefaehrlichste Fall: die Anfrage kann angekommen sein, das Token also existieren.
        FailingHandler handler = new(() => new TaskCanceledException("Zeitgrenze."));
        using TanssClient client = TestEnvironment.Client(handler);

        await Assert.ThrowsAsync<TanssUnreachableException>(() => TanssAuth.MintAsync(client));

        Assert.Equal(1, handler.Calls);
    }

    /// <summary>
    /// Ein Verbindungsabbruch heisst „konnte nicht gefragt werden“ — nicht „darf nicht“.
    /// </summary>
    /// <remarks>
    /// <para>Dieser Test prüfte früher <c>Assert.False</c> und hielt damit einen Fehler fest:
    /// Jede <c>TanssException</c> wurde auf <c>false</c> gedrückt, und <c>doctor</c> meldete
    /// daraufhin bei einem Netzausfall, dem Mitarbeiter fehle in TANSS das Recht 480. Das
    /// schickte den Techniker zum TANSS-Administrator, während in Wahrheit das VPN fehlte.</para>
    /// <para>Der zweite Teil bleibt: Auch der Trockentest fragt genau einmal. Jeder Versuch
    /// stellt serverseitig ein Token aus; eine Wiederholung nach Zeitüberschreitung erzeugte
    /// ein zweites, von dem hier niemand mehr erfährt.</para>
    /// </remarks>
    [Fact]
    public async Task Der_Trockentest_Meldet_Bei_Netzausfall_Unbekannt_Und_Fragt_Nur_Einmal()
    {
        FailingHandler handler = new(() => new HttpRequestException("Verbindung abgebrochen."));
        using TanssClient client = TestEnvironment.Client(handler);

        Assert.Null(await TanssAuth.CanRotateAsync(client));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Eine Abweisung durch TANSS heisst dagegen wirklich „darf nicht“.</summary>
    /// <remarks>
    /// Die Gegenprobe zum Test darüber: Nur <see cref="TanssAuthException"/> — also eine
    /// Antwort, in der TANSS den Vorgang abgelehnt hat — wird zu <c>false</c>. Ohne diesen Test
    /// liesse sich die Unterscheidung beseitigen, ohne dass etwas rot würde.
    /// </remarks>
    [Fact]
    public async Task Eine_Abweisung_Durch_Tanss_Meldet_Darf_Nicht()
    {
        RecordingHandler handler = new(HttpStatusCode.Forbidden,
            TestEnvironment.Error("Nicht erlaubt", type: "NOT_ALLOWED_TO_CREATE_JWTS"));
        using TanssClient client = TestEnvironment.Client(handler);

        Assert.False(await TanssAuth.CanRotateAsync(client));
    }

    [Fact]
    public async Task Lesende_Routen_Bleiben_Wiederholbar()
    {
        // Gegenprobe: die Ausnahme gilt der Praegeroute, nicht dem Verb GET.
        FailingHandler handler = new(() => new HttpRequestException("Verbindung abgebrochen."));
        using TanssClient client = TestEnvironment.Client(handler);

        await Assert.ThrowsAsync<TanssUnreachableException>(
            () => client.GetAsync<List<Model.Ticket>>(TanssRoutes.OwnTickets));

        Assert.Equal(5, handler.Calls);
    }
}
