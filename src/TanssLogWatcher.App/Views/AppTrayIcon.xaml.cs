using System.Runtime.Versioning;
using System.Windows;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views;

/// <summary>
/// Das Symbol im Infobereich — die einzige Anzeige, die im Normalbetrieb sichtbar ist.
/// </summary>
/// <remarks>
/// <para><b>Warum es der Anwendung gehört und keinem Fenster.</b> Das Werkzeug läuft im
/// Regelfall ohne offenes Fenster; beim Autostart wird nie eines gezeigt. Solange das Symbol in
/// <see cref="MainWindow"/> stand, entstand es erst mit dessen <c>Loaded</c>-Ereignis — das bei
/// einem nie gezeigten Fenster nicht fällt. Gemessen am laufenden Prozess: mit
/// <c>--minimized</c> trug er kein einziges Fenster der Klasse <c>H.NotifyIcon_*</c>, ohne
/// den Schalter schon. Das Werkzeug lief also unsichtbar und unerreichbar.</para>
///
/// <para><b>Die Bedienung geht ohne Fenster.</b> Alle drei Menüpunkte wenden sich an
/// <see cref="App"/> und nicht an ein Fenster: „Fenster öffnen“ erzeugt es, falls es noch
/// keines gibt, „Einrichten“ öffnet den Assistenten auch ohne Hauptfenster, und „Beenden“
/// beendet die Anwendung ausdrücklich — sie hängt nicht mehr am letzten offenen Fenster.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class AppTrayIcon
{
    /// <summary>Baut das Symbol. Bei Windows angemeldet wird es erst durch <c>ForceCreate</c>.</summary>
    /// <remarks>
    /// <b>Das Kontextmenü bekommt die Anzeige ausdrücklich mit.</b> Ein <c>ContextMenu</c> hängt
    /// in einem eigenen Popup und nicht im Baum darüber; dass es den Datenkontext trotzdem
    /// erbt, besorgt die Bibliothek von sich aus. Darauf soll die Bedienbarkeit dieses
    /// Werkzeugs aber nicht ruhen: Ohne Datenkontext bliebe „Überwachung angehalten“ ohne
    /// Wirkung und „Einrichten“ unsichtbar — und beides fiele niemandem auf, weil eine
    /// missglückte Bindung in WPF stillschweigend nichts tut.
    /// </remarks>
    /// <param name="model">Die Anzeige, die auch die Fußzeile des Hauptfensters trägt.</param>
    public AppTrayIcon(ShellViewModel model)
    {
        InitializeComponent();

        DataContext = model;

        if (ContextMenu is not null)
        {
            ContextMenu.DataContext = model;
        }
    }

    /// <summary>Holt das Hauptfenster nach vorn und erzeugt es, falls es noch keines gibt.</summary>
    /// <param name="sender">Das Symbol oder der Menüpunkt.</param>
    /// <param name="e">Das Ereignis.</param>
    private void OnOpenWindow(object sender, RoutedEventArgs e) => Running.ShowMainWindow();

    /// <summary>Öffnet den Einrichtungsassistenten.</summary>
    /// <param name="sender">Der Menüpunkt.</param>
    /// <param name="e">Das Ereignis.</param>
    private void OnSetup(object sender, RoutedEventArgs e) => Running.ShowSetup();

    /// <summary>Beendet die Anwendung.</summary>
    /// <param name="sender">Der Menüpunkt.</param>
    /// <param name="e">Das Ereignis.</param>
    private void OnExit(object sender, RoutedEventArgs e) => Running.RequestExit();

    /// <summary>Die laufende Anwendung — dort liegen Fenster, Assistent und das Ende.</summary>
    private static App Running => (App)Application.Current;
}
