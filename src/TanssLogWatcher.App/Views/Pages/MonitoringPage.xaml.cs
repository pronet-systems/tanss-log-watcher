using System.Windows.Controls;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views.Pages;

/// <summary>Die Überwachungsseite.</summary>
/// <remarks>Zum Muster „Modell nur bei sichtbarer Seite“ siehe <see cref="SessionsPage"/>.</remarks>
public partial class MonitoringPage : Page
{
    private MonitoringViewModel? _viewModel;

    /// <summary>Baut die Seite.</summary>
    public MonitoringPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _viewModel ??= new MonitoringViewModel(App.Runtime, App.Systems);
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
