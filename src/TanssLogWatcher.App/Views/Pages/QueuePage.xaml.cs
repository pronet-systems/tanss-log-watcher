using System.Windows.Controls;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views.Pages;

/// <summary>Die Warteschlangenseite.</summary>
/// <remarks>Zum Muster „Modell nur bei sichtbarer Seite“ siehe <see cref="SessionsPage"/>.</remarks>
public partial class QueuePage : Page
{
    private QueueViewModel? _viewModel;

    /// <summary>Baut die Seite.</summary>
    public QueuePage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _viewModel ??= new QueueViewModel(App.Runtime, App.Systems);
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
