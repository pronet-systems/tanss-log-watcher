using System.Runtime.Versioning;
using System.Windows;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views;

/// <summary>
/// Die Einstellungen zur Sprachmodell-Unterstützung.
/// </summary>
/// <remarks>
/// Der Schlüssel wird wie das Kennwort im Einrichtungsassistenten von Hand weitergereicht und
/// nicht gebunden: Eine gebundene Eigenschaft legte ihn als verwaltete Zeichenkette im Speicher
/// ab, wo er bis zur nächsten Speicherbereinigung liegen bliebe. Siehe <see cref="SetupWindow"/>.
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class AiSettingsWindow
{
    /// <summary>Baut die Maske für eine laufende Anwendung.</summary>
    /// <param name="host">Die Laufzeit.</param>
    public AiSettingsWindow(AppHost host)
    {
        InitializeComponent();

        ViewModel = new AiSettingsViewModel(host);
        DataContext = ViewModel;
    }

    /// <summary>Das Ansichtsmodell der Maske.</summary>
    public AiSettingsViewModel ViewModel { get; }

    private async void OnLoadModels(object sender, RoutedEventArgs e) =>
        await ViewModel.LoadModelsCommand.ExecuteAsync(KeyField.Password).ConfigureAwait(true);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveCommand.Execute(KeyField.Password);

        // Das Feld wird geleert, sobald der Wert abgelegt ist: Ein Schluessel, der im Fenster
        // stehen bleibt, waere beim naechsten Oeffnen fuer jeden sichtbar, der daneben steht.
        KeyField.Password = string.Empty;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
