using System.Text;
using System.Text.Json;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Http;

namespace TanssLogWatcher.Api.Tests.Fakes;

/// <summary>Ein Tokenspeicher für Tests: zählt Lesevorgänge und merkt sich Schreibvorgänge.</summary>
internal sealed class FakeTokenStore : ITokenStore
{
    private readonly Func<int, string> _tokenFor;
    private readonly List<string> _written = [];
    private readonly Lock _gate = new();
    private int _reads;

    public FakeTokenStore(string token) => _tokenFor = _ => token;

    /// <summary>Liefert bei jedem Lesen ein anderes Token — für die Rotationsprobe.</summary>
    public FakeTokenStore(Func<int, string> tokenFor) => _tokenFor = tokenFor;

    public int Reads => Volatile.Read(ref _reads);

    public IReadOnlyList<string> Written
    {
        get
        {
            lock (_gate)
            {
                return [.. _written];
            }
        }
    }

    public string Read() => _tokenFor(Interlocked.Increment(ref _reads));

    public void Write(string token)
    {
        lock (_gate)
        {
            _written.Add(token);
        }
    }
}

/// <summary>Kleine Helfer, damit die Tests von der Einrichtung nicht erdrückt werden.</summary>
internal static class TestEnvironment
{
    public const string BaseUrl = "https://tanss.example.invalid/backend";
    public const int EmployeeId = 42;

    public static TanssOptions Options(bool verifyTls = true) => new()
    {
        BaseUrl = BaseUrl,
        EmployeeId = EmployeeId,
        Timeout = TimeSpan.FromSeconds(5),
        VerifyTls = verifyTls,
    };

    /// <summary>Wiederholung ohne Warten und ohne Würfel — sonst dauert ein Test eine Minute.</summary>
    public static RetryPolicy FastRetry => new()
    {
        Sleep = static (_, _) => Task.CompletedTask,
        Jitter = static () => 0d,
    };

    public static TanssClient Client(HttpMessageHandler handler, ITokenStore? tokens = null,
                                     RetryPolicy? retry = null) =>
        new(Options(), tokens ?? new FakeTokenStore("Bearer test.token.value"), retry ?? FastRetry,
            handler);

    /// <summary>Baut den Erfolgsumschlag, so wie TANSS ihn schickt.</summary>
    public static string Envelope(string content, string? meta = null) =>
        $$"""{"meta":{{meta ?? "{}"}},"content":{{content}}}""";

    /// <summary>Baut den Fehlerumschlag.</summary>
    public static string Error(string text, string? type = null, string? traceId = null) =>
        JsonSerializer.Serialize(new
        {
            error = new
            {
                text,
                localizedText = "Nicht erlaubt",
                type = type ?? "TnsException",
                traceId = traceId ?? "t-1",
            },
        });

    /// <summary>
    /// Baut ein JWT mit gültigem Aufbau und <b>ungültiger</b> Signatur.
    /// </summary>
    /// <remarks>
    /// Genau das ist der Punkt: dieses Werkzeug prüft keine Signatur, sondern liest nur den
    /// Nutzteil. Ein Test mit echter Signatur würde etwas prüfen, das gar nicht stattfindet.
    /// </remarks>
    public static string Jwt(DateTimeOffset? expires = null, string? subject = null,
                             string signature = "keine-echte-signatur")
    {
        Dictionary<string, object> claims = new(StringComparer.Ordinal)
        {
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        if (expires is { } moment)
        {
            claims["exp"] = moment.ToUnixTimeSeconds();
        }

        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        string head = Base64Url("""{"alg":"HS256","typ":"JWT"}""");
        string body = Base64Url(JsonSerializer.Serialize(claims));
        return $"Bearer {head}.{body}.{Base64Url(signature)}";
    }

    public static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
