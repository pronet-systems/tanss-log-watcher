using System.Net;
using System.Text;

namespace TanssLogWatcher.Api.Tests.Fakes;

/// <summary>
/// Eine Attrappe der Verbindungsschicht: hält jede Anfrage fest und antwortet nach Vorgabe.
/// </summary>
/// <remarks>
/// Der Rumpf wird sofort ausgelesen und mitgeschrieben — nach dem Senden gibt der Client die
/// Anfrage frei, und ein später gelesener Rumpf wäre leer.
/// </remarks>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<CapturedRequest, HttpResponseMessage> _respond;
    private readonly List<CapturedRequest> _captured = [];
    private readonly Lock _gate = new();
    private int _calls;

    public RecordingHandler(Func<CapturedRequest, HttpResponseMessage> respond) => _respond = respond;

    /// <summary>Antwortet immer gleich.</summary>
    public RecordingHandler(HttpStatusCode status, string body)
        : this(_ => Respond(status, body))
    {
    }

    /// <summary>Wie oft wurde gesendet? Zählt auch Versuche, die in einer Ausnahme endeten.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public IReadOnlyList<CapturedRequest> Captured
    {
        get
        {
            lock (_gate)
            {
                return [.. _captured];
            }
        }
    }

    public CapturedRequest Last
    {
        get
        {
            lock (_gate)
            {
                return _captured[^1];
            }
        }
    }

    public static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    /// <summary>Eine Antwort ganz ohne Rumpf — der gefährliche Fall aus Hausregel 2.</summary>
    public static HttpResponseMessage Empty(HttpStatusCode status) =>
        new(status) { Content = new StringContent(string.Empty, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                 CancellationToken cancellationToken)
    {
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        int attempt = Interlocked.Increment(ref _calls);

        CapturedRequest captured = new(
            request.Method.Method,
            request.RequestUri ?? new Uri("about:blank"),
            request.Headers.TryGetValues("apiToken", out IEnumerable<string>? token)
                ? string.Join(",", token)
                : null,
            request.Headers.Contains("Authorization"),
            body,
            attempt);

        lock (_gate)
        {
            _captured.Add(captured);
        }

        return _respond(captured);
    }
}

/// <summary>Eine festgehaltene Anfrage.</summary>
/// <param name="Method">HTTP-Verb.</param>
/// <param name="Uri">Vollständige Adresse einschließlich Abfragezeichenkette.</param>
/// <param name="ApiToken">Wert der Kopfzeile <c>apiToken</c>, falls gesetzt.</param>
/// <param name="HasAuthorizationHeader">
/// War eine Kopfzeile <c>Authorization</c> gesetzt? TANSS erwartet sie nicht, also darf sie
/// niemals auftauchen.
/// </param>
/// <param name="Body">Der gesendete Rumpf als Text.</param>
/// <param name="Attempt">Der wievielte Sendeversuch dies war, beginnend bei 1.</param>
internal sealed record CapturedRequest(string Method, Uri Uri, string? ApiToken,
                                       bool HasAuthorizationHeader, string? Body, int Attempt)
{
    /// <summary>Liest einen Abfrageparameter; <c>null</c>, wenn er fehlt.</summary>
    public string? Query(string name)
    {
        foreach (string pair in Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = pair.IndexOf('=', StringComparison.Ordinal);
            string key = split < 0 ? pair : pair[..split];
            if (string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal))
            {
                return split < 0 ? string.Empty : Uri.UnescapeDataString(pair[(split + 1)..]);
            }
        }

        return null;
    }
}

/// <summary>Eine Verbindungsschicht, die niemals antwortet, sondern wirft.</summary>
internal sealed class FailingHandler : HttpMessageHandler
{
    private readonly Func<Exception> _error;
    private int _calls;

    public FailingHandler(Func<Exception> error) => _error = error;

    public int Calls => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                           CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        throw _error();
    }
}
