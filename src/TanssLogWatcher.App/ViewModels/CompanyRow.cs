using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Eine Firma, wie eine Zeile der Firmenauswahl sie zeigt.
/// </summary>
/// <remarks>
/// <para><b>Warum die Zeile mehr als den Namen trägt.</b> Nachgemessen gegen eine Instanz der
/// Fassung 10.10.0 stand derselbe Firmenname <b>fünfmal</b> mit verschiedenen Kennungen in einer
/// einzigen Trefferliste. Eine Auswahl, die nur <see cref="Name"/> zeigt, zwingt den Techniker
/// zum Raten — und eine auf die falsche Firma gebuchte Leistung fällt niemandem auf. Deshalb
/// stehen Kundennummer und Ort gleichberechtigt daneben, und zwar auch dann, wenn sie
/// <b>fehlen</b>: „ohne Kundennummer“ ist eine Auskunft, ein leeres Feld ist keine.</para>
///
/// <para><b>Fest bis auf einen einzigen Wert.</b> Alles, was aus TANSS kommt, steht mit der Zeile
/// fest; eine neue Suche liefert neue Zeilen. Veränderlich ist allein <see cref="IsChosen"/> —
/// die Marke „diese hier ist gewählt“. Sie gehört der Zeile, weil die Liste sonst für jede
/// Auswahl neu gebaut werden müsste und dabei Bildlauf und Tastaturfokus verlöre.</para>
/// </remarks>
public sealed partial class CompanyRow : ObservableObject
{
    /// <summary>Übernimmt eine Firma aus der Suche.</summary>
    /// <param name="company">Die Firma, wie TANSS sie geliefert hat.</param>
    public CompanyRow(Company company)
    {
        ArgumentNullException.ThrowIfNull(company);

        Company = company;

        // Fehlt eine Angabe, steht der Hinweis darauf da und nicht nichts: Hausregel 2 in
        // klein - "nicht hinterlegt" ist gemessen, ein leeres Feld ist mehrdeutig (fehlt der
        // Wert, oder hat ihn die Anzeige verschluckt?).
        CustomerNumber = string.IsNullOrWhiteSpace(company.DisplayId)
            ? "ohne Kundennummer"
            : company.DisplayId;

        string place = string.Join(
            " ",
            new[] { company.PostCode, company.City }.Where(part => !string.IsNullOrWhiteSpace(part)));
        Place = place.Length == 0 ? "ohne Ort" : place;

        HasCustomerNumber = !string.IsNullOrWhiteSpace(company.DisplayId);
        HasPlace = place.Length > 0;
    }

    /// <summary>Die Firma, so wie sie aus TANSS kam.</summary>
    /// <remarks>
    /// Der Aufrufer, der die Auswahl weiterreicht, braucht das ganze Stück und nicht die
    /// Anzeigetexte — <c>companyId</c> für die Buchung steht in <see cref="Company.Id"/>.
    /// </remarks>
    public Company Company { get; }

    /// <summary>Die TANSS-Kennung der Firma; das ist die <c>companyId</c>.</summary>
    public int Id => Company.Id;

    /// <summary>Der Firmenname. Allein <b>nicht</b> eindeutig.</summary>
    public string Name => Company.Name;

    /// <summary>Die Kundennummer, oder „ohne Kundennummer“, wenn keine hinterlegt ist.</summary>
    public string CustomerNumber { get; }

    /// <summary>Postleitzahl und Ort, oder „ohne Ort“, wenn beides fehlt.</summary>
    public string Place { get; }

    /// <summary>Trägt die Firma eine Kundennummer?</summary>
    public bool HasCustomerNumber { get; }

    /// <summary>Trägt die Firma eine Ortsangabe?</summary>
    public bool HasPlace { get; }

    /// <summary>
    /// Darf auf diese Firma gebucht werden?
    /// </summary>
    /// <remarks>
    /// Gesperrt heißt nein — die Beschreibung sagt dazu wörtlich „no service may be entered“.
    /// Inaktiv heißt ja; inaktive Firmen kommen in der Suche mit (gemessen: 5 von 11 Treffern)
    /// und dürfen bebucht werden.
    /// </remarks>
    public bool IsSelectable => Company.Selectable;

    /// <summary>Ist die Firma inaktiv gesetzt?</summary>
    public bool IsInactive => Company.Inactive;

    /// <summary>Ist die Firma gesperrt?</summary>
    public bool IsLocked => Company.Lockout;

    /// <summary>Ist die „Firma“ in Wahrheit ein Privatkunde?</summary>
    public bool IsPersonalCustomer => Company.PersonalCustomer;

    /// <summary>
    /// Ist diese Zeile die gewählte?
    /// </summary>
    /// <remarks>
    /// Gesetzt wird das ausschließlich von <see cref="CompanyPickerViewModel"/> — die Zeile
    /// wählt sich nicht selbst. Zwei Stellen, die dieselbe Marke setzen dürfen, ergeben
    /// irgendwann zwei gewählte Zeilen.
    /// </remarks>
    [ObservableProperty]
    private bool _isChosen;

    /// <summary>
    /// Die Marke neben dem Namen — „gesperrt“, „inaktiv“ — oder <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Kurz, weil sie in eine Pille neben den Namen passen muss; der ganze Satz steht in
    /// <see cref="StatusNote"/>.
    /// </remarks>
    public string? StatusTag => IsLocked ? "gesperrt" : IsInactive ? "inaktiv" : null;

    /// <summary>Der ganze Hinweis zum Zustand, oder <see langword="null"/>, wenn alles in Ordnung ist.</summary>
    public string? StatusNote => Company.StatusNote;

    /// <summary>Steht ein Zustandshinweis an dieser Zeile?</summary>
    public bool HasStatusNote => StatusNote is not null;

    /// <summary>
    /// Der Text für die Kurzinfo: alles, was diese Firma von einer gleichnamigen unterscheidet.
    /// </summary>
    public string Details
    {
        get
        {
            string text = string.Create(CultureInfo.CurrentCulture,
                $"{Name}\n{CustomerNumber} · {Place}\nTANSS-Kennung {Id}");

            return StatusNote is null ? text : text + "\n" + StatusNote;
        }
    }

    /// <summary>Name samt Unterscheidungsmerkmal — für Protokoll und Fehlersuche.</summary>
    public override string ToString() => Company.ToString();
}
