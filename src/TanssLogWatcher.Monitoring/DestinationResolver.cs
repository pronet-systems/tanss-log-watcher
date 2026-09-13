using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.Monitoring.Model;

namespace TanssLogWatcher.Monitoring;

/// <summary>Ein aufgelöstes Ziel samt seinem identifizierenden Anteil.</summary>
/// <param name="Destination">Der Text, der später im Kommentar der Fernwartung steht.</param>
/// <param name="IdentityKey">Der Anteil, über den die Sitzung wiedererkannt wird.</param>
/// <param name="WindowHandle">Das Fenster, aus dem das Ziel stammt; 0, wenn keines beteiligt war.</param>
public readonly record struct ResolvedDestination(string Destination, string IdentityKey, nint WindowHandle);

/// <summary>
/// Bestimmt die Ziele eines Prozesses auf einem der drei Wege.
/// </summary>
/// <remarks>
/// Der Aufrufer entscheidet nichts: welches Verfahren greift, steht im Profil. Ein leeres Ergebnis
/// heißt <b>keine Sitzung</b> und beendet eine laufende — das ist der Unterschied zum Platzhalter,
/// der eine Sitzung am Leben hält.
/// </remarks>
public sealed class DestinationResolver
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Übersetzte Ausdrücke. Ein Eintrag mit Wert <c>null</c> merkt sich einen fehlerhaften
    /// Ausdruck, damit er nicht in jedem Durchlauf erneut übersetzt und erneut gemeldet wird.
    /// </summary>
    private readonly ConcurrentDictionary<string, Regex?> compiled = new(StringComparer.Ordinal);

    private readonly ITcpConnectionSource tcpConnections;
    private readonly IHostNameResolver hostNames;
    private readonly ILogger<DestinationResolver> logger;

    /// <summary>Erzeugt den Auflöser.</summary>
    /// <param name="tcpConnections">Quelle der offenen TCP-Verbindungen.</param>
    /// <param name="hostNames">Rückwärtsauflösung mit Zwischenspeicher.</param>
    /// <param name="logger">Protokoll; <c>null</c> schaltet es ab.</param>
    public DestinationResolver(ITcpConnectionSource tcpConnections, IHostNameResolver hostNames,
                               ILogger<DestinationResolver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tcpConnections);
        ArgumentNullException.ThrowIfNull(hostNames);

        this.tcpConnections = tcpConnections;
        this.hostNames = hostNames;
        this.logger = logger ?? NullLogger<DestinationResolver>.Instance;
    }

    /// <summary>
    /// Bestimmt alle Ziele eines Prozesses.
    /// </summary>
    /// <param name="profile">Das Profil, das den Prozess erfasst hat.</param>
    /// <param name="setting">Die Einstellung des Technikers zu diesem Profil.</param>
    /// <param name="process">Der Prozess.</param>
    /// <param name="mainWindowHandle">
    /// Das Hauptfenster des Prozesses, also sein erstes sichtbares Fenster mit Titel; 0, wenn er
    /// keines hat.
    /// </param>
    /// <param name="processWindows">Die Fenster genau dieses Prozesses, in Enumerationsreihenfolge.</param>
    /// <param name="childProcessIds">Die Kindprozesse, für die Verbindungsauswertung.</param>
    /// <returns>Die Ziele; eine leere Liste bedeutet, dass keine Sitzung besteht.</returns>
    /// <exception cref="MonitoringConfigurationException">
    /// Das Profil oder die Einstellung trägt einen unübersetzbaren regulären Ausdruck. Der Aufrufer
    /// überspringt diesen Prozess, führt den Durchlauf zu Ende und meldet den Fehler einmal.
    /// </exception>
    public IReadOnlyList<ResolvedDestination> Resolve(
        MonitoringProfile profile,
        MonitoringSetting setting,
        ProcessInfo process,
        nint mainWindowHandle,
        IReadOnlyList<WindowInfo> processWindows,
        IReadOnlyList<int> childProcessIds)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(processWindows);
        ArgumentNullException.ThrowIfNull(childProcessIds);

        return profile.DestinationResolveMethod switch
        {
            DestinationResolveMethod.WindowTitleRegex =>
                ResolveByRegex(profile, setting, mainWindowHandle, processWindows),
            DestinationResolveMethod.WindowTitle =>
                ResolveByTitle(profile, processWindows),
            DestinationResolveMethod.RemoteHostIpConnection =>
                ResolveByConnection(profile, setting, process, mainWindowHandle, childProcessIds),
            _ => [],
        };
    }

    /// <exception cref="MonitoringConfigurationException">
    /// Der eingestellte Ausdruck ist nicht übersetzbar. Siehe dort, wie darauf zu reagieren ist.
    /// </exception>
    private List<ResolvedDestination> ResolveByRegex(
        MonitoringProfile profile, MonitoringSetting setting, nint mainWindowHandle,
        IReadOnlyList<WindowInfo> processWindows)
    {
        string? pattern = setting.RegexPatternOverride ?? profile.RegexPattern;

        // Ein leerer Ausdruck ist eine gueltige Einstellung — er bedeutet „ohne Muster“ und
        // liefert dafuer den Platzhalter. Ein GESETZTER, aber unuebersetzbarer Ausdruck ist
        // dagegen ein Konfigurationsfehler; ihn wie „ohne Muster“ zu behandeln, setzte
        // MonitorOnlyRegexMatchedWindow stillschweigend ausser Kraft.
        if (!string.IsNullOrEmpty(pattern))
        {
            Regex regex = GetRegex(profile.Key, pattern) ?? throw new MonitoringConfigurationException(
                profile.Key,
                $"Der reguläre Ausdruck des Profils '{profile.Key}' ist nicht übersetzbar. Bis er " +
                "in den Einstellungen berichtigt ist, liefert dieses Profil keine Sitzungen.");

            List<ResolvedDestination> matches = [];
            bool suppressed = false;

            foreach (WindowInfo window in processWindows)
            {
                if (profile.IsIgnoredTitle(window.Title) ||
                    (profile.MonitorOnlyMainWindowHandle && window.WindowHandle != mainWindowHandle))
                {
                    suppressed = true;
                    continue;
                }

                Match match;
                try
                {
                    match = regex.Match(window.Title);
                }
                catch (RegexMatchTimeoutException)
                {
                    // Ein Ausdruck, der an einem langen Titel haengenbleibt, darf den Durchlauf
                    // nicht anhalten. Der Titel gilt dann als nicht getroffen.
                    logger.LogWarning("Regex des Profils {ProfileKey} lief in die Zeitgrenze.", profile.Key);
                    match = Match.Empty;
                }

                if (match.Success)
                {
                    matches.Add(new ResolvedDestination(match.Value, match.Value, window.WindowHandle));
                }
                else if (profile.MonitorOnlyRegexMatchedWindow)
                {
                    suppressed = true;
                }
            }

            if (matches.Count > 0)
            {
                return matches;
            }

            // Ein unterdruecktes Fenster ist eine bewusste Aussage: dieser Prozess zeigt gerade
            // nichts Ueberwachenswertes. Der Platzhalter wuerde hier eine Sitzung erfinden.
            if (suppressed)
            {
                return [];
            }

            // Eine Anwendung, die dauerhaft offen steht, hat mit einem nicht passenden Titel
            // ebenfalls eine Aussage getroffen: Es laeuft gerade keine Fernwartung. Der
            // Platzhalter ist fuer ein Fenster OHNE Titel gedacht - und ein solches liefert die
            // Fensterquelle gar nicht erst mit, es kommt also hier nie an.
            if (profile.RequiresResolvedDestination && processWindows.Count > 0)
            {
                return [];
            }
        }

        // Ohne gesetztes Muster gaebe es nichts zu erkennen - dann bleibt der Platzhalter auch
        // fuer eine dauerhaft offene Anwendung, sonst lieferte sie nie wieder eine Sitzung.
        return [new ResolvedDestination(SessionConstants.NoDestinationTitle,
                                        SessionConstants.NoDestinationTitle, mainWindowHandle)];
    }

    /// <remarks>
    /// <see cref="MonitoringProfile.MonitorOnlyMainWindowHandle"/> wirkt hier bewusst <b>nicht</b>.
    /// Royal TS setzt die Angabe, in diesem Zweig wurde sie aber noch nie ausgewertet; wer sie
    /// jetzt beachtete, würde bei genau dieser Anwendung stillschweigend andere Sitzungen liefern
    /// als jede bisherige Fassung.
    /// </remarks>
    private static List<ResolvedDestination> ResolveByTitle(
        MonitoringProfile profile, IReadOnlyList<WindowInfo> processWindows)
    {
        List<ResolvedDestination> result = [];
        foreach (WindowInfo window in processWindows)
        {
            if (!profile.IsIgnoredTitle(window.Title))
            {
                result.Add(new ResolvedDestination(window.Title, window.Title, window.WindowHandle));
            }
        }

        return result;
    }

    private List<ResolvedDestination> ResolveByConnection(
        MonitoringProfile profile, MonitoringSetting setting, ProcessInfo process,
        nint mainWindowHandle, IReadOnlyList<int> childProcessIds)
    {
        IReadOnlyList<IPAddress> addresses = tcpConnections.GetRemoteAddresses(process.Id, childProcessIds);

        List<ResolvedDestination> result = [];
        HashSet<IPAddress> seen = [];

        foreach (IPAddress address in addresses)
        {
            if (IpFilter.IsUninteresting(address) || !seen.Add(address))
            {
                continue;
            }

            if (profile.IgnoredIpAddresses.Contains(address) ||
                IpFilter.IsExcluded(address, setting.ExcludeIpAddresses))
            {
                continue;
            }

            string? hostName = hostNames.GetCachedHostName(address);
            if (profile.IsIgnoredHostName(hostName))
            {
                continue;
            }

            // Der Bezeichner ist die Adresse allein. Der Name kommt haeufig erst einen Durchlauf
            // spaeter aus dem Zwischenspeicher; er darf die Sitzung nicht neu beginnen lassen.
            string destination = hostName is null ? $"[{address}]" : $"{hostName} [{address}]";
            result.Add(new ResolvedDestination(destination, address.ToString(), mainWindowHandle));
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Prozess {ProcessId} ({ProfileKey}): {Count} Gegenstellen nach Filterung.",
                            process.Id, profile.Key, result.Count);
        }
        return result;
    }

    private Regex? GetRegex(string profileKey, string pattern) =>
        compiled.GetOrAdd(pattern, static (p, state) =>
        {
            try
            {
                return new Regex(p, RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                state.Logger.LogError(ex, "Profil {ProfileKey} trägt einen unübersetzbaren Ausdruck; " +
                                          "bis zur Berichtigung entsteht dafür keine Sitzung.", state.Key);
                return null;
            }
        }, (Key: profileKey, Logger: logger));
}
