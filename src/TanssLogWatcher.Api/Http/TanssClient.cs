using System.Globalization;
using System.Net;
using System.Text;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Diagnostics;

namespace TanssLogWatcher.Api.Http;

/// <summary>
/// Der HTTP-Zugang zu TANSS. Kennt Umschlag, Token und die Präfixregel — sonst nichts.
/// </summary>
/// <remarks>
/// <para><b>Das Token wird bei jedem Aufruf frisch gelesen.</b> Es kann zwischen zwei Anfragen
/// erneuert worden sein (Rotation läuft im Hintergrund), und ein einmal gemerktes Token würde
/// nach der Erneuerung reihenweise 403 erzeugen. Das Lesen aus dem <see cref="ITokenStore"/> ist
/// billig genug, um es nicht zu optimieren.</para>
/// <para><b>Die Präfixregel nimmt der Aufrufer nicht in die Hand.</b> Auf <c>/api/v1</c> hängt
/// dieser Client <c>loggedInUserId</c> selbst an, auf <c>/api/tanss.x/v1</c> niemals. Ein von
/// Hand gesetzter Parameter gleichen Namens wird überschrieben beziehungsweise entfernt.</para>
/// <para><b>Wiederholt wird nur Lesendes.</b> <see cref="GetAsync{T}"/> läuft durch die
/// <see cref="RetryPolicy"/>; PUT, POST und DELETE laufen einmal und nur einmal, weil TANSS
/// nicht dedupliziert. Lesende Abfragen, die TANSS als PUT verlangt, wiederholt die
/// aufrufende Schicht bewusst selbst.</para>
/// <para><b>Das Verb allein entscheidet das nicht.</b> Ein <c>GET</c> auf einen Pfad, den
/// <see cref="TanssRoutes.HasSideEffectOnGet"/> kennt, ändert serverseitig etwas und läuft
/// deshalb ebenfalls mit genau einem Versuch — andernfalls prägte eine einzige
/// Zeitüberschreitung beim Token-Prägen gleich mehrere unwiderrufliche Token.</para>
/// </remarks>
public sealed class TanssClient : ITanssClient, ITanssBodyDelete, ITanssMetaRead
{
    private const string TokenHeader = "apiToken";
    private const string LoggedInUserId = "loggedInUserId";

    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _ownedHandler;
    private readonly ITokenStore _tokens;
    private readonly TanssOptions _options;
    private readonly RetryPolicy _retry;

    /// <summary>Baut den Zugang samt eigener Verbindungsschicht.</summary>
    /// <param name="options">Adresse, Mitarbeiter-ID, Zeitgrenze, Proxy, TLS-Prüfung.</param>
    /// <param name="tokens">Woher das Token kommt. Wird bei jedem Aufruf befragt.</param>
    /// <param name="retry">Wiederholung für lesende Aufrufe; ohne Angabe die Hausvorgabe.</param>
    public TanssClient(TanssOptions options, ITokenStore tokens, RetryPolicy? retry = null)
        : this(options, tokens, retry, CreateHandler(options), ownsHandler: true)
    {
    }

    /// <summary>
    /// Baut den Zugang auf einer vorgegebenen Verbindungsschicht auf.
    /// </summary>
    /// <remarks>
    /// Gedacht für Tests mit einer Attrappe und für Wirte, die ihre Verbindungen selbst
    /// verwalten. Der übergebene <paramref name="handler"/> wird nicht freigegeben — wer ihn
    /// erzeugt, gibt ihn auch frei.
    /// </remarks>
    public TanssClient(TanssOptions options, ITokenStore tokens, RetryPolicy? retry,
                       HttpMessageHandler handler)
        : this(options, tokens, retry, handler, ownsHandler: false)
    {
    }

    private TanssClient(TanssOptions options, ITokenStore tokens, RetryPolicy? retry,
                        HttpMessageHandler handler, bool ownsHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(handler);

        _options = options;
        _tokens = tokens;
        _retry = retry ?? RetryPolicy.Default;
        _ownedHandler = ownsHandler ? handler : null;
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = options.Timeout };
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string path, IDictionary<string, string?>? query = null,
                                CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return ReadPolicy(path).ExecuteReadAsync(
            async (_, token) => (await SendAsync<T>(HttpMethod.Get, path, null, query, token)
                .ConfigureAwait(false)).Content,
            ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Derselbe Aufruf wie <see cref="GetAsync{T}"/>, nur ohne den Umschlag wegzuwerfen. Beide
    /// Wege gehen durch dieselbe <see cref="ReadPolicy"/> — die Wiederholungsregel darf nicht
    /// davon abhängen, ob der Aufrufer <c>meta</c> haben will.
    /// </remarks>
    public Task<(T? Content, IReadOnlyDictionary<string, object?> Meta)> GetWithMetaAsync<T>(
        string path, IDictionary<string, string?>? query = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return ReadPolicy(path).ExecuteReadAsync(
            (_, token) => SendAsync<T>(HttpMethod.Get, path, null, query, token), ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Genau ein Versuch, wie <see cref="PutAsync{T}"/>. Dass die Route liest, ändert daran
    /// nichts: Das Verb entscheidet, und TANSS dedupliziert nicht.
    /// </remarks>
    public Task<(T? Content, IReadOnlyDictionary<string, object?> Meta)> PutWithMetaAsync<T>(
        string path, object? body = null, IDictionary<string, string?>? query = null,
        CancellationToken ct = default) =>
        SendAsync<T>(HttpMethod.Put, path, body, query, ct);

    /// <summary>
    /// Die Wiederholungsregel für lesende Aufrufe.
    /// </summary>
    /// <remarks>
    /// Lesend, also wiederholbar: eine 500 aus einem TANSS-Neustart darf die Oberfläche nicht
    /// in einen Fehlerzustand zwingen. Ausgenommen sind die GET-Routen, die serverseitig etwas
    /// anlegen — dort ist jeder Versuch ein eigener, nicht rücknehmbarer Vorgang, und eine
    /// Wiederholung erzeugt Müll statt Erfolg.
    /// </remarks>
    private RetryPolicy ReadPolicy(string path) =>
        TanssRoutes.HasSideEffectOnGet(path) ? RetryPolicy.None : _retry;

    /// <inheritdoc />
    public async Task<T?> PutAsync<T>(string path, object? body = null,
                                      IDictionary<string, string?>? query = null,
                                      CancellationToken ct = default) =>
        (await SendAsync<T>(HttpMethod.Put, path, body, query, ct).ConfigureAwait(false)).Content;

    /// <inheritdoc />
    public async Task<T?> PostAsync<T>(string path, object? body = null,
                                       IDictionary<string, string?>? query = null,
                                       CancellationToken ct = default) =>
        (await SendAsync<T>(HttpMethod.Post, path, body, query, ct).ConfigureAwait(false)).Content;

    /// <inheritdoc />
    public Task<(T? Content, IReadOnlyDictionary<string, object?> Meta)> PostWithMetaAsync<T>(
        string path, object? body = null, IDictionary<string, string?>? query = null,
        CancellationToken ct = default) =>
        SendAsync<T>(HttpMethod.Post, path, body, query, ct);

    /// <inheritdoc />
    public async Task DeleteAsync(string path, IDictionary<string, string?>? query = null,
                                  CancellationToken ct = default) =>
        await SendAsync<object>(HttpMethod.Delete, path, null, query, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task DeleteWithBodyAsync(string path, object? body,
                                          IDictionary<string, string?>? query = null,
                                          CancellationToken ct = default) =>
        await SendAsync<object>(HttpMethod.Delete, path, body, query, ct).ConfigureAwait(false);

    /// <summary>
    /// Setzt Pfad, Abfrageparameter und die Präfixregel zu einer Adresse zusammen.
    /// </summary>
    /// <remarks>
    /// Öffentlich, weil genau diese Regel die häufigste Fehlerquelle gegenüber TANSS ist und
    /// in den Tests unmittelbar nachprüfbar sein soll.
    /// </remarks>
    public Uri BuildUri(string path, IDictionary<string, string?>? query = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Dictionary<string, string?> parameters = new(StringComparer.Ordinal);
        if (query is not null)
        {
            foreach (KeyValuePair<string, string?> pair in query)
            {
                parameters[pair.Key] = pair.Value;
            }
        }

        // Die Regel gilt in beide Richtungen: anhaengen, wo er zwingend ist, und entfernen,
        // wo er verboten ist - auch wenn der Aufrufer ihn selbst gesetzt hat. Erst raeumen,
        // dann setzen: sonst stuende eine abweichend geschriebene Variante des Aufrufers auf
        // /api/v1 neben dem selbst gesetzten Parameter und auf /api/tanss.x/v1 sogar allein.
        RemoveEverySpelling(parameters, LoggedInUserId);

        if (TanssRoutes.NeedsLoggedInUserId(path))
        {
            parameters[LoggedInUserId] = _options.EmployeeId.ToString(CultureInfo.InvariantCulture);
        }

        StringBuilder address = new(_options.BaseUrl.TrimEnd('/'));
        address.Append(path);

        bool first = true;
        foreach (KeyValuePair<string, string?> pair in parameters)
        {
            if (pair.Value is null)
            {
                continue;
            }

            address.Append(first ? '?' : '&');
            address.Append(Uri.EscapeDataString(pair.Key));
            address.Append('=');
            address.Append(Uri.EscapeDataString(pair.Value));
            first = false;
        }

        string absolute = address.ToString();
        if (!Uri.TryCreate(absolute, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            // Der haeufigste Bedienfehler ueberhaupt. Eine rohe UriFormatException saehe hier
            // wie ein Programmfehler aus und traegt keine Handlungsanweisung.
            throw new TanssConfigurationException(
                $"Die Basisadresse \"{_options.BaseUrl}\" ergibt zusammen mit \"{path}\" keine "
                + "gültige Adresse. Sie muss mit http:// oder https:// beginnen und auf "
                + "/backend enden — zeigt sie stattdessen auf die TANSS-Weboberfläche, "
                + "antwortet diese auf jede Anfrage mit HTTP 400. Die Einstellung ist zu "
                + "berichtigen; ein erneuter Versuch ändert nichts.");
        }

        return uri;
    }

    /// <summary>
    /// Entfernt einen Abfrageparameter in jeder Schreibweise.
    /// </summary>
    /// <remarks>
    /// Die Abbildung vergleicht ordinal, TANSS wertet <c>loggedInUserId</c> aber schon dann
    /// aus, wenn er überhaupt ankommt. Ein Aufrufer, der ihn versehentlich
    /// <c>LoggedInUserId</c> schreibt, würde sonst auf <c>/api/tanss.x/v1</c> genau den
    /// serverseitigen Zweig auslösen, den diese Route niemals nehmen darf.
    /// </remarks>
    private static void RemoveEverySpelling(Dictionary<string, string?> parameters, string name)
    {
        List<string>? spellings = null;
        foreach (string key in parameters.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                (spellings ??= []).Add(key);
            }
        }

        if (spellings is null)
        {
            return;
        }

        foreach (string key in spellings)
        {
            parameters.Remove(key);
        }
    }

    private async Task<(T? Content, IReadOnlyDictionary<string, object?> Meta)> SendAsync<T>(
        HttpMethod method, string path, object? body, IDictionary<string, string?>? query,
        CancellationToken ct)
    {
        using HttpRequestMessage request = new(method, BuildUri(path, query));

        // Frisch lesen, nicht merken: das Token kann zwischen zwei Aufrufen erneuert worden sein.
        request.Headers.TryAddWithoutValidation(TokenHeader, _tokens.Read());

        if (body is not null)
        {
            request.Content = new StringContent(TanssJson.Serialize(body), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(method, path, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Kein Abbruch durch den Aufrufer, also die Zeitgrenze des HttpClient.
            throw new TanssUnreachableException(
                string.Create(CultureInfo.InvariantCulture,
                    $"{method} {path}: TANSS hat innerhalb von {_options.Timeout.TotalSeconds:0} Sekunden ")
                + "nicht geantwortet. Ob die Anfrage angekommen ist, lässt sich von hier aus nicht "
                + "sagen — schreibende Aufrufe deshalb nur nach vorheriger Existenzprüfung "
                + "wiederholen.", ex);
        }

        using (response)
        {
            string payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Erst der Status, dann der Rumpf. Umgekehrt waere eine leere 403 ein leerer Erfolg.
            if (!response.IsSuccessStatusCode)
            {
                throw TanssEnvelope.ToException((int)response.StatusCode, payload, method.Method, path);
            }

            return TanssEnvelope.Unpack<T>(payload);
        }
    }

    private static TanssUnreachableException Unreachable(HttpMethod method, string path, Exception ex) =>
        new($"{method} {path}: TANSS ist nicht erreichbar. Zu prüfen sind Basisadresse "
            + "(sie muss auf /backend enden), Netzverbindung, Proxy und Zertifikat. "
            + Redaction.Scrub(ex.Message), ex);

    private static HttpClientHandler CreateHandler(TanssOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        HttpClientHandler handler = new();

        if (options.Proxy is { } proxy)
        {
            WebProxy webProxy = new(proxy.Address, proxy.Port);
            if (!string.IsNullOrEmpty(proxy.User))
            {
                webProxy.Credentials = new NetworkCredential(proxy.User, proxy.Password ?? string.Empty);
            }

            handler.Proxy = webProxy;
            handler.UseProxy = true;
        }

        if (!options.VerifyTls)
        {
            // NOTBEHELF. Schaltet die Zertifikatspruefung vollstaendig ab und macht die
            // Verbindung gegen einen Angreifer in der Mitte wertlos. Vertretbar ausschliesslich
            // fuer eine TANSS-Instanz im eigenen Netz mit selbstsigniertem Zertifikat, und auch
            // dort nur so lange, bis das Zertifikat in den Rechnerspeicher aufgenommen ist.
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _ownedHandler?.Dispose();
    }
}
