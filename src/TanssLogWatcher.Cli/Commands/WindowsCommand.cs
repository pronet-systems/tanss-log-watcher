using System.Globalization;
using System.Net;
using TanssLogWatcher.Cli.Output;
using TanssLogWatcher.Monitoring;
using TanssLogWatcher.Monitoring.Model;

namespace TanssLogWatcher.Cli.Commands;

/// <summary>Was <c>windows</c> braucht.</summary>
public sealed record WindowsInputs
{
    /// <summary>Quelle der sichtbaren Fenster.</summary>
    public required IWindowSource Windows { get; init; }

    /// <summary>Quelle der Prozessmomentaufnahme.</summary>
    public required IProcessSource Processes { get; init; }

    /// <summary>Quelle der offenen TCP-Verbindungen.</summary>
    public required ITcpConnectionSource TcpConnections { get; init; }

    /// <summary>Die Rückwärtsauflösung; <c>null</c> zeigt nur Adressen.</summary>
    public IHostNameResolver? HostNames { get; init; }

    /// <summary>
    /// Die Beobachtungseinstellungen, falls eine Konfiguration vorliegt.
    /// </summary>
    /// <remarks>
    /// Der Befehl läuft ausdrücklich auch ohne: Er ist das Werkzeug, mit dem ein Techniker ein
    /// nicht mehr greifendes Muster repariert — also gerade dann zu gebrauchen, wenn die
    /// Einrichtung noch nicht steht.
    /// </remarks>
    public IReadOnlyList<MonitoringSetting> Settings { get; init; } = [];
}

/// <summary>
/// Listet die sichtbaren Fenster mit allem, woran die Erkennung hängt.
/// </summary>
/// <remarks>
/// <para>Im Original war diese Ansicht hinter einem Rechts-Doppelklick auf das Tray-Symbol
/// versteckt. Sie gehört aber an die Oberfläche: Sie ist der einzige Weg, an dem ein Techniker
/// sieht, <b>warum</b> ein Muster nicht mehr greift — ein Programm hat seinen Prozessnamen
/// geändert, ein Fenster trägt plötzlich einen Bindestrich mehr, eine Sitzung läuft über einen
/// Kindprozess.</para>
/// <para><b>Fenstertitel werden hier im Klartext gezeigt.</b> Das ist kein Widerspruch zur
/// Schwärzung im Protokoll: Der Techniker sieht seinen eigenen Bildschirm und braucht genau
/// diesen Titel, um sein Muster zu bauen. Geschwärzt wird, was auf die Platte geht.</para>
/// </remarks>
public static class WindowsCommand
{
    /// <summary>Führt den Befehl aus.</summary>
    /// <param name="inputs">Die Quellen.</param>
    /// <param name="profileKey">Ein Profilschlüssel für die Zielprobe; <c>null</c> lässt sie weg.</param>
    /// <param name="writer">Wohin die Ausgabe geht.</param>
    /// <returns>0, oder 64 bei einem unbekannten Profilschlüssel.</returns>
    public static int Run(WindowsInputs inputs, string? profileKey, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(writer);

        IReadOnlyList<WindowInfo> windows = inputs.Windows.GetVisibleWindows();
        IReadOnlyList<ProcessInfo> processes = inputs.Processes.Snapshot();

        Dictionary<int, ProcessInfo> byId = [];
        Dictionary<int, List<int>> childrenByParent = [];
        foreach (ProcessInfo process in processes)
        {
            byId[process.Id] = process;
            if (process.ParentId != 0 && process.ParentId != process.Id)
            {
                if (!childrenByParent.TryGetValue(process.ParentId, out List<int>? children))
                {
                    children = [];
                    childrenByParent[process.ParentId] = children;
                }

                children.Add(process.Id);
            }
        }

        Dictionary<int, List<WindowInfo>> byProcess = [];
        foreach (WindowInfo window in windows)
        {
            if (!byProcess.TryGetValue(window.ProcessId, out List<WindowInfo>? list))
            {
                list = [];
                byProcess[window.ProcessId] = list;
            }

            list.Add(window);
        }

        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{windows.Count} sichtbare Fenster in {byProcess.Count} Prozessen "
            + $"({processes.Count} Prozesse insgesamt)."));
        writer.WriteLine();

        foreach (int pid in byProcess.Keys.OrderBy(
                     key => byId.TryGetValue(key, out ProcessInfo p) ? p.Name : string.Empty,
                     StringComparer.OrdinalIgnoreCase).ThenBy(key => key))
        {
            WriteProcess(inputs, writer, pid, byId, byProcess[pid], childrenByParent);
        }

        return profileKey is null
            ? ExitCode.Healthy
            : WriteProfileProbe(inputs, profileKey, writer, byId, byProcess, childrenByParent);
    }

    /// <summary>Ein Prozess mit seinen Fenstern und Gegenstellen.</summary>
    private static void WriteProcess(WindowsInputs inputs, TextWriter writer, int pid,
                                     Dictionary<int, ProcessInfo> byId,
                                     List<WindowInfo> processWindows,
                                     Dictionary<int, List<int>> childrenByParent)
    {
        string name = byId.TryGetValue(pid, out ProcessInfo process)
            ? process.Name
            : "(Prozess inzwischen beendet)";

        List<int> children = childrenByParent.TryGetValue(pid, out List<int>? kids) ? kids : [];

        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}  (PID {pid}{(children.Count > 0 ? $", {children.Count} Kindprozesse" : string.Empty)})"));

        // Das Hauptfenster ist das erste BESITZERLOSE Fenster - dieselbe Regel wie in der
        // Zustandsmaschine. Schlicht das oberste zu nehmen, machte jeden offenen Dialog zum
        // vermeintlichen Hauptfenster, und genau daran scheitern die Profile mit
        // MonitorOnlyMainWindowHandle.
        nint mainHandle = 0;
        foreach (WindowInfo window in processWindows)
        {
            if (!window.IsOwned)
            {
                mainHandle = window.WindowHandle;
                break;
            }
        }

        foreach (WindowInfo window in processWindows)
        {
            string mark = window.WindowHandle == mainHandle && mainHandle != 0
                ? "Hauptfenster"
                : window.IsOwned ? "besessen" : "—";

            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Handle 0x{window.WindowHandle:X}  {mark,-12}  {window.Title}"));
        }

        WriteConnections(inputs, writer, pid, children);
        writer.WriteLine();
    }

    /// <summary>Die Gegenstellen der offenen Verbindungen dieses Prozesses.</summary>
    /// <remarks>
    /// Ein Fehler bleibt auf diesen einen Prozess beschränkt (Hausregel 5): Ein Prozess, der
    /// zwischen Momentaufnahme und Abfrage stirbt, darf nicht die ganze Liste kosten.
    /// </remarks>
    private static void WriteConnections(WindowsInputs inputs, TextWriter writer, int pid,
                                         IReadOnlyList<int> children)
    {
        IReadOnlyList<IPAddress> remotes;
        try
        {
            remotes = inputs.TcpConnections.GetRemoteAddresses(pid, children);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            writer.WriteLine("  Verbindungen: nicht lesbar — " + ex.Message);
            return;
        }

        if (remotes.Count == 0)
        {
            writer.WriteLine("  Verbindungen: keine");
            return;
        }

        IEnumerable<string> described = remotes.Select(address =>
        {
            string? host = inputs.HostNames?.GetCachedHostName(address);
            return host is null ? address.ToString() : $"{address} ({host})";
        });

        writer.WriteLine("  Verbindungen: " + string.Join(", ", described));
    }

    /// <summary>
    /// Die Zielprobe: Was würde dieses Profil aus den aktuellen Fenstern ableiten?
    /// </summary>
    /// <remarks>
    /// Sie läuft über denselben <see cref="DestinationResolver"/> wie der Betrieb. Eine
    /// nachgebaute Probe, die „ungefähr dasselbe“ täte, wäre wertlos: Gerade die Feinheiten —
    /// nur das Hauptfenster, nur getroffene Fenster, ausgeschlossene Titel — entscheiden, ob
    /// ein Muster greift.
    /// </remarks>
    private static int WriteProfileProbe(WindowsInputs inputs, string profileKey, TextWriter writer,
                                         Dictionary<int, ProcessInfo> byId,
                                         Dictionary<int, List<WindowInfo>> byProcess,
                                         Dictionary<int, List<int>> childrenByParent)
    {
        MonitoringProfile? profile = MonitoringProfiles.Find(profileKey);
        if (profile is null)
        {
            writer.WriteLine();
            writer.WriteLine($"Das Profil „{profileKey}“ gibt es nicht. Bekannt sind:");
            foreach (string line in Report.Wrap(
                         string.Join(", ", MonitoringProfiles.All.Select(entry => entry.Key)), 92))
            {
                writer.WriteLine("  " + line);
            }

            return ExitCode.Usage;
        }

        MonitoringSetting setting = inputs.Settings.FirstOrDefault(
                entry => string.Equals(entry.Key, profile.Key, StringComparison.Ordinal))
            ?? new MonitoringSetting { Key = profile.Key };

        writer.WriteLine();
        writer.WriteLine($"Profil „{profile.Key}“ ({profile.TypeDescription})");
        writer.WriteLine("  Prozessnamen:  " + string.Join(", ", profile.ProcessNames));
        writer.WriteLine("  Verfahren:     " + Method(profile.DestinationResolveMethod));
        writer.WriteLine("  Muster:        "
            + (setting.RegexPatternOverride ?? profile.RegexPattern ?? "(keines)"));
        writer.WriteLine("  Zuordnung:     " + (setting.IsActive
            ? string.Create(CultureInfo.InvariantCulture,
                $"Fernwartungstyp {setting.RemoteSupportTypeId}")
            : "nicht überwacht (keine Zuordnung in der Konfiguration)"));
        writer.WriteLine();

        DestinationResolver resolver = new(inputs.TcpConnections,
            inputs.HostNames ?? NullHostNames.Instance);

        int hits = 0;
        foreach ((int pid, ProcessInfo process) in byId.OrderBy(pair => pair.Key))
        {
            if (!profile.MatchesProcessName(process.Name))
            {
                continue;
            }

            hits++;
            List<WindowInfo> processWindows =
                byProcess.TryGetValue(pid, out List<WindowInfo>? list) ? list : [];
            List<int> children =
                childrenByParent.TryGetValue(pid, out List<int>? kids) ? kids : [];

            nint mainHandle = processWindows.FirstOrDefault(window => !window.IsOwned).WindowHandle;

            try
            {
                IReadOnlyList<ResolvedDestination> destinations = resolver.Resolve(
                    profile, setting, process, mainHandle, processWindows, children);

                if (destinations.Count == 0)
                {
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  PID {pid} ({process.Name}): kein Ziel — daraus entstünde keine Sitzung."));
                    continue;
                }

                foreach (ResolvedDestination destination in destinations)
                {
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  PID {pid} ({process.Name}): Ziel „{destination.Destination}“, "
                        + $"Bezeichner „{destination.IdentityKey}“"));
                }
            }
            catch (MonitoringConfigurationException ex)
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  PID {pid} ({process.Name}): Das Muster ist nicht übersetzbar — {ex.Message}"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  PID {pid} ({process.Name}): Ziel nicht ermittelbar — {ex.Message}"));
            }
        }

        if (hits == 0)
        {
            writer.WriteLine("  Derzeit läuft kein Prozess, auf den dieses Profil passt.");
        }

        return ExitCode.Healthy;
    }

    /// <summary>Die Bezeichnung eines Zielverfahrens in deutscher Prosa.</summary>
    private static string Method(DestinationResolveMethod method) => method switch
    {
        DestinationResolveMethod.WindowTitleRegex => "Fenstertitel über regulären Ausdruck",
        DestinationResolveMethod.WindowTitle => "Fenstertitel unverändert",
        DestinationResolveMethod.RemoteHostIpConnection => "Gegenstellen der TCP-Verbindungen",
        _ => method.ToString(),
    };

    /// <summary>
    /// Eine Rückwärtsauflösung, die nichts auflöst.
    /// </summary>
    /// <remarks>
    /// Für den Fall, dass der Befehl ohne Auflöser läuft. Sie wartet nie und stösst nichts an
    /// — was hier auch niemand will: Eine einmalige Anzeige soll keine DNS-Anfragen für jede
    /// gerade offene Verbindung erzeugen.
    /// </remarks>
    private sealed class NullHostNames : IHostNameResolver
    {
        public static NullHostNames Instance { get; } = new();

        public string? GetCachedHostName(IPAddress address) => null;

        public Task<string?> ResolveAsync(IPAddress address, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }
}
