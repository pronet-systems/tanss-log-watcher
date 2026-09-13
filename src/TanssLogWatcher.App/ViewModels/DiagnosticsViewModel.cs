using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Monitoring.Model;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Die Diagnoseseite: was das Werkzeug gerade sieht.
/// </summary>
/// <remarks>
/// <para><b>Die Antwort auf „warum wird meine Anwendung nicht erkannt?“.</b> Diese Seite zeigt
/// ungefiltert alle sichtbaren Fenster mit Prozess, Titel, Handle und offenen Verbindungen —
/// also genau die vier Angaben, aus denen die Erkennung ihre Entscheidung ableitet. Wer hier
/// sein Fenster nicht findet, hat kein Zuordnungsproblem, sondern ein Sichtbarkeitsproblem.</para>
///
/// <para><b>Diese Seite fragt nichts ab, solange sie nicht gefragt wird.</b> Fenstertitel
/// tragen Kundennamen und E-Mail-Betreffe. Eine Liste, die im Sekundentakt von selbst
/// mitliefe, wäre eine Mitschrift des Arbeitstags auf dem Bildschirm — auch wenn niemand
/// hinsieht.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class DiagnosticsViewModel : RuntimeViewModel
{
    /// <summary>Baut die Seite. Geholt wird erst auf Aufforderung.</summary>
    /// <param name="host">Die Laufzeit.</param>
    public DiagnosticsViewModel(AppHost host) : base(host) => Refresh();

    /// <summary>Die sichtbaren Fenster.</summary>
    public ObservableCollection<WindowRow> Windows { get; } = [];

    /// <summary>Die Zusammenfassung über der Liste.</summary>
    [ObservableProperty]
    private string _summary = "Noch nicht abgefragt.";

    /// <summary>Gibt es etwas anzuzeigen?</summary>
    public bool IsEmpty => Windows.Count == 0;

    /// <summary>Holt die sichtbaren Fenster neu.</summary>
    [RelayCommand]
    private void Refresh()
    {
        Windows.Clear();

        if (Host.Composition is not { } composition)
        {
            Summary = "Ohne Einrichtung steht die Fensterquelle nicht bereit. "
                + "Die Einrichtung öffnet das Zahnrad unter „Einstellungen“.";
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        try
        {
            IReadOnlyList<WindowInfo> windows = composition.Windows.GetVisibleWindows();
            IReadOnlyList<ProcessInfo> processes = composition.Processes.Snapshot();

            Dictionary<int, string> names = [];
            Dictionary<int, List<int>> children = [];

            foreach (ProcessInfo process in processes)
            {
                names[process.Id] = process.Name;

                if (!children.TryGetValue(process.ParentId, out List<int>? list))
                {
                    list = [];
                    children[process.ParentId] = list;
                }

                list.Add(process.Id);
            }

            foreach (WindowInfo window in windows
                .OrderBy(w => names.GetValueOrDefault(w.ProcessId, string.Empty), StringComparer.CurrentCulture)
                .ThenByDescending(w => !w.IsOwned))
            {
                string process = names.GetValueOrDefault(window.ProcessId, "(unbekannt)");
                Windows.Add(new WindowRow(window, process, Connections(composition, window, children)));
            }

            Summary = string.Create(CultureInfo.CurrentCulture,
                $"{windows.Count} sichtbare Fenster in "
                + $"{windows.Select(w => w.ProcessId).Distinct().Count()} Prozessen "
                + $"({processes.Count} Prozesse insgesamt).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Summary = "Die Abfrage ist fehlgeschlagen: " + Redaction.Scrub(ex.Message);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Die Gegenstellen der offenen Verbindungen eines Prozesses samt seiner Kinder.</summary>
    /// <remarks>
    /// Mit den Kindprozessen, weil etliche der überwachten Anwendungen die eigentliche
    /// Verbindung nicht selbst halten: Ein Browser oder ein Terminalprogramm startet dafür
    /// einen eigenen Prozess, und ohne dessen Verbindungen bliebe die Spalte leer.
    /// </remarks>
    private static string Connections(RuntimeComposition composition, WindowInfo window,
                                      Dictionary<int, List<int>> children)
    {
        try
        {
            IReadOnlyList<IPAddress> addresses = composition.TcpConnections.GetRemoteAddresses(
                window.ProcessId, children.GetValueOrDefault(window.ProcessId, []));

            return addresses.Count == 0
                ? "keine"
                : string.Join(", ", addresses.Select(a => a.ToString()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Verbindungen nicht lesbar: " + ex.Message);
            return "nicht lesbar";
        }
    }
}
