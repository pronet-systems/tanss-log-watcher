using System.Runtime.Versioning;
using System.Windows;
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
/// <para><b>Dieses Fenster hält die Sitzung.</b> Beim Sitzungsende entsteht seit dem Umbau
/// KEINE Warteschlangenzeile mehr — die Warteschlange ist der Fehlerpfad und nicht mehr der
/// Regelweg. „In TANSS buchen“ sendet unmittelbar; eingereiht wird nur, wenn das Senden
/// misslingt oder niemand antwortet (siehe <see cref="SaveSessionViewModel"/>).</para>
/// <para><b>Wegklicken kostet deshalb mehr als früher, aber nichts Verlorenes:</b> Wer das
/// Fenster ohne Antwort schliesst, dessen Sitzung wird wartend eingereiht und beim nächsten
/// Start erneut vorgelegt. Verloren geht nur der getippte Berichtstext.</para>
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

        // Gemessen am Arbeitsplatz des Technikers: Das Fenster ragte 32 Bildpunkte unter den
        // Bildschirmrand, und seine Fussleiste lag im Streifen der Taskleiste - ein Klick auf
        // die Taskleiste traf damit "In TANSS buchen". Die Arbeitsflaeche haengt vom Bildschirm
        // und von der Skalierung ab und laesst sich deshalb nicht in XAML hinschreiben; hier
        // ist sie abzufragen. Zusammen mit SizeToContent="Height" heisst das: so hoch wie
        // noetig, aber nie hoeher als der Platz ueber der Taskleiste.
        MaxHeight = SystemParameters.WorkArea.Height;

        ViewModel = new SaveSessionViewModel(host, closed, typeName, color, technician);
        ViewModel.Finished += (_, _) => Close();
        DataContext = ViewModel;

        // Dieses Fenster fragt TANSS beim Oeffnen nichts mehr. Hier standen einmal drei
        // Abfragen - Firma, Ticket, Geraet -, die beim Laden anliefen; sie sind mit dem
        // Zuordnungsblock entfallen. Das Fenster ist damit ohne Netz vollstaendig.
        //
        // Dispose bleibt: Ohne Antwort reiht es die Sitzung ein, damit sie nicht verlorengeht.
        Closed += (_, _) => ViewModel.Dispose();
    }

    /// <summary>Das Ansichtsmodell des Dialogs.</summary>
    public SaveSessionViewModel ViewModel { get; }
}
