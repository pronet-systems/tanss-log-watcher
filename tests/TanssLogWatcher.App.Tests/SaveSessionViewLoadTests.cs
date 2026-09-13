using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.Views;
using Xunit;

using WpfSize = System.Windows.Size;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Der Abschlussdialog wird wirklich geladen — in beiden Zuständen.
/// </summary>
/// <remarks>
/// <para><b>Warum dieser Fall da ist.</b> Ein fehlender <c>StaticResource</c>, ein
/// <c>BasedOn</c> auf einen <c>ui:</c>-Typ oder ein Zeichen, das WPF-UI 4.3.0 gar nicht kennt,
/// werfen erst beim <b>Laden</b> der Vorlage, nicht beim Übersetzen. Beides ist in diesem
/// Werkzeug schon vorgekommen: <c>Ticket24</c> gibt es nicht und warf beim Öffnen des Fensters,
/// und ein <c>BasedOn</c> liess die Navigation still scheitern. Ein grüner Bau beweist über
/// eine XAML-Datei also nichts — und ausgerechnet dieses Fenster darf nicht ausbleiben: Geht es
/// nicht auf, wird die Sitzung nicht gebucht, und niemandem fällt es auf.</para>
///
/// <para>Geprüft werden beide Zustände der Fussleiste: vor dem Buchen (Verwerfen / Später / In
/// TANSS buchen) und danach (nur noch Schliessen). Der zweite ist neu, und seine Bedienelemente
/// entstehen erst, wenn er eintritt.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[Collection(Ladefaelle.Name)]
public sealed class SaveSessionViewLoadTests(UiThreadFixture oberflaeche)
{
    private readonly UiThreadFixture _oberflaeche = oberflaeche;

    [Fact]
    public void Der_Abschlussdialog_laedt_in_beiden_Zustaenden() => _oberflaeche.Run(() =>
    {
        AppHost host = new(loggers: NullLoggerFactory.Instance,
                           notifier: new RuntimeNotifier(null));

        SessionClosed geschlossen = new(
            Sample.Session(window: 0) with { EndedAt = DateTimeOffset.Now },
            SessionDisposition.Enqueued, "Eingereiht.");

        SaveSessionWindow fenster = new(host, geschlossen, "Fernwartung", color: null);

        // Gemessen wird der Inhalt und nicht das Fenster selbst: Ein Fenster misst sich ueber
        // seinen Rahmen, und den gibt es erst beim Anzeigen.
        FrameworkElement inhalt = Assert.IsAssignableFrom<FrameworkElement>(fenster.Content);
        inhalt.Measure(new WpfSize(620, 700));
        inhalt.Arrange(new Rect(0, 0, 620, 700));
        inhalt.UpdateLayout();

        // Der zweite Zustand: Nach dem Buchen verschwinden die drei Ausgaenge, und "Schliessen"
        // kommt hinzu. Dessen Vorlage entsteht erst jetzt - vorher war sie Collapsed.
        fenster.ViewModel.IsBooked = true;
        inhalt.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // Die Obergrenze haengt am Bildschirm und wird im Code dahinter gesetzt. Sie ist der
        // Grund, warum die Fussleiste nicht mehr unter der Taskleiste liegt; stuende hier 0
        // oder Unendlich, waere genau das wieder moeglich.
        Assert.True(fenster.MaxHeight > 0);
        Assert.True(fenster.MaxHeight <= SystemParameters.WorkArea.Height);

        fenster.Close();
        host.Dispose();
    });
}
