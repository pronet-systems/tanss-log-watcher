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

    /// <summary>Speichert und schliesst — aber nur, wenn das Speichern geglückt ist.</summary>
    /// <remarks>
    /// <para><b>Die Bedingung ist der ganze Punkt.</b> Das Speichern kann aus vier Gründen
    /// scheitern: fehlende Einwilligung, kein Modell gewählt, kein Schlüssel, oder die Datei
    /// liess sich nicht schreiben. Jeder dieser Gründe steht danach im Fenster, teils mit einem
    /// roten Rahmen am betroffenen Feld. Ein Fenster, das sich trotzdem schliesst, nähme den
    /// Grund mit — und der Techniker stünde vor einer Unterstützung, die nicht läuft, ohne zu
    /// wissen warum.</para>
    /// <para><c>IsProblem</c> trägt diese Unterscheidung; <c>Fail</c> im Ansichtsmodell setzt
    /// es an jedem Fehlerausgang.</para>
    /// </remarks>
    /// <param name="sender">Die Schaltfläche.</param>
    /// <param name="e">Das Ereignis.</param>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveCommand.Execute(KeyField.Password);

        // Das Feld wird geleert, sobald der Wert abgelegt ist: Ein Schluessel, der im Fenster
        // stehen bleibt, waere beim naechsten Oeffnen fuer jeden sichtbar, der daneben steht.
        KeyField.Password = string.Empty;

        if (!ViewModel.IsProblem)
        {
            Close();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
