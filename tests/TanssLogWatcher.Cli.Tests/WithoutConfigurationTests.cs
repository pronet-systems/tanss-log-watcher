using System.Text;
using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.Cli.Tests;

/// <summary>
/// Was geschieht, wenn noch gar nichts eingerichtet ist.
/// </summary>
/// <remarks>
/// <para>Das ist der erste Aufruf auf einem frischen Rechner und damit der Normalfall, nicht
/// die Störung. Ein Befehl, der hier mit einer Ausnahme abbricht, sieht aus wie ein kaputtes
/// Werkzeug — und der Benutzer sucht den Fehler an der falschen Stelle.</para>
/// <para>Geprüft wird gegen ein leeres Verzeichnis und nicht gegen das echte Benutzerprofil:
/// Ein Test, der dafür das Profil des Ausführenden leerräumte, wäre schlimmer als kein
/// Test.</para>
/// </remarks>
public sealed class WithoutConfigurationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "tanss-cli-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("doctor")]
    [InlineData("watch")]
    [InlineData("watch", "--dry-run")]
    [InlineData("queue")]
    [InlineData("queue", "--flush")]
    [InlineData("token", "status")]
    [InlineData("token", "rotate")]
    public async Task Jeder_Befehl_endet_freundlich_statt_mit_einer_Ausnahme(params string[] args)
    {
        (int code, _, string error) = await RunAsync(args);

        Assert.Equal(ExitCode.Broken, code);
        Assert.Contains("Einrichtung fehlt", error, StringComparison.Ordinal);
        Assert.Contains("tanss-logwatch setup", error, StringComparison.Ordinal);
        Assert.Contains("config.example.json", error, StringComparison.Ordinal);
        Assert.Contains(Store().Path, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Eine_fehlerhafte_Konfiguration_nennt_den_Grund_und_stuerzt_nicht_ab()
    {
        Directory.CreateDirectory(_directory);

        // employee_id 0 ist genau der Fall, den der Pruefer abfaengt: Die Attribution laege
        // bei niemandem.
        File.WriteAllText(Store().Path,
            """
            {
              "version": 1,
              "tanss": { "base_url": "https://x.de/backend", "employee_id": 0 },
              "monitoring": [{ "key": "mstsc", "remote_support_type_id": 1002 }]
            }
            """,
            Encoding.UTF8);

        (int code, _, string error) = await RunAsync(["doctor"]);

        Assert.Equal(ExitCode.Broken, code);
        Assert.Contains("employee_id", error, StringComparison.Ordinal);
        Assert.Contains("tanss-logwatch setup", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hilfe_braucht_keine_Konfiguration()
    {
        (int code, string output, _) = await RunAsync(["--help"]);

        Assert.Equal(ExitCode.Healthy, code);
        Assert.Contains("tanss-logwatch <Befehl>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ein_unbekannter_Befehl_endet_mit_dem_Aufruffehler()
    {
        (int code, _, string error) = await RunAsync(["gibtsnicht"]);

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains("gibtsnicht", error, StringComparison.Ordinal);
        Assert.Contains("Rückgabewerte", error, StringComparison.Ordinal);
    }

    /// <summary>Räumt das Prüfverzeichnis wieder ab.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Ein liegengebliebenes Testverzeichnis ist kein Grund, einen Testlauf rot zu
            // faerben.
        }
    }

    private ConfigStore Store() => new(Path.Combine(_directory, "config.json"));

    private async Task<(int Code, string Output, string Error)> RunAsync(string[] args)
    {
        await using StringWriter output = new();
        await using StringWriter error = new();

        int code = await Program.RunAsync(args, Store(), output, error);

        return (code, output.ToString(), error.ToString());
    }
}
