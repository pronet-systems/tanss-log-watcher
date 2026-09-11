using System.Net;
using TanssLogWatcher.Monitoring.Model;
using Xunit;

namespace TanssLogWatcher.Monitoring.Tests;

/// <summary>
/// Nagelt den Katalog fest.
/// </summary>
/// <remarks>
/// Die Muster sind an echten Fenstertiteln erprobt; ein versehentlich verändertes Zeichen fällt
/// im Betrieb erst auf, wenn eine Fernwartung fehlt oder ein falsches Ziel trägt. Diese Prüfungen
/// sind die einzige Stelle, an der so etwas vorher auffällt.
/// </remarks>
public sealed class MonitoringProfilesTests
{
    [Fact]
    public void KatalogEnthaeltGenauSechsunddreissigProfile()
    {
        Assert.Equal(36, MonitoringProfiles.All.Count);
        Assert.Equal(36, MonitoringProfiles.ByKey.Count);
    }

    [Fact]
    public void JedesProfilHatEinenEindeutigenSchluesselUndProzessnamen()
    {
        foreach (MonitoringProfile profile in MonitoringProfiles.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Key));
            Assert.False(string.IsNullOrWhiteSpace(profile.TypeDescription));
            Assert.NotEmpty(profile.ProcessNames);
        }

        Assert.Equal(MonitoringProfiles.All.Count,
                     MonitoringProfiles.All.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("mstsc", DestinationResolveMethod.WindowTitleRegex, @".+(?=\s+-)")]
    [InlineData("RdClient.Windows", DestinationResolveMethod.WindowTitleRegex, @".+(?=\s+-)")]
    [InlineData("devenv", DestinationResolveMethod.WindowTitleRegex, @".+?(?= -|\(.+\))")]
    [InlineData("AnyDesk", DestinationResolveMethod.WindowTitleRegex, @".+(?=\s+-)")]
    [InlineData("putty", DestinationResolveMethod.WindowTitleRegex, @".+(?=\s+-)")]
    [InlineData("OUTLOOK|Message", DestinationResolveMethod.WindowTitleRegex,
                @".+(?=\s-\s+(Nachricht|Message)\s\(.+\))")]
    [InlineData("Mail|Message", DestinationResolveMethod.WindowTitleRegex,
                @".+(?=\s-\s+(Nachricht|Message)\s\-\sMail)")]
    [InlineData("ScreenConnect.WindowsClient", DestinationResolveMethod.WindowTitleRegex,
                @".+(?=\s+-\s((ConnectWise\sControl|(ScreenConnect))))")]
    [InlineData("Zoom", DestinationResolveMethod.WindowTitleRegex, @"(Zoom\sMeeting.*)|Zoom(?![-])")]
    [InlineData("TeamViewer", DestinationResolveMethod.WindowTitleRegex, @"^.+(?=\s+-\s+TeamViewer)")]
    [InlineData("KaseyaLiveConnectRM", DestinationResolveMethod.WindowTitleRegex, @"^.+(?=::(Private|Shared))")]
    [InlineData("mRemoteNG", DestinationResolveMethod.WindowTitleRegex, @"(?<=\s-\s)[^-]+$")]
    [InlineData("tvnviewer", DestinationResolveMethod.WindowTitleRegex, @".+(?=\s-\sTightVNC Viewer)")]
    [InlineData("Rustdesk", DestinationResolveMethod.WindowTitleRegex, @".+(?=\s-\sRemote Desktop\s-\sRustDesk)")]
    [InlineData("Pulsway", DestinationResolveMethod.WindowTitleRegex,
                @".+(?=\s+-\s+Pulseway Remote Control Connection)")]
    [InlineData("host", DestinationResolveMethod.WindowTitle, null)]
    [InlineData("WindowsTerminal", DestinationResolveMethod.WindowTitle, null)]
    [InlineData("cmd", DestinationResolveMethod.WindowTitle, null)]
    [InlineData("RoyalTS", DestinationResolveMethod.WindowTitle, null)]
    [InlineData("WinSCP", DestinationResolveMethod.RemoteHostIpConnection, null)]
    [InlineData("OpenVPN", DestinationResolveMethod.RemoteHostIpConnection, null)]
    [InlineData("MobaXterm", DestinationResolveMethod.RemoteHostIpConnection, null)]
    public void ProfilTraegtVerfahrenUndMusterWortgetreu(string key, DestinationResolveMethod method,
                                                        string? pattern)
    {
        MonitoringProfile profile = Assert.IsType<MonitoringProfile>(MonitoringProfiles.Find(key));

        Assert.Equal(method, profile.DestinationResolveMethod);
        Assert.Equal(pattern, profile.RegexPattern);
    }

    [Fact]
    public void SchalterDerEmpfindlichenProfileStehenWieInDerVorlage()
    {
        MonitoringProfile outlook = MonitoringProfiles.ByKey["OUTLOOK|Message"];
        Assert.True(outlook.IgnoreTitleChanges);
        Assert.True(outlook.MonitorOnlyRegexMatchedWindow);
        Assert.False(outlook.MonitorOnlyMainWindowHandle);

        MonitoringProfile devenv = MonitoringProfiles.ByKey["devenv"];
        Assert.True(devenv.MonitorOnlyMainWindowHandle);
        Assert.True(devenv.IgnoreTitleChanges);
        Assert.False(devenv.MonitorOnlyRegexMatchedWindow);

        MonitoringProfile mstsc = MonitoringProfiles.ByKey["mstsc"];
        Assert.False(mstsc.IgnoreTitleChanges);
        Assert.False(mstsc.MonitorOnlyMainWindowHandle);
        Assert.False(mstsc.MonitorOnlyRegexMatchedWindow);

        Assert.True(MonitoringProfiles.ByKey["OpenVPN"].NoGui);
        Assert.Equal(1, MonitoringProfiles.All.Count(p => p.NoGui));
    }

    [Fact]
    public void PcvisitIgnoriertSeineEigenenVerwaltungsfenster()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["host"];

        Assert.True(profile.IsIgnoredTitle("pcvisit Support"));
        Assert.True(profile.IsIgnoredTitle("pcvisit Support Supporter-Modul (pcvisit host)"));
        Assert.False(profile.IsIgnoredTitle("pcvisit support"));
        Assert.False(profile.IsIgnoredTitle("Kunde Meier"));
    }

    [Fact]
    public void RemoteDesktopManagerBringtSeineAusschlusslistenMit()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["RemoteDesktopManager"];

        Assert.Contains(IPAddress.Parse("157.56.28.125"), profile.IgnoredIpAddresses);
        Assert.Contains(IPAddress.Loopback, profile.IgnoredIpAddresses);
        Assert.Equal(6, profile.IgnoredIpAddresses.Count);
        Assert.Equal(5, profile.IgnoredDnsDomains.Count);
        Assert.True(profile.IsIgnoredHostName("ec2-1-2-3-4.eu-central-1.compute.amazonaws.com"));
    }

    /// <summary>
    /// Der Suffixtest der Vorlage kennt keine Punktgrenze. Das ist bewusst übernommen: eine
    /// Korrektur würde bei bestehenden Einrichtungen stillschweigend andere Ziele erzeugen.
    /// </summary>
    [Fact]
    public void SuffixtestDerNamensausschluesseKenntKeinePunktgrenze()
    {
        MonitoringProfile profile = MonitoringProfiles.ByKey["RemoteDesktopManager"];

        Assert.True(profile.IsIgnoredHostName("notwp.com"));
        Assert.False(profile.IsIgnoredHostName(null));
        Assert.False(profile.IsIgnoredHostName(string.Empty));
    }

    [Theory]
    [InlineData("AnyDesk", "AnyDesk", true)]
    [InlineData("AnyDesk", "anydesk", true)]
    [InlineData("AnyDesk", "AnyDesk-Kunde", true)]
    [InlineData("AnyDesk", "AnyDesk2", false)]
    [InlineData("AnyDesk", "Desk", false)]
    [InlineData("MobaXterm", "MobaXterm_Personal_23", true)]
    [InlineData("MobaXterm", "MobaRTE", true)]
    [InlineData("MobaXterm", "Moba", false)]
    [InlineData("putty", "KiTTY", true)]
    [InlineData("putty", "kitty_portable", true)]
    [InlineData("putty", "kittycat", false)]
    public void PraefixregelDerProzessnamen(string key, string processName, bool expected) =>
        Assert.Equal(expected, MonitoringProfiles.ByKey[key].MatchesProcessName(processName));

    [Fact]
    public void LeererProzessnamePasstNie()
    {
        Assert.False(MonitoringProfiles.ByKey["mstsc"].MatchesProcessName(null));
        Assert.False(MonitoringProfiles.ByKey["mstsc"].MatchesProcessName(string.Empty));
    }

    [Fact]
    public void AbgleichErgaenztFehlendeUndEntferntUnbekannteSchluessel()
    {
        MonitoringSetting[] stored =
        [
            new() { Key = "mstsc", RemoteSupportTypeId = 1003 },
            new() { Key = "verschwundenes-profil", RemoteSupportTypeId = 1004 },
        ];

        IReadOnlyList<MonitoringSetting> reconciled = MonitoringProfiles.Reconcile(stored);

        Assert.Equal(36, reconciled.Count);
        Assert.Equal(1003, reconciled.Single(s => s.Key == "mstsc").RemoteSupportTypeId);
        Assert.DoesNotContain(reconciled, s => s.Key == "verschwundenes-profil");
        Assert.Equal(35, reconciled.Count(s => !s.IsActive));
    }

    [Fact]
    public void AbgleichOhneGespeicherteWerteSchaltetNichtsEin()
    {
        IReadOnlyList<MonitoringSetting> reconciled = MonitoringProfiles.Reconcile(null);

        Assert.Equal(36, reconciled.Count);
        Assert.All(reconciled, s => Assert.False(s.IsActive));
    }

    /// <summary>Eine beschädigte Datei mit doppeltem Schlüssel darf die Einrichtung nicht sprengen.</summary>
    [Fact]
    public void AbgleichUeberlebtDoppelteSchluessel()
    {
        MonitoringSetting[] stored =
        [
            new() { Key = "mstsc", RemoteSupportTypeId = 1003 },
            new() { Key = "mstsc", RemoteSupportTypeId = 1009 },
        ];

        IReadOnlyList<MonitoringSetting> reconciled = MonitoringProfiles.Reconcile(stored);

        Assert.Equal(36, reconciled.Count);
        Assert.Equal(1003, reconciled.Single(s => s.Key == "mstsc").RemoteSupportTypeId);
    }
}
