using Xunit;

namespace TanssLogWatcher.Live.Tests;

/// <summary>
/// Die Zusagen, die nur eine echte Instanz bestätigen kann.
/// </summary>
/// <remarks>
/// <para>Jeder Test hier prüft eine Annahme, auf der das Werkzeug aufbaut und die sich mit
/// einer Attrappe nicht prüfen lässt — weil die Attrappe die Annahme selbst verkörpert. Wer
/// eine Annahme gegen seine eigene Nachbildung testet, bekommt immer recht.</para>
///
/// <para><b>Zwei dieser Tests sind Nachzügler zu echten Fehlern</b>
/// (<see cref="Ohne_Bearer_Praefix_weist_TANSS_ab"/> und
/// <see cref="Das_Sitzungstoken_taugt_nicht_fuer_tanss_x"/>). Beide Fehler lagen wochenlang im
/// Quelltext, beide waren mit Attrappen unsichtbar, beide kosteten einen Nachmittag. Sie
/// stehen hier, damit das kein zweites Mal passiert.</para>
/// </remarks>
public sealed class ContractTests
{
    private const string TechniciansRoute = "/api/tanss.x/v1/technicians";

    /// <summary>
    /// Ohne das Präfix <c>Bearer </c> weist TANSS jede Anfrage ab.
    /// </summary>
    /// <remarks>
    /// Der Nachzügler zum ersten Fehler: <c>FixedTokenStore.Read()</c> gab das Token wörtlich
    /// zurück, während <c>ITokenStore.Read()</c> das Präfix zusagt und <c>TanssClient</c> den
    /// Wert unverändert in die Kopfzeile setzt. Jeder Aufruf des Einrichtungsassistenten lief
    /// damit unangemeldet — und die Antwort, 403, sieht aus wie ein abgelaufenes Token.
    /// </remarks>
    [LiveFact]
    public async Task Ohne_Bearer_Praefix_weist_TANSS_ab()
    {
        LiveSession session = await LiveInstance.GetAsync();

        (int status, _) = await session.SendAsync(HttpMethod.Get, TechniciansRoute,
                                                  session.MintedWithoutPrefix);

        Assert.Equal(403, status);
    }

    /// <summary>Mit dem Präfix nimmt dieselbe Anfrage denselben Weg — und wird angenommen.</summary>
    [LiveFact]
    public async Task Mit_Bearer_Praefix_nimmt_TANSS_an()
    {
        LiveSession session = await LiveInstance.GetAsync();

        (int status, _) = await session.SendAsync(HttpMethod.Get, TechniciansRoute,
                                                  session.MintedToken);

        Assert.Equal(200, status);
    }

    /// <summary>
    /// Das Token aus der Anmeldung taugt für <c>/api/tanss.x/v1</c> nicht.
    /// </summary>
    /// <remarks>
    /// Der Nachzügler zum zweiten Fehler: Der Einrichtungsassistent legte den <c>apiKey</c> aus
    /// <c>/api/v1/login</c> als Arbeitstoken ab. TANSS sagt bei der Anmeldung selbst, dass es
    /// vier Stunden gilt — und auf den Routen unter <c>tanss.x</c> gilt es überhaupt nicht.
    /// Gebraucht wird ein geprägtes Token.
    /// </remarks>
    [LiveFact]
    public async Task Das_Sitzungstoken_taugt_nicht_fuer_tanss_x()
    {
        LiveSession session = await LiveInstance.GetAsync();

        (int status, _) = await session.SendAsync(HttpMethod.Get, TechniciansRoute,
                                                  session.SessionToken);

        Assert.Equal(403, status);
    }

    /// <summary>Das geprägte Token dagegen schon — das ist der Unterschied, auf den es ankommt.</summary>
    [LiveFact]
    public async Task Das_gepraegte_Token_traegt_auf_tanss_x()
    {
        LiveSession session = await LiveInstance.GetAsync();

        (int status, string body) = await session.SendAsync(HttpMethod.Get, TechniciansRoute,
                                                            session.MintedToken);

        Assert.Equal(200, status);
        Assert.Contains("\"content\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die undokumentierten Routen, auf denen das Werkzeug steht, antworten noch.
    /// </summary>
    /// <remarks>
    /// <para>Keine dieser Routen steht in der OpenAPI-Beschreibung von TANSS 10.10.0. Bricht
    /// eine nach einem Update weg, soll das hier auffallen und nicht beim Kunden — dort sähe es
    /// aus wie ein Fehler dieses Werkzeugs.</para>
    /// <para>Ausschliesslich lesende Routen. Das Anlegen einer Fernwartung wäre gebuchte
    /// Arbeitszeit und gehört in keinen Test.</para>
    /// </remarks>
    [LiveFact]
    public async Task Die_undokumentierten_Routen_antworten_noch()
    {
        LiveSession session = await LiveInstance.GetAsync();

        string[] routes =
        [
            "/api/tanss.x/v1/technicians",
            "/api/tanss.x/v1/remoteSupports/systems",
            $"/api/v1/timers?loggedInUserId={session.EmployeeId}",
        ];

        foreach (string route in routes)
        {
            (int status, _) = await session.SendAsync(HttpMethod.Get, route, session.MintedToken);

            Assert.True(status == 200,
                $"{route} antwortete mit {status} statt 200. Ist die Route nach einem "
                + "TANSS-Update weggefallen oder umbenannt worden?");
        }
    }

    /// <summary>
    /// Auf <c>/api/v1</c> ist <c>loggedInUserId</c> Pflicht — ohne ihn kommt 403.
    /// </summary>
    /// <remarks>
    /// <para>Dies ist die einzige Richtung, in der die Regel wirklich beisst, und sie wird hier
    /// durch den <b>Fehlschlag</b> nachgewiesen: Ein Test, der nur den erlaubten Aufruf macht,
    /// kann die Regel gar nicht verletzt sehen.</para>
    /// <para>Genau dieser Fehler steckte in der ersten Fassung dieses Tests: Er prüfte zweimal
    /// einen erlaubten Aufruf und hiess trotzdem nach der Regel. Er wäre grün geblieben, auch
    /// wenn der Zugang den Parameter nie gesetzt hätte.</para>
    /// </remarks>
    [LiveFact]
    public async Task Auf_api_v1_ist_loggedInUserId_Pflicht()
    {
        LiveSession session = await LiveInstance.GetAsync();

        (int without, _) = await session.SendAsync(HttpMethod.Get,
            "/api/v1/timers", session.MintedToken);

        Assert.Equal(403, without);

        (int with, _) = await session.SendAsync(HttpMethod.Get,
            $"/api/v1/timers?loggedInUserId={session.EmployeeId}", session.MintedToken);

        Assert.Equal(200, with);
    }

    /// <summary>
    /// Auf <c>/api/tanss.x/v1</c> stört <c>loggedInUserId</c> nicht — er wird ignoriert.
    /// </summary>
    /// <remarks>
    /// <para><b>Dieser Test hält eine Korrektur fest.</b> Der Quelltext behauptete lange, der
    /// Parameter dürfe auf <c>tanss.x</c> „niemals“ mitgeschickt werden, und diese Behauptung
    /// stand als Ursache Nummer eins in der Meldung zu jeder 403 — sie schickte den Techniker
    /// auf eine Fährte, die es nicht gibt. Nachgemessen antwortet dieselbe Route mit und ohne
    /// den Parameter mit 200.</para>
    /// <para>Der Zugang setzt ihn dort trotzdem nicht: Er ist überflüssig, und Überflüssiges
    /// gehört nicht auf die Leitung. Aber er ist nicht verboten, und das soll hier stehen
    /// bleiben, falls jemand die alte Behauptung wieder einführt.</para>
    /// </remarks>
    [LiveFact]
    public async Task Auf_tanss_x_wird_loggedInUserId_ignoriert()
    {
        LiveSession session = await LiveInstance.GetAsync();

        (int without, _) = await session.SendAsync(HttpMethod.Get,
            TechniciansRoute, session.MintedToken);

        (int with, _) = await session.SendAsync(HttpMethod.Get,
            $"{TechniciansRoute}?loggedInUserId={session.EmployeeId}", session.MintedToken);

        Assert.Equal(200, without);
        Assert.Equal(200, with);
    }

    /// <summary>
    /// Der Trockentest beim Prägen liefert ein <b>brauchbares</b> Token, kein unbrauchbares.
    /// </summary>
    /// <remarks>
    /// <para>Hält eine zweite Korrektur fest: Der Quelltext behauptete, <c>isForTesting=true</c>
    /// ergebe „ein unbrauchbares Token von 60 Sekunden Laufzeit“. Nachgemessen hält sich TANSS
    /// bei beiden Werten an die angefragte <c>duration</c> und liefert ein voll gültiges Token.
    /// Kurzlebig wird der Trockentest allein dadurch, dass <c>MintAsync</c> in diesem Fall
    /// selbst 60 Sekunden anfragt.</para>
    /// <para>Der Test fragt bewusst eine kurze Laufzeit an und prüft, dass TANSS sie
    /// übernimmt — schlüge er fehl, hiesse das, die Harmlosigkeit des Trockentests hängt an
    /// etwas anderem als angenommen.</para>
    /// </remarks>
    [LiveFact]
    public async Task Der_Trockentest_beim_Praegen_folgt_der_angefragten_Laufzeit()
    {
        LiveSession session = await LiveInstance.GetAsync();

        (int status, string body) = await session.SendAsync(HttpMethod.Get,
            "/api/v1/jwts/tanss_app?duration=60000&info=Laufzeitprobe&isForTesting=true"
            + $"&loggedInUserId={session.EmployeeId}", session.MintedToken);

        Assert.Equal(200, status);

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(body);
        string token = document.RootElement.GetProperty("content").GetProperty("apiToken")
            .GetString()!;

        long expiresAt = ReadExpiry(token);
        long lifetime = expiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.True(lifetime is > 0 and < 600,
            $"Das Probetoken läuft {lifetime} Sekunden statt der angefragten 60. Die "
            + "Harmlosigkeit des Trockentests hängt genau an dieser Laufzeit.");
    }

    /// <summary>Liest den <c>exp</c>-Anspruch eines JWT, ohne die Signatur zu prüfen.</summary>
    /// <remarks>
    /// Ungeprüft, und das ist hier richtig: Der Test misst eine Laufzeit, er trifft keine
    /// Zugriffsentscheidung. Den Schlüssel zur Prüfung hat ohnehin nur TANSS.
    /// </remarks>
    private static long ReadExpiry(string token)
    {
        string value = token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? token["Bearer ".Length..]
            : token;

        string payload = value.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));

        return document.RootElement.GetProperty("exp").GetInt64();
    }
}
