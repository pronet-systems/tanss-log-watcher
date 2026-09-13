using System.Windows.Controls;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views.Pages;

/// <summary>Die Seite zur Bildschirmaufzeichnung.</summary>
/// <remarks>Zum Muster „Modell nur bei sichtbarer Seite“ siehe <see cref="SessionsPage"/>.</remarks>
public partial class RecordingPage : Page
{
    private RecordingViewModel? _viewModel;

    /// <summary>Baut die Seite.</summary>
    public RecordingPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _viewModel ??= new RecordingViewModel(App.Runtime);
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
