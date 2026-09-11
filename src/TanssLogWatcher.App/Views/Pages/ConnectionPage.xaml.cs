using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views.Pages;

/// <summary>
/// Die Verbindungsseite — und der Weg zum Einrichtungsassistenten.
/// </summary>
/// <remarks>
/// Zum Muster „Modell nur bei sichtbarer Seite“ siehe <see cref="SessionsPage"/>. Eigen ist
/// dieser Seite allein das Zahnrad: Das Ansichtsmodell darf kein Fenster öffnen — es kennt
/// keines und wäre damit nicht mehr ohne Oberfläche prüfbar. Es meldet den Wunsch, und hier
/// wird er ausgeführt.
/// </remarks>
[SuppressMessage("Design", "CA1001:Typen mit verwerfbaren Feldern sollten verwerfbar sein",
    Justification = "Eine Seite wird von der Navigation angelegt und abgelegt, nicht vom "
        + "Aufrufer; ein IDisposable daran riefe niemand. Freigegeben wird in Unloaded — "
        + "das ist der Zeitpunkt, zu dem die Navigation die Seite tatsächlich ablegt.")]
public partial class ConnectionPage : Page
{
    private ConnectionViewModel? _viewModel;

    /// <summary>Baut die Seite.</summary>
    public ConnectionPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (_viewModel is null)
            {
                _viewModel = new ConnectionViewModel(App.Runtime, App.Systems, App.Updates);
                _viewModel.SetupRequested += OnSetupRequested;
                _viewModel.ShutdownRequested += OnShutdownRequested;
                _viewModel.AiSettingsRequested += OnAiSettingsRequested;
            }

            DataContext = _viewModel;
        };

        Unloaded += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.SetupRequested -= OnSetupRequested;
                _viewModel.ShutdownRequested -= OnShutdownRequested;
                _viewModel.AiSettingsRequested -= OnAiSettingsRequested;
                _viewModel.Dispose();
                _viewModel = null;
            }

            DataContext = null;
        };
    }

    /// <summary>
    /// Das Setup läuft; die Anwendung beendet sich geordnet.
    /// </summary>
    /// <remarks>
    /// <c>Shutdown</c> und nicht <c>Close</c> am Fenster: Das Hauptfenster fängt das Schliessen
    /// ab und klappt nur zu. Der Weg über die Anwendung löst <c>OnExit</c> aus — und genau dort
    /// werden die laufenden Fernwartungen abgeschlossen und eingereiht, bevor der Prozess geht.
    /// </remarks>
    private void OnShutdownRequested(object? sender, EventArgs e) =>
        Application.Current.Shutdown();

    /// <summary>
    /// Öffnet die Einstellungen zur Sprachmodell-Unterstützung.
    /// </summary>
    /// <remarks>
    /// Als Dialog und nicht als Seite: Was dort bestätigt wird, ist eine Erklärung — sie
    /// verträgt kein beiläufiges Wegblättern mitten in der Eingabe.
    /// </remarks>
    private void OnAiSettingsRequested(object? sender, EventArgs e)
    {
        AiSettingsWindow settings = new(App.Runtime) { Owner = Window.GetWindow(this) };
        _ = settings.ShowDialog();
    }

    private void OnSetupRequested(object? sender, EventArgs e)
    {
        SetupWindow setup = new(App.Runtime) { Owner = Window.GetWindow(this) };

        // Der Assistent laedt die Laufzeit selbst neu, wenn er durchgelaufen ist. Die Seite
        // erfaehrt davon ueber den Betriebszustand und baut sich daraufhin neu auf - hier ist
        // nichts weiter zu tun, als das Fenster zu zeigen.
        _ = setup.ShowDialog();
    }
}
