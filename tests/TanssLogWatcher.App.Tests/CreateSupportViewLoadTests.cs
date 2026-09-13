using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.ViewModels;
using TanssLogWatcher.App.Views;
using Xunit;

using WpfSize = System.Windows.Size;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Der Leistungsdialog wird wirklich geladen — samt dem neu eingesetzten Anlegebereich.
/// </summary>
/// <remarks>
/// <para><b>Warum dieser Fall da ist.</b> Ein fehlender <c>StaticResource</c> und ein
/// <c>BasedOn</c> auf einen <c>ui:</c>-Typ werfen erst beim <b>Laden</b> der Vorlage, nicht beim
/// Übersetzen — in diesem Werkzeug hat genau das die Navigation schon einmal <b>still</b>
/// scheitern lassen. Ein grüner Bau beweist über eine XAML-Datei also nichts. Der Dialog trägt
/// seit dieser Stufe eine eingesetzte Firmenauswahl, einen zweiten Umsetzer und einen zweiten
/// Rollbereich; ohne diesen Fall fiele ein Fehler darin erst dem Techniker auf.</para>
///
/// <para><b>Der Anlegebereich wird ausdrücklich aufgeklappt</b>, bevor gemessen wird: Ein
/// zugeklappter Bereich ist <c>Collapsed</c>, und WPF baut dessen Inhalt gar nicht erst. Genau
/// der soll hier aber entstehen.</para>
///
/// <para>Nachgewiesen ist die Wirksamkeit dieses Falles: Mit einem absichtlich falschen
/// Ressourcennamen in der Vorlage fällt er um.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[Collection(Ladefaelle.Name)]
public sealed class CreateSupportViewLoadTests(UiThreadFixture oberflaeche)
{
    private readonly UiThreadFixture _oberflaeche = oberflaeche;

    [Fact]
    public void Der_Leistungsdialog_laedt_samt_Anlegebereich() => _oberflaeche.Run(() =>
    {
        AppHost host = new(loggers: NullLoggerFactory.Instance,
                           notifier: new RuntimeNotifier(null));

        TimerRow timer = new(
            new TanssTimer { Id = 42, Title = "Fernwartung", Duration = 600 },
            new DateTimeOffset(2026, 9, 13, 14, 0, 0, TimeSpan.Zero));

        CreateSupportWindow fenster = new(host, timer,
            [new TicketRow(new Ticket { Id = 4711, Title = "Serverstörung" })]);

        // Ohne diesen Griff bliebe der Anlegebereich Collapsed - und die Vorlagen darin
        // entstuenden nie. Der Fall pruefte dann genau das nicht, wofuer es ihn gibt.
        fenster.ViewModel.NewTicket.Toggle();
        Assert.True(fenster.ViewModel.NewTicket.IsOpen);

        // Erst Messen und Anordnen baut die Vorlagen. Gemessen wird der Inhalt und nicht das
        // Fenster selbst: Ein Fenster misst sich ueber seinen Rahmen, und den gibt es erst beim
        // Anzeigen.
        FrameworkElement inhalt = Assert.IsAssignableFrom<FrameworkElement>(fenster.Content);
        inhalt.Measure(new WpfSize(700, 760));
        inhalt.Arrange(new Rect(0, 0, 700, 760));
        inhalt.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        fenster.Close();
        host.Dispose();
    });
}
