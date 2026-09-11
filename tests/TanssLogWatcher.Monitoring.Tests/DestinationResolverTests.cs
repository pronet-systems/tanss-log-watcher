using TanssLogWatcher.Monitoring.Model;
using Xunit;

namespace TanssLogWatcher.Monitoring.Tests;

/// <summary>Die drei Wege zum Ziel, jeweils an ihren Grenzen.</summary>
public sealed class DestinationResolverTests
{
    private readonly FakeTcpConnectionSource tcp = new();
    private readonly FakeHostNameResolver dns = new();

    private static readonly ProcessInfo AnyProcess = new(100, 4, "mstsc", null);

    [Fact]
    public void RegexLiefertDenTreffer()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["mstsc"];
        WindowInfo[] windows = [new("SRV-DC01 - Remotedesktopverbindung", 100, 1)];

        IReadOnlyList<ResolvedDestination> result = Resolve(profile, windows, mainWindowHandle: 1);

        ResolvedDestination single = Assert.Single(result);
        Assert.Equal("SRV-DC01", single.Destination);
        Assert.Equal("SRV-DC01", single.IdentityKey);
        Assert.Equal(1, single.WindowHandle);
    }

    [Fact]
    public void OhneTrefferUndOhneUnterdrueckungEntstehtDerPlatzhalter()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["mstsc"];
        WindowInfo[] windows = [new("Remotedesktopverbindung", 100, 7)];

        IReadOnlyList<ResolvedDestination> result = Resolve(profile, windows, mainWindowHandle: 7);

        ResolvedDestination single = Assert.Single(result);
        Assert.Equal(SessionConstants.NoDestinationTitle, single.Destination);
        Assert.Equal(7, single.WindowHandle);
    }

    [Fact]
    public void OhneFensterEntstehtDerPlatzhalter()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["mstsc"];

        IReadOnlyList<ResolvedDestination> result = Resolve(profile, [], mainWindowHandle: 0);

        Assert.Equal(SessionConstants.NoDestinationTitle, Assert.Single(result).Destination);
    }

    /// <summary>
    /// Der entscheidende Unterschied: bei <c>MonitorOnlyRegexMatchedWindow</c> heißt „kein Treffer“
    /// <b>keine Sitzung</b> und nicht „Platzhalter“.
    /// </summary>
    [Fact]
    public void MonitorOnlyRegexMatchedWindowOhneTrefferErzeugtKeineSitzung()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["TeamViewer"];
        WindowInfo[] windows = [new("TeamViewer", 100, 3)];

        Assert.Empty(Resolve(profile, windows, mainWindowHandle: 3));
    }

    [Fact]
    public void MonitorOnlyRegexMatchedWindowMitTrefferLiefertDasZiel()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["TeamViewer"];
        WindowInfo[] windows = [new("Kunde Meier - TeamViewer", 100, 3)];

        Assert.Equal("Kunde Meier", Assert.Single(Resolve(profile, windows, mainWindowHandle: 3)).Destination);
    }

    [Fact]
    public void MonitorOnlyMainWindowHandleUnterdruecktNebenfenster()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["devenv"];
        WindowInfo[] windows =
        [
            new("Nebenfenster - Visual Studio", 100, 2),
            new("Projekt - Microsoft Visual Studio", 100, 1),
        ];

        ResolvedDestination single = Assert.Single(Resolve(profile, windows, mainWindowHandle: 1));
        Assert.Equal("Projekt", single.Destination);
        Assert.Equal(1, single.WindowHandle);
    }

    /// <summary>
    /// Wird das einzige Fenster unterdrückt, bleibt kein Treffer übrig — und der Platzhalter darf
    /// nicht einspringen, sonst liefe eine Sitzung zu einem Fenster, das ausdrücklich ausgeschlossen
    /// ist.
    /// </summary>
    [Fact]
    public void NurUnterdruecktesFensterErgibtKeineSitzung()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["devenv"];
        WindowInfo[] windows = [new("Nebenfenster - Visual Studio", 100, 2)];

        Assert.Empty(Resolve(profile, windows, mainWindowHandle: 1));
    }

    [Fact]
    public void IgnorierterTitelUnterdruecktEbenfalls()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["host"];
        WindowInfo[] windows =
        [
            new("pcvisit Support", 100, 1),
            new("Kunde Schulz", 100, 2),
        ];

        ResolvedDestination single = Assert.Single(Resolve(profile, windows, mainWindowHandle: 1));
        Assert.Equal("Kunde Schulz", single.Destination);
        Assert.Equal(2, single.WindowHandle);
    }

    [Fact]
    public void TitelverfahrenLiefertJedesFensterEinzeln()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["cmd"];
        WindowInfo[] windows = [new("C:\\Windows", 100, 1), new("C:\\Temp", 100, 2)];

        IReadOnlyList<ResolvedDestination> result = Resolve(profile, windows, mainWindowHandle: 1);

        Assert.Equal(2, result.Count);
        Assert.Equal(["C:\\Windows", "C:\\Temp"], result.Select(r => r.Destination));
    }

    [Fact]
    public void TitelverfahrenOhneFensterErgibtKeineSitzung() =>
        Assert.Empty(Resolve(MonitoringProfiles.ByKey["cmd"], [], mainWindowHandle: 0));

    /// <summary>
    /// Ein eigener Ausdruck aus den Einstellungen schlägt den des Profils. Gedacht für den Fall,
    /// dass eine Anwendung ihren Titelaufbau ändert, bevor der Katalog nachzieht.
    /// </summary>
    [Fact]
    public void EigenerAusdruckSchlaegtDenDesProfils()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["mstsc"];
        MonitoringSetting setting = new()
        {
            Key = "mstsc",
            RemoteSupportTypeId = 1001,
            RegexPatternOverride = @"(?<=RDP:\s).+",
        };
        WindowInfo[] windows = [new("RDP: SRV-FILE01", 100, 1)];

        DestinationResolver resolver = new(tcp, dns);
        IReadOnlyList<ResolvedDestination> result =
            resolver.Resolve(profile, setting, AnyProcess, 1, windows, []);

        Assert.Equal("SRV-FILE01", Assert.Single(result).Destination);
    }

    /// <summary>
    /// Ein unübersetzbarer Ausdruck ist ein Konfigurationsfehler und wird gemeldet.
    /// </summary>
    /// <remarks>
    /// Der Rückfall auf den Platzhalter wäre schlimmer als der Fehler: er setzt
    /// <see cref="MonitoringProfile.MonitorOnlyRegexMatchedWindow"/> außer Kraft und legt für
    /// <b>jedes</b> Fenster der Anwendung eine inhaltsleere Fernwartung im TANSS des Kunden an.
    /// </remarks>
    [Fact]
    public void UnuebersetzbarerAusdruckIstEinKonfigurationsfehler()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["mstsc"];
        MonitoringSetting setting = new()
        {
            Key = "mstsc",
            RemoteSupportTypeId = 1001,
            RegexPatternOverride = "(unvollstaendig",
        };
        WindowInfo[] windows = [new("SRV - Remote", 100, 1)];
        DestinationResolver resolver = new(tcp, dns);

        MonitoringConfigurationException error = Assert.Throws<MonitoringConfigurationException>(
            () => resolver.Resolve(profile, setting, AnyProcess, 1, windows, []));

        Assert.Equal("mstsc", error.ProfileKey);
    }

    /// <summary>
    /// Derselbe Fall beim Profil mit <c>MonitorOnlyRegexMatchedWindow</c>: dort entstünde ohne die
    /// Meldung je Fenster ein Platzhalter, auch für das Chatfenster.
    /// </summary>
    [Fact]
    public void UnuebersetzbarerAusdruckErzeugtKeinePlatzhalterJeFenster()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["TeamViewer"];
        MonitoringSetting setting = new()
        {
            Key = "TeamViewer",
            RemoteSupportTypeId = 1001,
            RegexPatternOverride = "(unvollstaendig",
        };
        WindowInfo[] windows = [new("TeamViewer", 100, 3), new("Chat", 100, 4)];
        DestinationResolver resolver = new(tcp, dns);

        Assert.Throws<MonitoringConfigurationException>(
            () => resolver.Resolve(profile, setting, AnyProcess, 3, windows, []));
    }

    /// <summary>
    /// Ein <b>leerer</b> Ausdruck bleibt dagegen eine gültige Einstellung und ergibt den Platzhalter
    /// — genau wie in der Vorlage.
    /// </summary>
    [Fact]
    public void LeererAusdruckBleibtDerPlatzhalterfall()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["mstsc"] with { RegexPattern = null };
        WindowInfo[] windows = [new("SRV - Remote", 100, 1)];
        DestinationResolver resolver = new(tcp, dns);

        IReadOnlyList<ResolvedDestination> result =
            resolver.Resolve(profile, Active("mstsc"), AnyProcess, 1, windows, []);

        Assert.Equal(SessionConstants.NoDestinationTitle, Assert.Single(result).Destination);
    }

    [Fact]
    public void VerbindungsverfahrenNimmtGegenstellenVonProzessUndKind()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["WinSCP"];
        ProcessInfo process = new(200, 4, "WinSCP", null);
        tcp.Set(200, "203.0.113.10").Set(201, "198.51.100.4");
        dns.Names["203.0.113.10"] = "sftp.kunde.example";

        DestinationResolver resolver = new(tcp, dns);
        IReadOnlyList<ResolvedDestination> result =
            resolver.Resolve(profile, Active("WinSCP"), process, 0, [], [201]);

        Assert.Equal(2, result.Count);
        Assert.Equal("sftp.kunde.example [203.0.113.10]", result[0].Destination);
        Assert.Equal("203.0.113.10", result[0].IdentityKey);
        Assert.Equal("[198.51.100.4]", result[1].Destination);
        Assert.Equal("198.51.100.4", result[1].IdentityKey);
    }

    /// <summary>
    /// Der Bezeichner bleibt die Adresse, auch wenn der Name erst später eintrifft. Sonst würde das
    /// Eintreffen des Namens die laufende Sitzung beenden und eine neue beginnen.
    /// </summary>
    [Fact]
    public void BezeichnerBleibtDieAdresseAuchWennDerNameSpaeterKommt()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["WinSCP"];
        ProcessInfo process = new(200, 4, "WinSCP", null);
        tcp.Set(200, "203.0.113.10");
        DestinationResolver resolver = new(tcp, dns);

        string before = resolver.Resolve(profile, Active("WinSCP"), process, 0, [], [])[0].IdentityKey;
        dns.Names["203.0.113.10"] = "sftp.kunde.example";
        ResolvedDestination after = resolver.Resolve(profile, Active("WinSCP"), process, 0, [], [])[0];

        Assert.Equal(before, after.IdentityKey);
        Assert.NotEqual("[203.0.113.10]", after.Destination);
    }

    [Fact]
    public void VerbindungsverfahrenFiltertSchleifeUndUnbrauchbareAdressen()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["WinSCP"];
        ProcessInfo process = new(200, 4, "WinSCP", null);
        tcp.Set(200, "127.0.0.1", "0.0.0.0", "255.255.255.255", "203.0.113.10", "203.0.113.10");

        DestinationResolver resolver = new(tcp, dns);
        IReadOnlyList<ResolvedDestination> result =
            resolver.Resolve(profile, Active("WinSCP"), process, 0, [], []);

        Assert.Equal("203.0.113.10", Assert.Single(result).IdentityKey);
    }

    [Fact]
    public void VerbindungsverfahrenBeachtetProfilUndBenutzerausschluesse()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["RemoteDesktopManager"];
        ProcessInfo process = new(300, 4, "RemoteDesktopManager", null);
        tcp.Set(300, "157.56.28.125", "10.5.5.5", "203.0.113.77");
        dns.Names["203.0.113.77"] = "srv.sucuri.net";

        DestinationResolver resolver = new(tcp, dns);
        IReadOnlyList<ResolvedDestination> result = resolver.Resolve(
            profile,
            new MonitoringSetting { Key = "RemoteDesktopManager", RemoteSupportTypeId = 1001, ExcludeIpAddresses = "10.0.0.0/8" },
            process, 0, [], []);

        Assert.Empty(result);
    }

    private static MonitoringSetting Active(string key) =>
        new() { Key = key, RemoteSupportTypeId = 1001 };

    private IReadOnlyList<ResolvedDestination> Resolve(MonitoringProfile profile, WindowInfo[] windows,
                                                      nint mainWindowHandle)
    {
        DestinationResolver resolver = new(tcp, dns);
        return resolver.Resolve(profile, Active(profile.Key), AnyProcess, mainWindowHandle, windows, []);
    }
}
