using System.Text;
using TanssLogWatcher.Monitoring;
using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.Cli.Tests;

/// <summary>
/// Die Beispielkonfiguration.
/// </summary>
/// <remarks>
/// <para>Sie ist keine Dokumentation, sondern eine Datei, die jemand kopiert und in Betrieb
/// nimmt. Eine Vorlage, die <see cref="ConfigStore"/> nicht laden kann — ein Feld, das
/// umbenannt wurde, eine Kennung unter 1000 —, schickt genau die Benutzer in den Ladefehler,
/// die sich an die Vorlage gehalten haben.</para>
/// <para>Geprüft wird deshalb mit demselben Weg, den auch der Dienst nimmt: lesen und
/// vollständig prüfen.</para>
/// </remarks>
public sealed class ConfigExampleTests
{
    [Fact]
    public void Beispiel_laesst_sich_lesen_und_pruefen()
    {
        string raw = File.ReadAllText(Locate(), Encoding.UTF8);

        AppConfig config = ConfigStore.Parse(raw, "config.example.json");
        config.Validate("config.example.json");

        Assert.Equal(AppConfig.CurrentVersion, config.Version);
        Assert.EndsWith("/backend", config.Tanss.BaseUrl, StringComparison.Ordinal);
        Assert.NotEmpty(config.Monitoring);
    }

    [Fact]
    public void Beispiel_nennt_nur_Profile_aus_dem_Katalog()
    {
        // Ein erfundener Schluessel wuerde weder abgelehnt noch beachtet: Die Beobachtung
        // liefe still leer, und der Techniker hielte seine Regel fuer aktiv.
        AppConfig config = ConfigStore.Parse(File.ReadAllText(Locate(), Encoding.UTF8));

        foreach (MonitoringEntry entry in config.Monitoring)
        {
            Assert.True(MonitoringProfiles.Find(entry.Key) is not null,
                $"Das Beispiel nennt das Profil „{entry.Key}“, das der Katalog nicht kennt.");
        }
    }

    [Fact]
    public void Beispiel_schwaerzt_Fensterbeschriftungen()
    {
        // Die Vorlage ist der Ausgangszustand jeder Installation. Stuende hier false, liefe
        // jede daraus gebaute Einrichtung mit Klartext in state.db an.
        AppConfig config = ConfigStore.Parse(File.ReadAllText(Locate(), Encoding.UTF8));

        Assert.True(config.Logging.RedactWindowTitles);
    }

    /// <summary>
    /// Findet die Beispieldatei.
    /// </summary>
    /// <remarks>
    /// Erst neben der Testbaugruppe — dorthin legt sie das Bauskript —, sonst aufwärts durch
    /// den Quellbaum. Der zweite Weg ist der, der auch dann trägt, wenn jemand die Datei aus
    /// dem Ausgabeverzeichnis nimmt.
    /// </remarks>
    private static string Locate()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "config.example.json");
        if (File.Exists(beside))
        {
            return beside;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "config.example.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "config.example.json wurde weder neben der Testbaugruppe noch im Quellbaum "
            + "darüber gefunden. Sie gehört in das Wurzelverzeichnis des Repositoriums.");
    }
}
