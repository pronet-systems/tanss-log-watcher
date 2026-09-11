using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Auth;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Cli.Commands;
using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.Cli.Tests;

/// <summary>
/// Tokenstand und Tokenwechsel.
/// </summary>
/// <remarks>
/// Der Wechsel ist der Vorgang mit der geringsten Fehlertoleranz des ganzen Werkzeugs: TANSS
/// 10.10.0 kann ein ausgestelltes Token nicht widerrufen, und ein fehlerhaft übernommenes
/// nähme dem Werkzeug jeden Zugang. Geprüft wird deshalb vor allem, dass <b>nicht</b>
/// übernommen wird, solange die Probe nicht bestanden ist.
/// </remarks>
public sealed class TokenCommandTests
{
    [Fact]
    public async Task Rotate_praegt_prueft_und_uebernimmt_erst_dann()
    {
        FakeTokenStore store = new(FakeTokenStore.JwtExpiring(DateTimeOffset.UtcNow.AddDays(10)));
        FakeTanssClient client = Minting(DateTimeOffset.UtcNow.AddDays(365));

        (int code, string output) = await RunAsync("rotate", store, client,
            _ => new FakeTanssClient { Systems = [new RemoteSupportSystem { Id = 1005 }] });

        Assert.Equal(ExitCode.Healthy, code);
        Assert.NotNull(store.Written);
        Assert.Equal(1, client.MintCalls);
        Assert.Contains("Gewechselt", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotate_uebernimmt_nichts_wenn_die_Probe_scheitert()
    {
        // Der schlechteste Ausgang eines misslungenen Wechsels muss sein, dass alles bleibt,
        // wie es war. Umgekehrt stuende das Werkzeug ohne jeden Zugang da.
        FakeTokenStore store = new(FakeTokenStore.JwtExpiring(DateTimeOffset.UtcNow.AddDays(10)));
        FakeTanssClient client = Minting(DateTimeOffset.UtcNow.AddDays(365));

        (int code, string output) = await RunAsync("rotate", store, client,
            _ => new FakeTanssClient
            {
                GetFails = new TanssAuthException("Das neue Token trägt nicht.", 403),
            });

        Assert.Equal(ExitCode.Broken, code);
        Assert.Null(store.Written);
        Assert.Contains("bisherige", output, StringComparison.Ordinal);
        Assert.Contains("Widerruf", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotate_praegt_genau_einmal()
    {
        // Das Verb ist GET, der Vorgang ist es nicht: Jeder Versuch stellt ein weiteres,
        // nicht widerrufbares Token aus.
        FakeTokenStore store = new(FakeTokenStore.JwtExpiring(DateTimeOffset.UtcNow.AddDays(10)));
        FakeTanssClient client = Minting(DateTimeOffset.UtcNow.AddDays(365));

        _ = await RunAsync("rotate", store, client,
            _ => new FakeTanssClient
            {
                GetFails = new TanssUnreachableException("Zeitüberschreitung."),
            });

        Assert.Equal(1, client.MintCalls);
    }

    [Fact]
    public async Task Status_meldet_eine_lange_Restlaufzeit_als_in_Ordnung()
    {
        FakeTokenStore store = new(FakeTokenStore.JwtExpiring(DateTimeOffset.UtcNow.AddDays(300)));

        (int code, string output) = await RunAsync("status", store,
            Minting(DateTimeOffset.UtcNow.AddMinutes(1)), _ => new FakeTanssClient());

        Assert.Equal(ExitCode.Healthy, code);
        Assert.Contains("Restlaufzeit", output, StringComparison.Ordinal);
        Assert.Contains("Nächster Erneuerungstermin", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_meldet_eine_faellige_Erneuerung_als_Warnung()
    {
        FakeTokenStore store = new(FakeTokenStore.JwtExpiring(DateTimeOffset.UtcNow.AddDays(5)));

        (int code, string output) = await RunAsync("status", store,
            Minting(DateTimeOffset.UtcNow.AddMinutes(1)), _ => new FakeTanssClient());

        Assert.Equal(ExitCode.Warning, code);
        Assert.Contains("token rotate", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_meldet_ein_unlesbares_Token_als_gestoert()
    {
        FakeTokenStore store = new("kein-jwt");

        (int code, string output) = await RunAsync("status", store, new FakeTanssClient(),
                                                   _ => new FakeTanssClient());

        Assert.Equal(ExitCode.Broken, code);
        Assert.Contains("setup", output, StringComparison.Ordinal);
    }

    /// <summary>Ein Zugang, der ein gültiges Token prägt.</summary>
    private static FakeTanssClient Minting(DateTimeOffset expiry) => new()
    {
        Minted = new MintedToken { ApiToken = FakeTokenStore.JwtExpiring(expiry) },
    };

    private static async Task<(int Code, string Output)> RunAsync(
        string subCommand, FakeTokenStore store, ITanssClient client,
        Func<string, ITanssClient> factory)
    {
        await using StringWriter writer = new();

        int code = await TokenCommand.RunAsync(new TokenInputs
        {
            Config = new AppConfig
            {
                Tanss = new TanssSection
                {
                    BaseUrl = "https://tanss.example.de/backend",
                    EmployeeId = 42,
                    RotateBeforeDays = 60,
                },
            },
            Client = client,
            Tokens = store,
            ClientFactory = factory,
        }, subCommand, writer);

        return (code, writer.ToString());
    }
}
