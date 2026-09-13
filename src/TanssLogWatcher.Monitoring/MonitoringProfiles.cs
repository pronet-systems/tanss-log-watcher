using System.Collections.ObjectModel;
using System.Net;
using TanssLogWatcher.Monitoring.Model;

namespace TanssLogWatcher.Monitoring;

/// <summary>
/// Der feste Katalog der überwachbaren Anwendungen.
/// </summary>
/// <remarks>
/// Wortgetreu aus der Vorlage übernommen, einschließlich der regulären Ausdrücke. Jedes Zeichen
/// darin ist an einem echten Fenstertitel erprobt worden; eine Glättung ohne Gegenprobe an der
/// jeweiligen Anwendung ändert stillschweigend, welche Sitzungen erkannt werden.
/// <para>Der Katalog enthält ausdrücklich auch Anwendungen, die keine Fernwartung sind — etwa
/// Visual Studio oder eine Eingabeaufforderung. Sie sind, wie alle anderen, ab Werk abgeschaltet;
/// erst die Zuordnung auf einen TANSS-Fernwartungstyp schaltet sie ein.</para>
/// </remarks>
public static class MonitoringProfiles
{
    /// <summary>Der gemeinsame Ausdruck: alles vor dem ersten Bindestrich.</summary>
    private const string TextBeforeHyphen = """.+(?=\s+-)""";

    private static readonly ReadOnlyCollection<MonitoringProfile> AllProfiles = BuildAll();

    private static readonly ReadOnlyDictionary<string, MonitoringProfile> ByKeyLookup =
        AllProfiles.ToDictionary(p => p.Key, StringComparer.Ordinal).AsReadOnly();

    /// <summary>Alle Profile in der Reihenfolge der Vorlage.</summary>
    public static IReadOnlyList<MonitoringProfile> All => AllProfiles;

    /// <summary>Alle Profile nach Schlüssel.</summary>
    public static IReadOnlyDictionary<string, MonitoringProfile> ByKey => ByKeyLookup;

    /// <summary>Sucht ein Profil. Liefert <c>null</c> für einen unbekannten Schlüssel.</summary>
    /// <param name="key">Der Profilschlüssel.</param>
    public static MonitoringProfile? Find(string key) =>
        ByKeyLookup.TryGetValue(key, out MonitoringProfile? profile) ? profile : null;

    /// <summary>
    /// Gleicht eine gespeicherte Einstellungsliste mit dem Katalog ab.
    /// </summary>
    /// <remarks>
    /// Fehlende Schlüssel kommen abgeschaltet hinzu, unbekannte fallen weg. So überlebt die
    /// Konfiguration sowohl eine neue Programmfassung mit zusätzlichen Profilen als auch eine
    /// Rückkehr zur alten, ohne dass der Techniker etwas von Hand nachziehen muss.
    /// </remarks>
    /// <param name="stored">Die gespeicherten Einstellungen, gern leer.</param>
    /// <returns>Eine Liste in Katalogreihenfolge mit genau einem Eintrag je Profil.</returns>
    public static IReadOnlyList<MonitoringSetting> Reconcile(IEnumerable<MonitoringSetting>? stored)
    {
        Dictionary<string, MonitoringSetting> known = new(StringComparer.Ordinal);
        if (stored is not null)
        {
            foreach (MonitoringSetting setting in stored)
            {
                // Der erste Eintrag je Schluessel gewinnt; Doppelte aus einer beschaedigten Datei
                // wuerden sonst eine Ausnahme werfen und die gesamte Konfiguration verwerfen.
                known.TryAdd(setting.Key, setting);
            }
        }

        List<MonitoringSetting> result = new(AllProfiles.Count);
        foreach (MonitoringProfile profile in AllProfiles)
        {
            result.Add(known.TryGetValue(profile.Key, out MonitoringSetting? existing)
                ? existing
                : new MonitoringSetting { Key = profile.Key });
        }

        return result;
    }

    private static ReadOnlyCollection<MonitoringProfile> BuildAll() =>
    [
        new MonitoringProfile
        {
            Key = "mstsc",
            YieldsDeviceIdentity = true,
            TypeDescription = "Microsoft Remotedesktop",
            ProcessNames = ["mstsc"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RegexPattern = TextBeforeHyphen,
        },
        new MonitoringProfile
        {
            Key = "RdClient.Windows",
            YieldsDeviceIdentity = true,
            TypeDescription = "Microsoft Remotedesktop (Store App)",
            ProcessNames = ["RdClient.Windows"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RegexPattern = TextBeforeHyphen,
        },
        new MonitoringProfile
        {
            Key = "devenv",
            TypeDescription = "Microsoft Visual Studio",
            ProcessNames = ["devenv"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RegexPattern = """.+?(?= -|\(.+\))""",
            IgnoreTitleChanges = true,
            MonitorOnlyMainWindowHandle = true,
        },
        new MonitoringProfile
        {
            Key = "AnyDesk",
            YieldsDeviceIdentity = true,
            TypeDescription = "AnyDesk",
            ProcessNames = ["AnyDesk", "AnyDesk-*"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RequiresResolvedDestination = true,
            RegexPattern = TextBeforeHyphen,
            IgnoreTitleChanges = true,
        },
        new MonitoringProfile
        {
            Key = "BASEClient",
            YieldsDeviceIdentity = true,
            TypeDescription = "Solar Winds - Take Control",
            ProcessNames = ["BASEClient", "TakeControlRDViewer", "NCentralRDViewer"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RegexPattern = TextBeforeHyphen,
            IgnoreTitleChanges = true,
            MonitorOnlyMainWindowHandle = true,
        },
        new MonitoringProfile
        {
            Key = "host",
            TypeDescription = "pcvisit Support Supporter-Modul",
            ProcessNames = ["host"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitle,
            IgnoreTitleChanges = true,
            IgnoredTitles = ["pcvisit Support", "pcvisit Support Supporter-Modul (pcvisit host)"],
        },
        new MonitoringProfile
        {
            Key = "putty",
            YieldsDeviceIdentity = true,
            TypeDescription = "PuTTY / KiTTY",
            ProcessNames = ["putty", "kitty", "kitty_portable", "kitty_nocompress"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RegexPattern = TextBeforeHyphen,
            IgnoreTitleChanges = true,
        },
        new MonitoringProfile
        {
            Key = "WindowsTerminal",
            TypeDescription = "Windows Terminal (Store App)",
            ProcessNames = ["WindowsTerminal"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitle,
            IgnoreTitleChanges = true,
        },
        new MonitoringProfile
        {
            Key = "cmd",
            TypeDescription = "Eingabeaufforderung (cmd.exe)",
            ProcessNames = ["cmd"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitle,
            IgnoreTitleChanges = true,
        },
        new MonitoringProfile
        {
            Key = "AppMngr",
            TypeDescription = "WatchGuard System Manager",
            ProcessNames = ["AppMngr"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitle,
            IgnoreTitleChanges = true,
        },
        new MonitoringProfile
        {
            Key = "pm_fb",
            TypeDescription = "WatchGuard Fireware Policy Manager",
            ProcessNames = ["pm_fb"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitle,
            IgnoreTitleChanges = true,
        },
        new MonitoringProfile
        {
            Key = "VpxClient",
            YieldsDeviceIdentity = true,
            TypeDescription = "VMware vSphere Client",
            ProcessNames = ["VpxClient"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RegexPattern = TextBeforeHyphen,
            IgnoreTitleChanges = true,
        },
        new MonitoringProfile
        {
            Key = "OUTLOOK|Message",
            TypeDescription = "Microsoft Outlook - Nachrichtenfenster (legacy)",
            ProcessNames = ["OUTLOOK"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RegexPattern = """.+(?=\s-\s+(Nachricht|Message)\s\(.+\))""",
            IgnoreTitleChanges = true,
            MonitorOnlyRegexMatchedWindow = true,
        },
        new MonitoringProfile
        {
            Key = "Mail|Message",
            TypeDescription = "Microsoft Mail (Store App) - Nachrichtenfenster",
            ProcessNames = ["commsapps"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RegexPattern = """.+(?=\s-\s+(Nachricht|Message)\s\-\sMail)""",
            IgnoreTitleChanges = true,
            MonitorOnlyRegexMatchedWindow = true,
        },
        new MonitoringProfile
        {
            Key = "ScreenConnect.WindowsClient",
            YieldsDeviceIdentity = true,
            TypeDescription = "ConnectWise Control",
            ProcessNames = ["ScreenConnect.WindowsClient"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RequiresResolvedDestination = true,
            RegexPattern = """.+(?=\s+-\s((ConnectWise\sControl|(ScreenConnect))))""",
            IgnoreTitleChanges = true,
        },
        new MonitoringProfile
        {
            Key = "Zoom",
            TypeDescription = "Zoom Meeting",
            ProcessNames = ["Zoom"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RequiresResolvedDestination = true,
            RegexPattern = """(Zoom\sMeeting.*)|Zoom(?![-])""",
            IgnoreTitleChanges = true,
            MonitorOnlyRegexMatchedWindow = true,
        },
        new MonitoringProfile
        {
            Key = "RoyalTS",
            TypeDescription = "Royal TS",
            ProcessNames = ["RoyalTS"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitle,
            IgnoreTitleChanges = true,
            MonitorOnlyMainWindowHandle = true,
        },
        new MonitoringProfile
        {
            Key = "RemoteDesktopManager",
            YieldsDeviceIdentity = true,
            TypeDescription = "Devolutions Remote Desktop Manager",
            ProcessNames =
            [
                "RemoteDesktopManager",
                "RemoteDesktopManagerFree",
                "RemoteDesktopManager64",
                "RemoteDesktopManagerFree64",
            ],
            DestinationResolveMethod = DestinationResolveMethod.RemoteHostIpConnection,
            IgnoredIpAddresses =
            [
                IPAddress.Parse("157.56.28.125"),
                IPAddress.Parse("23.111.9.35"),
                IPAddress.Parse("40.116.232.96"),
                IPAddress.Parse("192.0.73.2"),
                IPAddress.Parse("151.139.128.14"),
                IPAddress.Loopback,
            ],
            IgnoredDnsDomains =
            [
                "compute.amazonaws.com",
                "1e100.net",
                "sucuri.net",
                "wp.com",
                "cloudfront.net",
            ],
        },
        new MonitoringProfile
        {
            Key = "OpenVPN",
            YieldsDeviceIdentity = true,
            TypeDescription = "OpenVPN",
            ProcessNames = ["OpenVPNConnect", "openvpn"],
            DestinationResolveMethod = DestinationResolveMethod.RemoteHostIpConnection,
            NoGui = true,
            IgnoredIpAddresses = [IPAddress.Loopback],
        },
        new MonitoringProfile
        {
            Key = "ASGRD",
            YieldsDeviceIdentity = true,
            TypeDescription = "Rocket/ASG Remote Desktop",
            ProcessNames = ["ASGRD", "ASGRDElevated", "RDConsole"],
            DestinationResolveMethod = DestinationResolveMethod.RemoteHostIpConnection,
            IgnoredIpAddresses = [IPAddress.Parse("193.240.199.35")],
            IgnoredDnsDomains = ["visionapp.com"],
        },
        new MonitoringProfile
        {
            Key = "WinSCP",
            YieldsDeviceIdentity = true,
            TypeDescription = "WinSCP",
            ProcessNames = ["WinSCP"],
            DestinationResolveMethod = DestinationResolveMethod.RemoteHostIpConnection,
        },
        new MonitoringProfile
        {
            Key = "ngadmin",
            YieldsDeviceIdentity = true,
            TypeDescription = "Barracuda Firewall Admin",
            ProcessNames = ["ngadminX", "ngadmin"],
            DestinationResolveMethod = DestinationResolveMethod.RemoteHostIpConnection,
        },
        new MonitoringProfile
        {
            Key = "MobaXterm",
            YieldsDeviceIdentity = true,
            TypeDescription = "MobaXterm",
            ProcessNames = ["MobaXterm", "MobaRTE", "MobaXterm_*"],
            DestinationResolveMethod = DestinationResolveMethod.RemoteHostIpConnection,
        },
        new MonitoringProfile
        {
            Key = "TeamViewer",
            YieldsDeviceIdentity = true,
            TypeDescription = "TeamViewer",
            ProcessNames = ["TeamViewer"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RequiresResolvedDestination = true,
            RegexPattern = """^.+(?=\s+-\s+TeamViewer)""",
            MonitorOnlyRegexMatchedWindow = true,
        },
        new MonitoringProfile
        {
            Key = "KaseyaLiveConnectRM",
            YieldsDeviceIdentity = true,
            TypeDescription = "Kaseya VSA - Remote Control",
            ProcessNames = ["KaseyaLiveConnect"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RequiresResolvedDestination = true,
            RegexPattern = """^.+(?=::(Private|Shared))""",
            MonitorOnlyRegexMatchedWindow = true,
        },
        new MonitoringProfile
        {
            Key = "ISLLight",
            YieldsDeviceIdentity = true,
            TypeDescription = "ISL Light",
            ProcessNames = ["ISLLight"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RequiresResolvedDestination = true,
            RegexPattern = """.+(?=\s-\s.+)""",
            MonitorOnlyRegexMatchedWindow = true,
        },
        new MonitoringProfile
        {
            Key = "mRemoteNG",
            YieldsDeviceIdentity = true,
            TypeDescription = "mRemoteNG",
            ProcessNames = ["mRemoteNG"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RequiresResolvedDestination = true,
            RegexPattern = """(?<=\s-\s)[^-]+$""",
        },
        new MonitoringProfile
        {
            Key = "tvnviewer",
            YieldsDeviceIdentity = true,
            TypeDescription = "TightVNC Viewer",
            ProcessNames = ["tvnviewer"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RequiresResolvedDestination = true,
            RegexPattern = """.+(?=\s-\sTightVNC Viewer)""",
            MonitorOnlyRegexMatchedWindow = true,
        },
        new MonitoringProfile
        {
            Key = "Supremo",
            YieldsDeviceIdentity = true,
            TypeDescription = "SupRemo",
            ProcessNames = ["Supremo"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RequiresResolvedDestination = true,
            RegexPattern = """.+(?=\s-\sRemote Desktop|\s-\sDesktop Remoto|\s-\sBureau Distant|\s-\sExtern Bureaublad|\s-\sZdalny Pulpit|\s-\sEscritorio remoto|\s-\sDesktop Viewer)""",
            MonitorOnlyRegexMatchedWindow = true,
        },
        new MonitoringProfile
        {
            Key = "Code",
            TypeDescription = "Visual Studio Code",
            ProcessNames = ["Code"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RegexPattern = TextBeforeHyphen,
            IgnoreTitleChanges = true,
        },
        new MonitoringProfile
        {
            Key = "powershell",
            TypeDescription = "Powershell",
            ProcessNames = ["powershell"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitle,
            IgnoreTitleChanges = true,
        },
        new MonitoringProfile
        {
            Key = "powershell_ise",
            TypeDescription = "Powershell ISE",
            ProcessNames = ["powershell_ise"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitle,
            IgnoreTitleChanges = true,
        },
        new MonitoringProfile
        {
            Key = "Splashtop",
            TypeDescription = "Splashtop Business",
            ProcessNames = ["strwinclt"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitle,
            IgnoreTitleChanges = true,
        },
        new MonitoringProfile
        {
            Key = "Rustdesk",
            YieldsDeviceIdentity = true,
            TypeDescription = "Rustdesk",
            ProcessNames = ["rustdesk"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RequiresResolvedDestination = true,
            RegexPattern = """.+(?=\s-\sRemote Desktop\s-\sRustDesk)""",
            IgnoreTitleChanges = true,
            MonitorOnlyRegexMatchedWindow = true,
        },
        new MonitoringProfile
        {
            Key = "ncplayer",
            TypeDescription = "Ninja Remote",
            ProcessNames = ["ncplayer"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitle,
            IgnoreTitleChanges = true,
        },
        new MonitoringProfile
        {
            Key = "Pulsway",
            YieldsDeviceIdentity = true,
            TypeDescription = "Pulsway Remote Control",
            ProcessNames = ["RemoteDesktop"],
            DestinationResolveMethod = DestinationResolveMethod.WindowTitleRegex,
            RegexPattern = """.+(?=\s+-\s+Pulseway Remote Control Connection)""",
            MonitorOnlyRegexMatchedWindow = true,
        },
    ];
}
