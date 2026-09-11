using System.ComponentModel;
using System.Windows;
using TanssLogWatcher.App.ViewModels;
using TanssLogWatcher.App.Views.Pages;

namespace TanssLogWatcher.App.Views;

/// <summary>
/// Das Hauptfenster. Es ist die Schaltzentrale, nicht der Normalzustand — im Betrieb läuft das
/// Werkzeug mit geschlossenem Fenster und sichtbarem Symbol im Infobereich.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Unterscheidet „zuklappen“ von „beenden“.
    /// </summary>
    /// <remarks>
    /// Ohne diesen Merker würde <see cref="OnClosing"/> auch das gewollte Beenden abfangen und
    /// das Werkzeug ließe sich nur noch über den Task-Manager loswerden.
    /// </remarks>
    private bool _exitRequested;

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

        // Ohne diese Zeile zeigt die Navigationsleiste beim Start eine leere Fläche.
        Loaded += (_, _) =>
        {
            // Erst hier und nicht im Konstruktor: Die Laufzeit entsteht in App.OnStartup nach
            // dem Fenster. Ein Zugriff davor liefe in die Ausnahme, die AppHost dafuer vorsieht.
            ViewModel ??= new ShellViewModel(App.Runtime);
            DataContext = ViewModel;

            _ = Navigation.Navigate(page);
        };
    }

    /// <summary>Fußzeile und Hinweistext am Symbol im Infobereich.</summary>
    /// <remarks>
    /// Lebt so lange wie das Fenster und wird nicht beim Zuklappen abgemeldet: Der Hinweistext
    /// am Symbol ist im Normalbetrieb die einzige sichtbare Anzeige — gerade dann, wenn das
    /// Fenster zu ist.
    /// </remarks>
    public ShellViewModel? ViewModel { get; private set; }

    private static Type PageFor(string? name) => name?.ToLowerInvariant() switch
    {
        "queue" => typeof(QueuePage),
        "timers" => typeof(TimersPage),
        "monitoring" => typeof(MonitoringPage),
        "diagnostics" => typeof(DiagnosticsPage),
        "connection" => typeof(ConnectionPage),
        _ => typeof(SessionsPage),
    };

    /// <summary>
    /// Das Schließen des Fensters beendet die Überwachung <b>nicht</b>, es blendet sie aus.
    /// </summary>
    /// <remarks>
    /// Das ist bei einem Werkzeug im Infobereich üblich und entspricht dem Original. Damit
    /// daraus kein verdeckter Betrieb wird, bleibt das Symbol im Infobereich sichtbar und der
    /// Menüpunkt „Beenden“ steht direkt daneben.
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void OnTrayOpen(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        _exitRequested = true;
        Application.Current.Shutdown();
    }
}
