using System.Windows.Controls;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views.Pages;

/// <summary>Die Verlaufsseite.</summary>
/// <remarks>
/// Zum Muster „Modell nur bei sichtbarer Seite“ — und warum es auf jeder Seite wiederholt und
/// nicht in eine Basisklasse gezogen ist — siehe <see cref="SessionsPage"/>.
/// </remarks>
public partial class HistoryPage : Page
{
    private HistoryViewModel? _viewModel;

    /// <summary>Baut die Seite.</summary>
    public HistoryPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _viewModel ??= new HistoryViewModel(App.Runtime, App.Systems);
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
