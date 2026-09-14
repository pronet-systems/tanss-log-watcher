using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using TanssLogWatcher.App.ViewModels;
using TanssLogWatcher.App.Views.Pages;

namespace TanssLogWatcher.App.Views;

/// <summary>
/// Das Hauptfenster. Es ist die Schaltzentrale, nicht der Normalzustand — im Betrieb läuft das
/// Werkzeug mit geschlossenem Fenster und sichtbarem Symbol im Infobereich.
/// </summary>
public partial class MainWindow
{
    public MainWindow() : this(null)
    {
    }

    /// <param name="startPage">
    /// Kurzname der Startseite aus dem Startargument <c>--page</c>, oder
    /// <see langword="null"/> für die Sitzungen. Ein unbekannter Name fällt still auf die
    /// Sitzungen zurück — der Schalter dient dem Nachsehen, nicht dem Betrieb.
    /// </param>
    public MainWindow(string? startPage)
    {
        InitializeComponent();

        Type page = PageFor(startPage);

        // Jetzt im Konstruktor und nicht mehr im Loaded-Ereignis: Die Anzeige gehört seit dem
        // Umbau der Anwendung (App.Shell) und wird dort vor jedem Fenster gebaut. Am
        // Loaded-Ereignis war sie falsch aufgehoben - beim Autostart fällt es nie, und damit
        // fehlte auch der Hinweistext am Symbol im Infobereich, der an derselben Anzeige hängt.
        ViewModel = App.Shell;
        DataContext = ViewModel;

        // Die Navigation braucht dagegen wirklich das Loaded-Ereignis: Vorher gibt es die
        // Einträge nicht, die sie auflösen müsste. Ohne diese Zeile zeigt die
        // Navigationsleiste beim Start eine leere Fläche.
        Loaded += (_, _) => ShowStartPage(page);
    }

    /// <summary>
    /// Öffnet die Startseite — und gibt nicht auf, wenn der erste Versuch zu früh kommt.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Rückgabewert war das Problem.</b> Hier stand <c>_ = Navigation.Navigate(page)</c>.
    /// Die Navigationsleiste löst das Ziel über ihre Einträge auf; sind die beim Auslösen von
    /// <see cref="FrameworkElement.Loaded"/> noch nicht aufgebaut, findet sie nichts, meldet
    /// <see langword="false"/> — und wählt anschliessend von sich aus den letzten Eintrag.
    /// Das Werkzeug startete dann auf dem letzten Eintrag statt auf den Sitzungen, und weil der
    /// Rückgabewert weggeworfen wurde, sah es aus wie eine Design-Entscheidung.</para>
    ///
    /// <para><b>Warum es lange gutging.</b> Ob die Einträge rechtzeitig stehen, hängt davon ab,
    /// wie schnell der Rechner das erste Layout durchrechnet — ein Wettlauf, den dieselbe
    /// Version mal gewinnt und mal verliert. Genau deshalb gehört der Rückgabewert geprüft und
    /// nicht verworfen.</para>
    ///
    /// <para>Zwei Nachfassversuche in aufsteigender Trägheit; danach steht die Anwendung auf
    /// der Seite, die die Navigationsleiste selbst gewählt hat. Das ist kein Beinbruch — aber
    /// es bleibt eine Meldung im Protokoll, statt stillschweigend hingenommen zu werden.</para>
    /// </remarks>
    /// <param name="page">Die Seite, die zuerst zu zeigen ist.</param>
    private void ShowStartPage(Type page)
    {
        if (Navigation.Navigate(page))
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Navigation.Navigate(page))
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (!Navigation.Navigate(page))
                {
                    Debug.WriteLine(
                        "Die Startseite liess sich nicht öffnen: " + page.Name
                        + ". Die Navigationsleiste hat statt dessen selbst gewählt.");
                }
            });
        });
    }

    /// <summary>Die Fußzeile — dieselbe Anzeige, die auch das Symbol im Infobereich trägt.</summary>
    /// <remarks>
    /// <b>Geliehen, nicht besessen.</b> Das Stück gehört der Anwendung (<see cref="App.Shell"/>)
    /// und lebt länger als dieses Fenster: Der Hinweistext am Symbol hängt daran und ist im
    /// Normalbetrieb die einzige sichtbare Anzeige — gerade dann, wenn nie ein Fenster offen
    /// war. Deshalb wird es hier auch nicht freigegeben; das tut <see cref="App.Dispose"/>.
    /// </remarks>
    public ShellViewModel ViewModel { get; }

    private static Type PageFor(string? name) => name?.ToLowerInvariant() switch
    {
        "queue" => typeof(QueuePage),
        "timers" => typeof(TimersPage),
        "history" => typeof(HistoryPage),
        "monitoring" => typeof(MonitoringPage),
        "recording" => typeof(RecordingPage),
        "diagnostics" => typeof(DiagnosticsPage),
        "connection" => typeof(ConnectionPage),
        "about" => typeof(AboutPage),
        _ => typeof(SessionsPage),
    };

    /// <summary>
    /// Das Schließen des Fensters beendet die Überwachung <b>nicht</b>, es blendet sie aus.
    /// </summary>
    /// <remarks>
    /// <para>Das ist bei einem Werkzeug im Infobereich üblich und hier deshalb ebenso gehalten.
    /// Damit daraus kein verdeckter Betrieb wird, bleibt das Symbol im Infobereich sichtbar und
    /// der Menüpunkt „Beenden“ steht direkt daneben.</para>
    /// <para><b>Der Merker steht in <see cref="App"/> und nicht mehr hier.</b> Er unterschied
    /// früher „zuklappen“ von „beenden“ und wurde vom Menüpunkt am Symbol gesetzt — der aber
    /// gehört seit dem Umbau nicht mehr diesem Fenster. Ein Beenden ohne offenes Fenster hätte
    /// ihn nie erreicht.</para>
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>Führt vom Hinweis auf die neue Version dorthin, wo sie sich holen lässt.</summary>
    /// <remarks>
    /// Die Seite „Verbindung“ trägt den ganzen Vorgang: holen, gegen die Prüfsumme halten,
    /// einspielen. Ein Hinweis, der nur hinweist, zwänge den Techniker, den Weg selbst zu
    /// suchen.
    /// </remarks>
    /// <param name="sender">Die Schaltfläche in der Fußzeile.</param>
    /// <param name="e">Das Ereignis.</param>
    private void OnUpdateHintClick(object sender, RoutedEventArgs e)
    {
        if (!Navigation.Navigate(typeof(ConnectionPage)))
        {
            Debug.WriteLine("Die Seite „Verbindung“ liess sich vom Aktualisierungshinweis aus "
                            + "nicht öffnen.");
        }
    }

}
