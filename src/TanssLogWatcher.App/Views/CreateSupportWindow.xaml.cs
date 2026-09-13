using System.Runtime.Versioning;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views;

/// <summary>
/// Der Dialog, der aus einem Timer eine Leistung macht.
/// </summary>
/// <remarks>
/// <para>Er geht auf Aufforderung auf, nicht von selbst: Ein Timer ist erfasste Zeit, eine
/// Leistung ist eine Buchung. Wann aus dem einen das andere wird, entscheidet der Techniker.</para>
/// <para><b>Modal, anders als der Abschlussdialog.</b> Dort ist das Fenster oft zu und die
/// Sitzung längst eingereiht; hier steht der Techniker davor und hat den Timer eben selbst
/// angeklickt. Ein zweiter Dialog daneben brächte nur die Frage, welcher zu welchem Timer
/// gehört.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class CreateSupportWindow
{
    /// <summary>Baut den Dialog zu einem Timer.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="timer">Der Timer, der zur Leistung werden soll.</param>
    /// <param name="tickets">Die offenen Tickets zur Auswahl.</param>
    public CreateSupportWindow(AppHost host, TimerRow timer, IReadOnlyList<TicketRow> tickets)
    {
        InitializeComponent();

        ViewModel = new CreateSupportViewModel(host, timer, tickets);
        ViewModel.Finished += (_, _) => Close();
        DataContext = ViewModel;

        // Der Anleger fuehrt eine Firmenauswahl mit Entprellungstakt. Ohne diese Zeile liefe
        // der Takt nach dem Schliessen weiter und hielte den Dialog im Speicher.
        Closed += (_, _) => ViewModel.Dispose();
    }

    /// <summary>Das Ansichtsmodell des Dialogs.</summary>
    public CreateSupportViewModel ViewModel { get; }
}
