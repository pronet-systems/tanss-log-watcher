using System.Windows.Controls;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views.Pages;

/// <summary>Die Diagnoseseite.</summary>
/// <remarks>Zum Muster „Modell nur bei sichtbarer Seite“ siehe <see cref="SessionsPage"/>.</remarks>
public partial class DiagnosticsPage : Page
{
    private DiagnosticsViewModel? _viewModel;

    /// <summary>Baut die Seite.</summary>
    public DiagnosticsPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _viewModel ??= new DiagnosticsViewModel(App.Runtime);
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
