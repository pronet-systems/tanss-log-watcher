using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

public sealed class ConfigValidatorTests
{
    [Fact]
    public void Ein_Fernwartungstyp_unter_1000_wird_erkannt_bevor_Arbeit_verlorengeht()
    {
        AppConfig config = Sample.Config() with
        {
            Monitoring = [new MonitoringEntry { Key = "rdp", RemoteSupportTypeId = 53 }],
        };

        string problem = Assert.Single(ConfigValidator.Collect(config));

        Assert.Contains("monitoring[0].remote_support_type_id", problem, StringComparison.Ordinal);
        Assert.Contains("TYPE_GREATER_1000", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Doppelte_Schluessel_sind_keine_Frage_der_Reihenfolge()
    {
        AppConfig config = Sample.Config() with
        {
            Monitoring =
            [
                new MonitoringEntry { Key = "rdp", RemoteSupportTypeId = 1001 },
                new MonitoringEntry { Key = "RDP", RemoteSupportTypeId = 1002 },
            ],
        };

        string problem = Assert.Single(ConfigValidator.Collect(config));

        Assert.Contains("monitoring[1].key", problem, StringComparison.Ordinal);
        Assert.Contains("mehrfach", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_Schluessel_mit_Leerzeichen_am_Rand_wird_nicht_durchgelassen()
    {
        // Gesucht wird der Schluessel woertlich (AppConfig.MonitorFor). Ginge " rdp" durch,
        // bestuende die Regel jede Pruefung und griffe trotzdem nie - genau die stille
        // Wirkungslosigkeit, gegen die diese Konfiguration sonst konsequent vorgeht.
        AppConfig config = Sample.Config() with
        {
            Monitoring = [new MonitoringEntry { Key = " rdp", RemoteSupportTypeId = 1001 }],
        };

        string problem = Assert.Single(ConfigValidator.Collect(config));

        Assert.Contains("monitoring[0].key", problem, StringComparison.Ordinal);
        Assert.Contains("Leerzeichen", problem, StringComparison.Ordinal);
        Assert.Null(config.MonitorFor("rdp"));
    }

    [Fact]
    public void Eine_vertippte_Ausschlussadresse_wird_benannt()
    {
        AppConfig config = Sample.Config() with
        {
            Monitoring =
            [
                new MonitoringEntry
                {
                    Key = "rdp",
                    RemoteSupportTypeId = 1001,
                    ExcludeIpAddresses = ["10.0.0.0/8", "192.168.1.300", "kunde-srv01"],
                },
            ],
        };

        IReadOnlyList<string> problems = ConfigValidator.Collect(config);

        Assert.Equal(2, problems.Count);
        Assert.Contains("monitoring[0].exclude_ip_addresses[1]", problems[0], StringComparison.Ordinal);
        Assert.Contains("monitoring[0].exclude_ip_addresses[2]", problems[1], StringComparison.Ordinal);
        Assert.Contains("unbemerkt", problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_fehlerhafter_Ausdruck_faellt_beim_Laden_auf_und_nicht_erst_bei_der_Sitzung()
    {
        AppConfig config = Sample.Config() with
        {
            Monitoring =
            [
                new MonitoringEntry
                {
                    Key = "putty",
                    RemoteSupportTypeId = 1003,
                    RegexPatternOverride = "(unvollstaendig",
                },
            ],
        };

        string problem = Assert.Single(ConfigValidator.Collect(config));
        Assert.Contains("regex_pattern_override", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_gueltiger_Ausdruck_stoert_nicht()
    {
        AppConfig config = Sample.Config() with
        {
            Monitoring =
            [
                new MonitoringEntry
                {
                    Key = "putty",
                    RemoteSupportTypeId = 1003,
                    RegexPatternOverride = @"^(?<host>[^\s@]+)\s+-\s+PuTTY$",
                },
            ],
        };

        Assert.Empty(ConfigValidator.Collect(config));
    }

    [Fact]
    public void Eine_Schonfrist_kuerzer_als_der_Takt_waere_wirkungslos()
    {
        AppConfig config = Sample.Config() with
        {
            Watcher = new WatcherSection { PollIntervalSeconds = 60, IpSessionGraceSeconds = 30 },
        };

        string problem = Assert.Single(ConfigValidator.Collect(config));

        Assert.Contains("ip_session_grace_seconds", problem, StringComparison.Ordinal);
        Assert.Contains("wirkungslos", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Abgeschaltete_Schonfrist_ist_erlaubt()
    {
        AppConfig config = Sample.Config() with
        {
            Watcher = new WatcherSection { PollIntervalSeconds = 60, IpSessionGraceSeconds = 0 },
        };

        Assert.Empty(ConfigValidator.Collect(config));
    }

    [Fact]
    public void Ein_Proxy_ohne_Adresse_ist_kein_Proxy()
    {
        AppConfig config = Sample.Config() with
        {
            Proxy = new ProxySection { Enabled = true, Address = "", Port = 0, User = "svc" },
        };

        IReadOnlyList<string> problems = ConfigValidator.Collect(config);

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("proxy.address", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("proxy.port", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("proxy.password_ref", StringComparison.Ordinal));
    }

    [Fact]
    public void Ein_abgeschalteter_Proxy_wird_nicht_geprueft()
    {
        AppConfig config = Sample.Config() with
        {
            Proxy = new ProxySection { Enabled = false, Address = "", Port = 0 },
        };

        Assert.Empty(ConfigValidator.Collect(config));
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("info")]
    [InlineData("warning")]
    [InlineData("error")]
    public void Bekannte_Protokollstufen(string level)
    {
        AppConfig config = Sample.Config() with { Logging = new LoggingSection { Level = level } };

        Assert.Empty(ConfigValidator.Collect(config));
    }

    [Theory]
    [InlineData("INFO")]
    [InlineData("verbose")]
    [InlineData("")]
    public void Unbekannte_Protokollstufen(string level)
    {
        AppConfig config = Sample.Config() with { Logging = new LoggingSection { Level = level } };

        string problem = Assert.Single(ConfigValidator.Collect(config));
        Assert.StartsWith("logging.level", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Eine_Adresse_mit_Schraegstrich_am_Ende_erzeugte_doppelte_Pfade()
    {
        AppConfig config = Sample.Config() with
        {
            Tanss = new TanssSection
            {
                BaseUrl = "https://tanss.kunde.de/backend/",
                EmployeeId = 1,
            },
        };

        string problem = Assert.Single(ConfigValidator.Collect(config));

        Assert.Contains("Schrägstrich", problem, StringComparison.Ordinal);
        Assert.Contains("https://tanss.kunde.de/backend“", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Eine_vollstaendige_Konfiguration_beanstandet_nichts()
    {
        Assert.Empty(ConfigValidator.Collect(Sample.Config()));
    }
}
