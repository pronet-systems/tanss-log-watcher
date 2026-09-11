using System.Windows.Controls;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views.Pages;

/// <summary>Die Timerseite.</summary>
/// <remarks>Zum Muster „Modell nur bei sichtbarer Seite“ siehe <see cref="SessionsPage"/>.</remarks>
public partial class TimersPage : Page
{
    private TimersViewModel? _viewModel;

    /// <summary>Baut die Seite.</summary>
    public TimersPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _viewModel ??= new TimersViewModel(App.Runtime);
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
