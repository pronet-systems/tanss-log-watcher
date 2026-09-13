using System.Runtime.Versioning;
using System.Windows.Controls;
using TanssLogWatcher.App.ViewModels;

namespace TanssLogWatcher.App.Views;

/// <summary>
/// Die Firmenauswahl als einsetzbares Stück Oberfläche.
/// </summary>
/// <remarks>
/// <para><b>Ein Steuerelement und kein Fenster</b>, weil die Firma an mehreren Stellen gebraucht
/// wird — beim Anlegen eines Tickets ebenso wie beim Anlegen einer Leistung. Zwei Abschriften
/// derselben Auswahl liefen auseinander, und die Abweichung fiele erst auf, wenn eine von beiden
/// die Unwahrheit „keine Firma gefunden“ wieder einführt.</para>
///
/// <para><b>Hier steht mit Absicht nichts weiter drin.</b> Der gesamte Zustand — Suchbegriff,
/// Entprellung, Treffer, Auswahl, Meldung — gehört <see cref="CompanyPickerViewModel"/>. Was in
/// einer <c>xaml.cs</c> läge, wäre ohne Oberfläche nicht zu prüfen, und eine Zusage, die kein
/// Test hält, ist eine Zusage auf Zuruf.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class CompanyPicker : UserControl
{
    /// <summary>Baut das Steuerelement. Der <c>DataContext</c> kommt vom Einsatzort.</summary>
    /// <remarks>
    /// Erwartet wird ein <see cref="CompanyPickerViewModel"/>. Es wird hier <b>nicht</b> selbst
    /// gebaut: Woher die Firmensuche kommt und ob überhaupt eine Verbindung eingerichtet ist,
    /// weiß der Einsatzort und nicht dieses Stück.
    /// </remarks>
    public CompanyPicker()
    {
        InitializeComponent();
    }

    /// <summary>Das Ansichtsmodell, sofern eines gesetzt ist.</summary>
    /// <remarks>
    /// Bequemlichkeit für den Einsatzort, der nach der Wahl an
    /// <see cref="CompanyPickerViewModel.SelectedCompany"/> herankommen muss.
    /// </remarks>
    public CompanyPickerViewModel? ViewModel => DataContext as CompanyPickerViewModel;
}
