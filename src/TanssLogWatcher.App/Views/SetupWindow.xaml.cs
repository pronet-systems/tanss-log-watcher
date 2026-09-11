using System.Runtime.Versioning;
using System.Windows;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views;

/// <summary>
/// Der Einrichtungsassistent als Fenster.
/// </summary>
/// <remarks>
/// <para><b>Das Kennwort wird von Hand weitergereicht und nicht gebunden.</b>
/// <see cref="System.Windows.Controls.PasswordBox"/> bietet aus gutem Grund keine Bindung an:
/// Eine gebundene Eigenschaft legte das Kennwort als verwaltete Zeichenkette im Speicher ab,
/// wo es bis zur nächsten Speicherbereinigung liegen bliebe und in jeden Absturzabzug geriete.
/// Hier geht es genau einmal in den Aufruf und ist danach nicht mehr erreichbar.</para>
/// <para><b>Warum hier Ereignisbehandlung und kein Befehl.</b> Genau wegen des Kennworts: Ein
/// Befehl bekäme seinen Parameter aus einer Bindung — also aus der Eigenschaft, die es nicht
/// geben soll.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class SetupWindow
{
    /// <summary>Baut den Assistenten für eine laufende Anwendung.</summary>
    /// <param name="host">Die Laufzeit; sie wird am Ende neu geladen.</param>
    public SetupWindow(AppHost host)
    {
        InitializeComponent();

        ViewModel = new SetupViewModel(host);
        DataContext = ViewModel;
    }

    /// <summary>Das Ansichtsmodell des Assistenten.</summary>
    public SetupViewModel ViewModel { get; }

    private async void OnLogin(object sender, RoutedEventArgs e) =>
        await ViewModel.LoginCommand.ExecuteAsync(PasswordField.Password).ConfigureAwait(true);

    private void OnBack(object sender, RoutedEventArgs e) => ViewModel.Step = SetupStep.Login;

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        // Der Befehl selbst haengt am Button; hier wird nur noch geschlossen, wenn er
        // durchgelaufen ist. Ein Fenster, das sich trotz Fehlermeldung schliesst, verschluckt
        // genau die Meldung, die der Benutzer braucht.
        if (ViewModel.IsFinished)
        {
            DialogResult = true;
            Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
