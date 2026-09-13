using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TanssLogWatcher.Live.Tests;

/// <summary>
/// Ein Test, der eine echte TANSS-Instanz braucht.
/// </summary>
/// <remarks>
/// <para><b>Wozu es diese Testart überhaupt gibt.</b> Die übrigen Testprojekte arbeiten mit
/// Attrappen, und das ist richtig — sie prüfen Logik, nicht Leitungen. Genau dort liegt aber
/// eine Lücke, die dieses Werkzeug zweimal teuer bezahlt hat: Ein Tokenspeicher gab das
/// Präfix <c>Bearer </c> nicht mit, und weil jeder Test einen gefälschten Zugang benutzt, wurde
/// nie eine echte Kopfzeile gebaut — der Fehler war für keine Attrappe sichtbar. Und der
/// Einrichtungsassistent legte das kurzlebige Sitzungstoken als Arbeitstoken ab, weil nichts je
/// gegen eine Instanz lief.</para>
///
/// <para><b>Ohne Zugangsdaten wird übersprungen, nicht rot.</b> Die Werkstrecke bei GitHub hat
/// keine TANSS-Instanz und soll deswegen nicht scheitern. Gesetzt sein müssen
/// <c>TANSS_BASE_URL</c>, <c>TANSS_USER</c> und <c>TANSS_PASSWORD</c>.</para>
///
/// <para><b>Kein Geheimnis steht im Quelltext.</b> Die Zugangsdaten kommen ausschliesslich aus
/// der Umgebung — ein Testprojekt, das sie einkompiliert, wäre der nächste Fehler dieser Art.</para>
/// </remarks>
public sealed class LiveFactAttribute : FactAttribute
{
    /// <summary>Setzt den Übersprunggrund, wenn die Umgebung nicht vollständig ist.</summary>
    public LiveFactAttribute()
    {
        if (!LiveInstance.IsConfigured)
        {
            Skip = "Keine echte TANSS-Instanz eingerichtet. Zu setzen sind TANSS_BASE_URL, "
                + "TANSS_USER und TANSS_PASSWORD. Ohne sie prüft dieser Test nichts und wird "
                + "übersprungen — er gilt nicht als bestanden.";
        }
    }
}

/// <summary>
/// Ein Test, der in der echten Instanz <b>schreibt</b>.
/// </summary>
/// <remarks>
/// Zusätzlich zu den Zugangsdaten verlangt er <c>TANSS_LIVE_WRITES=1</c>. Schreibende Tests
/// hinterlassen Spuren im Änderungsprotokoll der Instanz, auch wenn sie hinter sich aufräumen;
/// wer nur die Erreichbarkeit prüfen will, soll das tun können, ohne etwas anzulegen.
/// </remarks>
public sealed class LiveWriteFactAttribute : FactAttribute
{
    /// <summary>Setzt den Übersprunggrund, wenn Zugangsdaten oder Freigabe fehlen.</summary>
    public LiveWriteFactAttribute()
    {
        if (!LiveInstance.IsConfigured)
        {
            Skip = "Keine echte TANSS-Instanz eingerichtet.";
        }
        else if (!LiveInstance.WritesAllowed)
        {
            Skip = "Schreibende Tests sind nicht freigegeben. Zu setzen ist TANSS_LIVE_WRITES=1. "
                + "Sie legen in der Instanz etwas an und räumen es wieder weg.";
        }
    }
}

/// <summary>
/// Der Zugang zur echten Instanz für die Tests.
/// </summary>
/// <remarks>
/// <para><b>Geprägt wird ausschliesslich mit <c>isForTesting=true</c>.</b> Ein regulär geprägtes
/// Token läuft ein Jahr, wird von TANSS protokolliert und lässt sich in Version 10.10.0
/// <b>nicht widerrufen</b>. Eine Testreihe, die bei jedem Lauf eines ausstellt, hinterliesse
/// nach einem Monat dreissig gültige Jahrestoken, von denen niemand mehr weiss.</para>
///
/// <para><b>Es wird nie eine Fernwartung angelegt.</b> Das wäre gebuchte Arbeitszeit beim
/// Kunden. Geprüft wird das Anlegen deshalb nur über die Routen, die folgenlos sind, und über
/// Timer, die sich restlos wieder löschen lassen.</para>
/// </remarks>
public static class LiveInstance
{
    private static readonly Lazy<Task<LiveSession>> Shared = new(CreateAsync);

    /// <summary>Die Basisadresse aus der Umgebung.</summary>
    public static string? BaseUrl => Trimmed("TANSS_BASE_URL")?.TrimEnd('/');

    /// <summary>Sind Adresse, Benutzer und Kennwort gesetzt?</summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(Trimmed("TANSS_USER"))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TANSS_PASSWORD"));

    /// <summary>Sind schreibende Tests freigegeben?</summary>
    public static bool WritesAllowed =>
        string.Equals(Trimmed("TANSS_LIVE_WRITES"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Die gemeinsame Sitzung: einmal anmelden, einmal prägen, für alle Tests.
    /// </summary>
    /// <remarks>
    /// Einmal und nicht je Test: Jede Anmeldung ist ein Vorgang in der Instanz, und eine
    /// Testreihe soll sie nicht mit Anmeldungen fluten.
    /// </remarks>
    public static Task<LiveSession> GetAsync() => Shared.Value;

    private static string? Trimmed(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim();

    private static async Task<LiveSession> CreateAsync()
    {
        string baseUrl = BaseUrl
            ?? throw new InvalidOperationException("TANSS_BASE_URL fehlt.");
        string user = Trimmed("TANSS_USER")!;
        string password = Environment.GetEnvironmentVariable("TANSS_PASSWORD")!;

        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // 1. Anmelden. Die Antwort traegt employeeId und apiKey - das Sitzungstoken.
        using StringContent body = new(
            JsonSerializer.Serialize(new { username = user, password }),
            Encoding.UTF8, "application/json");

        using HttpResponseMessage login =
            await http.PostAsync(new Uri(baseUrl + "/api/v1/login"), body).ConfigureAwait(false);

        string loginPayload = await login.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!login.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Die Anmeldung an {baseUrl} scheiterte mit {(int)login.StatusCode}.");
        }

        using JsonDocument document = JsonDocument.Parse(loginPayload);
        JsonElement content = document.RootElement.GetProperty("content");

        string sessionToken = content.GetProperty("apiKey").GetString()
            ?? throw new InvalidOperationException("Die Anmeldung lieferte kein apiKey.");
        int employeeId = content.GetProperty("employeeId").GetInt32();

        // 2. Praegen - ausdruecklich als Trockentest, siehe Klassenkommentar.
        string minted = await MintAsync(http, baseUrl, sessionToken, employeeId).ConfigureAwait(false);

        return new LiveSession(baseUrl, employeeId, sessionToken, minted);
    }

    private static async Task<string> MintAsync(HttpClient http, string baseUrl,
                                                string sessionToken, int employeeId)
    {
        Uri address = new(baseUrl
            + "/api/v1/jwts/tanss_app?duration=60000&info=TANSS%20Log-Watcher%20Test"
            + $"&isForTesting=true&loggedInUserId={employeeId}");

        using HttpRequestMessage request = new(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("apiToken", sessionToken);

        using HttpResponseMessage response = await http.SendAsync(request).ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Das Prägen scheiterte mit {(int)response.StatusCode}. Fehlt dem Mitarbeiter "
                + "das Recht, Token zu prägen?");
        }

        using JsonDocument document = JsonDocument.Parse(payload);

        return document.RootElement.GetProperty("content").GetProperty("apiToken").GetString()
            ?? throw new InvalidOperationException("Das Prägen lieferte kein apiToken.");
    }
}

/// <summary>Eine angemeldete Sitzung an der echten Instanz.</summary>
/// <param name="BaseUrl">Die Basisadresse ohne Schrägstrich am Ende.</param>
/// <param name="EmployeeId">Die Mitarbeiterkennung aus der Anmeldung.</param>
/// <param name="SessionToken">Das kurzlebige Token der Anmeldung, mit <c>Bearer </c>-Präfix.</param>
/// <param name="MintedToken">Das geprägte Token, mit <c>Bearer </c>-Präfix.</param>
public sealed record LiveSession(string BaseUrl, int EmployeeId, string SessionToken,
                                 string MintedToken)
{
    /// <summary>Das geprägte Token ohne Präfix — für den Nachweis, dass es so nicht geht.</summary>
    public string MintedWithoutPrefix =>
        MintedToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? MintedToken["Bearer ".Length..]
            : MintedToken;

    /// <summary>Setzt eine Anfrage mit einem beliebigen Kopfzeilenwert ab.</summary>
    /// <param name="method">Das Verb.</param>
    /// <param name="path">Der Pfad ab <c>/api</c>.</param>
    /// <param name="token">Der Wert der Kopfzeile <c>apiToken</c>, wörtlich.</param>
    /// <param name="json">Ein Rumpf, oder <c>null</c>.</param>
    public async Task<(int Status, string Body)> SendAsync(HttpMethod method, string path,
                                                           string token, string? json = null)
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
        using HttpRequestMessage request = new(method, new Uri(BaseUrl + path));

        // TryAddWithoutValidation und nicht Authorization: TANSS erwartet den Wert in einer
        // eigenen Kopfzeile namens apiToken, und genau dort setzt ihn auch TanssClient hin.
        request.Headers.TryAddWithoutValidation("apiToken", token);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await http.SendAsync(request).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        request.Content?.Dispose();

        return ((int)response.StatusCode, body);
    }
}
