using System.Text;
using System.Text.Json.Nodes;
using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public void Rundlauf_erhaelt_alle_Felder()
    {
        using TempDirectory temp = new();
        ConfigStore store = new(temp.File("config.json"));

        AppConfig written = Sample.Config() with
        {
            Watcher = new WatcherSection { PollIntervalSeconds = 7, IpSessionGraceSeconds = 240 },
            Proxy = new ProxySection
            {
                Enabled = true,
                Address = "proxy.pronet.local",
                Port = 3128,
                User = "svc-tanss",
                PasswordRef = "dpapi:proxy",
            },
            Logging = new LoggingSection
            {
                Level = "debug",
                RedactWindowTitles = false,
                RetentionDays = 14,
            },
        };

        store.Save(written);
        AppConfig read = store.Load();

        Assert.Equal(written.Version, read.Version);
        Assert.Equal(written.Tanss, read.Tanss);
        Assert.Equal(written.Watcher, read.Watcher);
        Assert.Equal(written.Proxy, read.Proxy);
        Assert.Equal(written.Logging, read.Logging);

        MonitoringEntry entry = Assert.Single(read.Monitoring);
        Assert.Equal("putty", entry.Key);
        Assert.Equal(1003, entry.RemoteSupportTypeId);
        string[] expectedExcludes = ["10.0.0.0/8", "192.168.1.5"];
        Assert.Equal(expectedExcludes, entry.ExcludeIpAddresses);
    }

    [Fact]
    public void Geschrieben_wird_in_Schlangenschrift()
    {
        using TempDirectory temp = new();
        ConfigStore store = new(temp.File("config.json"));
        store.Save(Sample.Config());

        string raw = File.ReadAllText(store.Path, Encoding.UTF8);

        Assert.Contains("\"base_url\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"employee_id\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"rotate_before_days\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"poll_interval_seconds\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"ip_session_grace_seconds\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"remote_support_type_id\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"exclude_ip_addresses\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"redact_window_titles\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("baseUrl", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("BaseUrl", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Unbekanntes_Feld_wird_abgelehnt_und_erklaert()
    {
        // Der Tippfehler, auf den es ankommt: "adresses" statt "addresses". Wuerde er
        // stillschweigend uebergangen, hielte der Techniker seine Ausschlussliste fuer
        // aktiv, waehrend jede Sitzung beim Kunden gebucht wuerde.
        const string raw = """
            {
              "version": 1,
              "tanss": { "base_url": "https://tanss.kunde.de/backend", "employee_id": 1 },
              "monitoring": [
                { "key": "rdp", "remote_support_type_id": 1001,
                  "exclude_ip_adresses": ["10.0.0.0/8"] }
              ]
            }
            """;

        ConfigException error = Assert.Throws<ConfigException>(() => ConfigStore.Parse(raw, "config.json"));

        Assert.Contains("unbekanntes Feld", error.Message, StringComparison.Ordinal);
        Assert.Contains("exclude_ip_adresses", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_Wert_mit_falschem_Typ_wird_nicht_zum_ungueltigen_JSON_erklaert()
    {
        // Die Einordnung darf nicht am englischen Wortlaut der Framework-Meldung haengen:
        // Auf einem Rechner mit deutschen .NET-Ressourcen traefe kein Vergleich mehr zu.
        // Hier faellt schon auf Englisch keiner der beiden alten Vergleiche - die Datei
        // wurde deshalb als „kein gueltiges JSON“ abgetan, obwohl sie tadelloses JSON ist
        // und nur ein einziges Feld zu berichtigen waere.
        const string raw = """
            {
              "version": 1,
              "tanss": { "base_url": "https://tanss.kunde.de/backend", "employee_id": "sieben" },
              "monitoring": [ { "key": "rdp", "remote_support_type_id": 1001 } ]
            }
            """;

        ConfigException error = Assert.Throws<ConfigException>(() => ConfigStore.Parse(raw, "config.json"));

        Assert.DoesNotContain("ist kein gültiges JSON", error.Message, StringComparison.Ordinal);
        Assert.Contains("employee_id", error.Message, StringComparison.Ordinal);
        Assert.Contains("falschen Typ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_fehlendes_Pflichtfeld_wird_als_solches_benannt()
    {
        const string raw = """
            { "version": 1, "monitoring": [ { "key": "rdp", "remote_support_type_id": 1001 } ] }
            """;

        ConfigException error = Assert.Throws<ConfigException>(() => ConfigStore.Parse(raw, "config.json"));

        Assert.DoesNotContain("ist kein gültiges JSON", error.Message, StringComparison.Ordinal);
        Assert.Contains("Pflichtfeld", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wirklich_kaputtes_JSON_heisst_weiterhin_so()
    {
        ConfigException error =
            Assert.Throws<ConfigException>(() => ConfigStore.Parse("{ das ist kein JSON", "config.json"));

        Assert.Contains("ist kein gültiges JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fehlerhafte_Basisadresse_ergibt_eine_sprechende_Meldung()
    {
        AppConfig config = Sample.Config() with
        {
            Tanss = new TanssSection { BaseUrl = "https://tanss.kunde.de", EmployeeId = 1 },
        };

        ConfigValidationException error =
            Assert.Throws<ConfigValidationException>(() => config.Validate("config.json"));

        string problem = Assert.Single(error.Problems);
        Assert.Contains("tanss.base_url", problem, StringComparison.Ordinal);
        Assert.Contains("/backend", problem, StringComparison.Ordinal);
        Assert.Contains("400", problem, StringComparison.Ordinal);
        Assert.Contains("https://tanss.kunde.de/backend", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Alle_Verstoesse_stehen_in_einer_Meldung()
    {
        AppConfig config = new()
        {
            Tanss = new TanssSection
            {
                BaseUrl = "nicht-mal-eine-adresse",
                EmployeeId = 0,
                TimeoutSeconds = 1,
            },
            Monitoring = [],
        };

        ConfigValidationException error = Assert.Throws<ConfigValidationException>(() => config.Validate());

        Assert.Equal(4, error.Problems.Count);
        Assert.Contains(error.Problems, p => p.StartsWith("tanss.base_url", StringComparison.Ordinal));
        Assert.Contains(error.Problems, p => p.StartsWith("tanss.employee_id", StringComparison.Ordinal));
        Assert.Contains(error.Problems, p => p.StartsWith("tanss.timeout_seconds", StringComparison.Ordinal));
        Assert.Contains(error.Problems, p => p.StartsWith("monitoring:", StringComparison.Ordinal));
        // Die Meldung selbst traegt bereits alles - der Aufrufer soll nichts nachformatieren.
        Assert.Contains("tanss.employee_id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fehlende_Datei_nennt_den_Einrichtungsbefehl()
    {
        using TempDirectory temp = new();
        ConfigStore store = new(temp.File("config.json"));

        Assert.False(store.Exists());
        ConfigException error = Assert.Throws<ConfigException>(() => store.Load());
        Assert.Contains("setup", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Eine_fehlerhafte_Konfiguration_wird_gar_nicht_erst_geschrieben()
    {
        using TempDirectory temp = new();
        ConfigStore store = new(temp.File("config.json"));

        AppConfig broken = Sample.Config() with
        {
            Monitoring = [new MonitoringEntry { Key = "rdp", RemoteSupportTypeId = 42 }],
        };

        _ = Assert.Throws<ConfigValidationException>(() => store.Save(broken));
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void Kommentare_und_nachgestellte_Kommata_stoeren_nicht()
    {
        const string raw = """
            {
              // von Hand stillgelegt, weil der Kunde AnyDesk abgeschafft hat
              "tanss": { "base_url": "https://tanss.kunde.de/backend", "employee_id": 7, },
              "monitoring": [ { "key": "rdp", "remote_support_type_id": 1001 } ]
            }
            """;

        AppConfig config = ConfigStore.Parse(raw);

        Assert.Equal(7, config.Tanss.EmployeeId);
        Assert.Equal(1, config.Version);
    }

    [Fact]
    public void Zwischenstand_ueberlebt_einen_Abbruch_und_laesst_sich_raeumen()
    {
        using TempDirectory temp = new();
        ConfigStore store = new(temp.File("config.json"));

        JsonObject draft = new()
        {
            ["tanss"] = new JsonObject { ["base_url"] = "https://tanss.kunde.de/backend" },
        };

        store.SavePartial(draft);

        Assert.True(File.Exists(store.PartialPath));
        Assert.EndsWith("config.partial.json", store.PartialPath, StringComparison.Ordinal);

        JsonObject? loaded = store.LoadPartial();
        Assert.NotNull(loaded);
        Assert.Equal("https://tanss.kunde.de/backend",
            loaded["tanss"]?["base_url"]?.GetValue<string>());

        store.ClearPartial();
        Assert.Null(store.LoadPartial());
    }

    [Fact]
    public void Ein_unlesbarer_Zwischenstand_verhindert_die_Einrichtung_nicht()
    {
        using TempDirectory temp = new();
        ConfigStore store = new(temp.File("config.json"));
        File.WriteAllText(store.PartialPath, "{ das ist kein JSON");

        Assert.Null(store.LoadPartial());
    }

    [Fact]
    public void Ein_neuerer_Dateistand_wird_nicht_stillschweigend_gelesen()
    {
        AppConfig config = Sample.Config() with { Version = AppConfig.CurrentVersion + 1 };

        ConfigValidationException error = Assert.Throws<ConfigValidationException>(() => config.Validate());
        string problem = Assert.Single(error.Problems);
        Assert.StartsWith("version:", problem, StringComparison.Ordinal);
    }
}
