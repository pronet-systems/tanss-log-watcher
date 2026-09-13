using System.Windows.Controls;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views.Pages;

/// <summary>Die Sitzungsseite.</summary>
/// <remarks>
/// <para><b>Das Ansichtsmodell lebt nur, solange die Seite sichtbar ist.</b> Die Navigation
/// legt Seiten an und wieder ab; ein Modell, das im Konstruktor entstünde und nie abgemeldet
/// würde, hinge nach zwanzig Wechseln zwanzigmal an den Ereignissen der Laufzeit — und jedes
/// einzelne liefe bei jedem Takt der Beobachtung mit.</para>
/// <para>Dasselbe Muster steht auf jeder Seite. Es ist bewusst wiederholt und nicht in eine
/// Basisklasse gezogen: Eine eigene Seitenbasisklasse zwischen <see cref="Page"/> und den
/// Seiten verträgt sich nicht mit dem, was der XAML-Übersetzer für <c>x:Class</c> erzeugt,
/// und der Gewinn wären je Seite vier gesparte Zeilen.</para>
/// </remarks>
public partial class SessionsPage : Page
{
    private SessionsViewModel? _viewModel;

    /// <summary>Baut die Seite.</summary>
    public SessionsPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _viewModel ??= new SessionsViewModel(App.Runtime, App.Systems);
            DataContext = _viewModel;
        };

        Unloaded += (_, _) =>
        {
            _viewModel?.Dispose();
            _viewModel = null;
            DataContext = null;
        };
    }
}
