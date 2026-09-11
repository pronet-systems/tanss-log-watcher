using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Auth;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Cli.Commands;
using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.Cli.Tests;

/// <summary>
/// Der Rückgabewert von <c>doctor</c>.
/// </summary>
/// <remarks>
/// Er ist der Überwachungsvertrag: 0 gesund, 1 Warnung, 2 gestört. Eine Überwachung entscheidet
/// allein anhand dieser Zahl, ob jemand geweckt wird — geprüft wird sie deshalb hier und nicht
/// nur von Hand gegen eine echte Instanz.
/// </remarks>
public sealed class DoctorCommandTests
{
    [Fact]
    public async Task Gesunde_Einrichtung_gibt_null_zurueck()
    {
        (int code, string output) = await RunAsync(Healthy());

        Assert.Equal(ExitCode.Healthy, code);
        Assert.Contains("Befund: gesund", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Abgeschaltete_TLS_Pruefung_ist_eine_Warnung()
    {
        DoctorInputs inputs = Healthy() with
        {
            Config = Config(verifyTls: false),
        };

        (int code, string output) = await RunAsync(inputs);

        Assert.Equal(ExitCode.Warning, code);
        Assert.Contains("verify_tls", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fehlendes_Recht_zum_Praegen_ist_eine_Warnung_und_kein_Fehler()
    {
        // Heute laeuft alles; am Tag des Ablaufs stirbt das Token lautlos. Genau deshalb
        // Warnung und nicht Fehler - und genau deshalb muss es ueberhaupt in der Ausgabe
        // stehen.
        DoctorInputs inputs = Healthy() with
        {
            Client = new FakeTanssClient { Minted = null },
        };

        (int code, string output) = await RunAsync(inputs);

        Assert.Equal(ExitCode.Warning, code);
        Assert.Contains("480", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unerreichbares_TANSS_gilt_als_gestoert()
    {
        DoctorInputs inputs = Healthy() with
        {
            Technicians = new FakeTechnicianRepository
            {
                Fails = new TanssUnreachableException("Kein Netz."),
            },
        };

        (int code, string output) = await RunAsync(inputs);

        Assert.Equal(ExitCode.Broken, code);
        Assert.Contains("Befund: gestört", output, StringComparison.Ordinal);
        Assert.Contains("/backend", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Abgewiesener_Aufruf_trennt_Erreichbarkeit_von_Tokenfrage()
    {
        // Die wichtigste Unterscheidung des ganzen Befehls: nicht erreichbar heisst Netz oder
        // Adresse, abgewiesen heisst Token oder Recht. Wer beides zusammenzieht, schickt den
        // Techniker in die falsche Richtung.
        DoctorInputs inputs = Healthy() with
        {
            Technicians = new FakeTechnicianRepository
            {
                Fails = new TanssAuthException("Abgewiesen.", 403),
            },
        };

        (int code, string output) = await RunAsync(inputs);

        Assert.Equal(ExitCode.Broken, code);
        Assert.Contains("TANSS hat geantwortet", output, StringComparison.Ordinal);
        Assert.Contains("token rotate", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Abgelaufenes_Token_gilt_als_gestoert()
    {
        DoctorInputs inputs = Healthy() with
        {
            Tokens = new FakeTokenStore(
                FakeTokenStore.JwtExpiring(DateTimeOffset.UtcNow.AddDays(-1))),
        };

        (int code, _) = await RunAsync(inputs);

        Assert.Equal(ExitCode.Broken, code);
    }

    [Fact]
    public async Task Nicht_lesbares_Token_gilt_als_gestoert()
    {
        DoctorInputs inputs = Healthy() with
        {
            Tokens = new FakeTokenStore("kein-jwt"),
        };

        (int code, _) = await RunAsync(inputs);

        Assert.Equal(ExitCode.Broken, code);
    }

    [Fact]
    public async Task Nicht_lizenziertes_Modul_gilt_als_gestoert()
    {
        DoctorInputs inputs = Healthy() with
        {
            RemoteSupports = new FakeRemoteSupportRepository
            {
                SystemsFails = new TanssModuleNotLicensedException("Nicht lizenziert.", 403),
            },
        };

        (int code, string output) = await RunAsync(inputs);

        Assert.Equal(ExitCode.Broken, code);
        Assert.Contains("lizenziert", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Eingestellter_Typ_ohne_Anbindung_ist_eine_Warnung()
    {
        // Der Fehler faellt sonst erst beim Upload auf - also nach getaner Arbeit, deren
        // Sitzung dann in der Warteschlange festhinge.
        DoctorInputs inputs = Healthy() with
        {
            RemoteSupports = new FakeRemoteSupportRepository
            {
                Systems = [new RemoteSupportSystem { Id = 1002, Name = "Remote Desktop" }],
            },
        };

        (int code, string output) = await RunAsync(inputs);

        Assert.Equal(ExitCode.Warning, code);
        Assert.Contains("TYPE_DOESNT_EXIST", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unbekannte_Mitarbeiter_ID_ist_eine_Warnung()
    {
        DoctorInputs inputs = Healthy() with
        {
            Technicians = new FakeTechnicianRepository
            {
                Technicians = [new Technician { Id = 7, Name = "Jemand anderes" }],
            },
        };

        (int code, string output) = await RunAsync(inputs);

        Assert.Equal(ExitCode.Warning, code);
        Assert.Contains("Attribution", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nicht_oeffenbare_Warteschlange_gilt_als_gestoert()
    {
        DoctorInputs inputs = Healthy() with
        {
            Queue = () => throw new InvalidOperationException("state.db ist gesperrt."),
        };

        (int code, string output) = await RunAsync(inputs);

        Assert.Equal(ExitCode.Broken, code);
        Assert.Contains("Zustandsdatenbank", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unlesbare_Zeilen_werden_nicht_verschwiegen()
    {
        // Sonst stuende "ausstehend: 4" neben drei Eintraegen, und niemand wuesste, wo der
        // vierte geblieben ist.
        FakeUploadQueue queue = new() { Unreadable = 1 };
        DoctorInputs inputs = Healthy() with { Queue = () => queue };

        (int code, string output) = await RunAsync(inputs);

        Assert.Equal(ExitCode.Warning, code);
        Assert.Contains("unlesbar", output, StringComparison.Ordinal);
    }

    /// <summary>Eine Einrichtung, an der nichts auszusetzen ist.</summary>
    private static DoctorInputs Healthy() => new()
    {
        Config = Config(),
        Client = new FakeTanssClient
        {
            Minted = new MintedToken { ApiToken = "Bearer probe" },
        },
        RemoteSupports = new FakeRemoteSupportRepository
        {
            Systems =
            [
                new RemoteSupportSystem { Id = 1005, Name = "AnyDesk" },
                new RemoteSupportSystem { Id = 1002, Name = "Remote Desktop" },
            ],
        },
        Technicians = new FakeTechnicianRepository
        {
            Technicians = [new Technician { Id = 42, Name = "Musterfrau, Erika" }],
        },
        Tokens = new FakeTokenStore(
            FakeTokenStore.JwtExpiring(DateTimeOffset.UtcNow.AddDays(300))),
        Queue = () => new FakeUploadQueue(),
        ConfigPath = @"C:\Test\config.json",
    };

    private static AppConfig Config(bool verifyTls = true) => new()
    {
        Tanss = new TanssSection
        {
            BaseUrl = "https://tanss.example.de/backend",
            EmployeeId = 42,
            VerifyTls = verifyTls,
        },
        Monitoring = [new MonitoringEntry { Key = "AnyDesk", RemoteSupportTypeId = 1005 }],
    };

    private static async Task<(int Code, string Output)> RunAsync(DoctorInputs inputs)
    {
        await using StringWriter writer = new();
        int code = await DoctorCommand.RunAsync(inputs, writer);
        return (code, writer.ToString());
    }
}
