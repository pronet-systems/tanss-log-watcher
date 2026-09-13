using System.Windows.Controls;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views.Pages;

/// <summary>
/// Die Seite „Über“: Entwicklung, Version und die Pfade, nach denen im Störungsfall gefragt
/// wird.
/// </summary>
/// <remarks>
/// <para><b>Ohne Zugang zur Laufzeit, und das mit Absicht.</b> Alles, was hier steht, kommt aus
/// dem Erzeugnis selbst. Die Seite trägt deshalb auch dann, wenn nichts eingerichtet ist — und
/// gerade dann wird sie gebraucht: Wer anruft, weil etwas nicht geht, wird als Erstes nach der
/// Version gefragt.</para>
///
/// <para>Zum Muster „Modell nur bei sichtbarer Seite“ siehe <see cref="SessionsPage"/>. Dieses
/// Modell hängt an keinem Ereignis der Laufzeit und wäre auch einmalig im Konstruktor gut
/// aufgehoben; das Muster steht hier trotzdem, damit die Seiten sich gleich verhalten und
/// niemand beim Lesen nach dem Grund für die Ausnahme sucht. Es kostet vier
/// Attributabfragen je Aufruf.</para>
/// </remarks>
public partial class AboutPage : Page
{
    private AboutViewModel? _viewModel;

    /// <summary>Baut die Seite.</summary>
    public AboutPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _viewModel ??= new AboutViewModel();
            DataContext = _viewModel;
        };

        Unloaded += (_, _) =>
        {
            // Kein Dispose: Dieses Modell haelt nichts, was freizugeben waere.
            _viewModel = null;
            DataContext = null;
        };
    }
}
