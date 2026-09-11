using System.Runtime.Versioning;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views;

/// <summary>
/// Der Dialog beim Sitzungsende.
/// </summary>
/// <remarks>
/// <para>Drei Ausgänge statt zwei. Die Vorlage kannte nur Speichern und Verwerfen — und
/// Verwerfen löschte die Sitzung endgültig und ohne Spur. „Später“ lässt sie statt dessen in
/// der Warteschlange stehen, wo sie sichtbar bleibt und nachträglich zu kommentieren ist.</para>
/// <para><b>Dieses Fenster erzeugt nichts.</b> Die Sitzung liegt schon in der Warteschlange,
/// bevor es aufgeht — siehe <see cref="SaveSessionViewModel"/>. Wegklicken kostet deshalb
/// nichts ausser dem Kommentar.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class SaveSessionWindow
{
    /// <summary>Baut den Dialog zu einer beendeten Sitzung.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="closed">Die beendete Sitzung samt dem, was mit ihr geschah.</param>
    /// <param name="typeName">Der sprechende Name des Fernwartungstyps, falls bekannt.</param>
    /// <param name="color">Die Farbe der Anbindung als Hexwert ohne Raute.</param>
    /// <param name="technician">Der Name des Technikers, falls bekannt.</param>
    public SaveSessionWindow(AppHost host, SessionClosed closed, string? typeName, string? color,
                             string? technician = null)
    {
        InitializeComponent();

        ViewModel = new SaveSessionViewModel(host, closed, typeName, color, technician);
        ViewModel.Finished += (_, _) => Close();
        DataContext = ViewModel;
    }

    /// <summary>Das Ansichtsmodell des Dialogs.</summary>
    public SaveSessionViewModel ViewModel { get; }
}
