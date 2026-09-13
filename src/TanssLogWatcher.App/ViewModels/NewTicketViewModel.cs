using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Api.Repository;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Ein Ticket anlegen, ohne den Leistungsdialog zu verlassen.
/// </summary>
/// <remarks>
/// <para><b>Warum hier und nicht als eigenes Fenster.</b> Der Fall, für den es diesen Weg gibt,
/// ist genau einer: Der Techniker hat gearbeitet, will die Zeit buchen — und die Prüfung unter
/// dem Ticketfeld sagt, dass es die eingetippte Nummer nicht gibt, oder es gab von vornherein
/// keine. Bisher endete das hier; das Ticket war in TANSS von Hand anzulegen, die Nummer von
/// dort abzuschreiben und der Dialog neu zu öffnen. Der Anlegeweg
/// (<see cref="ITicketCreation"/>) war gebaut und hatte keinen einzigen Aufrufer.</para>
///
/// <para><b>Ein zweiter Versuch legt ein zweites Ticket an.</b> Ein Ticket trägt keine eigene
/// Kennung, an der sich eine Dublette erkennen liesse — deshalb schaltet <see cref="IsDone"/>
/// nach dem <b>ersten</b> von TANSS angenommenen Aufruf ab, und zwar auch dann, wenn keine
/// Nummer genannt wurde. Was dann zu tun ist, steht in
/// <see cref="TicketCreateResult.Summary"/>, und das ist nicht „noch einmal senden“.</para>
///
/// <para><b>Gesendet werden nur die rechtefreien Felder</b> (siehe <see cref="TicketDraft"/>).
/// Was TANSS beim Anlegen wirklich erzwingt, ist nicht gemessen; scheitert der Aufruf, zeigt
/// dieser Anzeiger den Text von <see cref="TicketCreator.Explain"/>, der die in Frage kommenden
/// Ursachen ausdrücklich als ungeprüft kennzeichnet (Hausregel 2).</para>
///
/// <para><b>Ohne Oberfläche prüfbar.</b> Der ganze Zustand steht hier; beide Übergabewerte sind
/// Schnittstellen, hinter die sich eine Attrappe setzen lässt.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class NewTicketViewModel : ObservableObject, IDisposable
{
    private const string IdleText =
        "Firma wählen, Betreff eintragen — mehr verlangt dieses Werkzeug nicht. Ob TANSS "
        + "weitere Felder erzwingt, ist nicht gemessen; scheitert das Anlegen, steht der Grund "
        + "hier.";

    private const string UnavailableText =
        "Ohne eingerichtete Verbindung zu TANSS lässt sich kein Ticket anlegen.";

    private readonly ITicketCreation? _creation;
    private readonly int _assignTo;
    private bool _disposed;

    /// <summary>Baut den Anleger mit einer eigenen Firmenauswahl.</summary>
    /// <param name="creation">
    /// Der Anlegeweg, oder <see langword="null"/>, wenn nichts eingerichtet ist. Ohne ihn wird
    /// nichts angeboten und ausdrücklich gesagt, warum.
    /// </param>
    /// <param name="companies">Die Firmensuche, oder <see langword="null"/>.</param>
    /// <param name="assignTo">
    /// Der Techniker, dem das neue Ticket zugewiesen wird; 0 heisst „niemandem“ und lässt das
    /// Feld ganz weg. Üblich ist der angemeldete Mitarbeiter — er hat die Arbeit getan, über die
    /// gleich gebucht wird.
    /// </param>
    public NewTicketViewModel(ITicketCreation? creation, ICompanyRepository? companies,
                              int assignTo = 0)
        : this(creation, new CompanyPickerViewModel(companies), assignTo)
    {
    }

    /// <summary>Baut den Anleger auf einer bereits vorhandenen Firmenauswahl.</summary>
    /// <remarks>
    /// Der Weg für Tests und für einen Einsatzort, der die Auswahl anderweitig braucht. Die
    /// Auswahl gehört danach diesem Anzeiger: <see cref="Dispose"/> gibt sie frei.
    /// </remarks>
    /// <param name="creation">Der Anlegeweg, oder <see langword="null"/>.</param>
    /// <param name="company">Die Firmenauswahl.</param>
    /// <param name="assignTo">Der Techniker, dem zugewiesen wird; 0 heisst „niemandem“.</param>
    public NewTicketViewModel(ITicketCreation? creation, CompanyPickerViewModel company,
                              int assignTo = 0)
    {
        ArgumentNullException.ThrowIfNull(company);

        _creation = creation;
        _assignTo = assignTo;
        Company = company;

        _message = creation is null ? UnavailableText : IdleText;
    }

    /// <summary>
    /// Ein Ticket ist angelegt — mit allem, was TANSS dazu gesagt hat.
    /// </summary>
    /// <remarks>
    /// Wird <b>auch dann</b> ausgelöst, wenn keine Nummer kam: Der Aufrufer soll den Fall sehen,
    /// statt auf eine Nummer zu warten, die es nicht gibt.
    /// </remarks>
    public event EventHandler<TicketCreateResult>? Created;

    /// <summary>Die Firmenauswahl dieses Anlegers.</summary>
    public CompanyPickerViewModel Company { get; }

    /// <summary>Ist der Bereich aufgeklappt?</summary>
    /// <remarks>
    /// Zugeklappt beginnend: Der Regelfall ist eine Leistung auf ein bestehendes Ticket, und ein
    /// aufgeklappter Anlegebereich lüde dazu ein, für jede Buchung ein neues anzulegen.
    /// </remarks>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Der Betreff des neuen Tickets.</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Die Beschreibung; darf leer bleiben.</summary>
    [ObservableProperty]
    private string _content = string.Empty;

    /// <summary>Läuft der Aufruf gerade?</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private bool _isBusy;

    /// <summary>
    /// Ist der Anlegeversuch bereits gelaufen?
    /// </summary>
    /// <remarks>
    /// <b>Der Riegel gegen die Dublette.</b> Steht ab dem ersten von TANSS angenommenen Aufruf
    /// auf <see langword="true"/> — auch ohne genannte Nummer. Ein zweiter Aufruf legte ein
    /// zweites Ticket an, und es gäbe nichts, woran die Dublette später zu erkennen wäre.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private bool _isDone;

    /// <summary>Die neue Ticketnummer; 0, solange keine vorliegt.</summary>
    [ObservableProperty]
    private int _createdTicketId;

    /// <summary>
    /// Der Satz unter den Feldern. <b>Wird unverändert angezeigt.</b>
    /// </summary>
    /// <remarks>
    /// Nach einem Aufruf ist das wörtlich <see cref="TicketCreateResult.Summary"/>
    /// beziehungsweise der Text von <see cref="TicketCreator.Explain"/>. Wer sich hier einen
    /// eigenen Satz baut, verliert genau die Fälle, für die jene Typen gemacht sind — die
    /// Antwort ohne Nummer und die ausdrücklich ungeprüfte Ursache.
    /// </remarks>
    [ObservableProperty]
    private string _message = string.Empty;

    /// <summary>Ist die Meldung ein Fehlschlag oder ein offener Punkt?</summary>
    [ObservableProperty]
    private bool _isWarning;

    /// <summary>Lässt sich hier überhaupt ein Ticket anlegen?</summary>
    public bool IsAvailable => _creation is not null;

    /// <summary>Darf jetzt angelegt werden?</summary>
    /// <remarks>
    /// Die Firma wird hier <b>nicht</b> mitgeprüft: Was fehlt, soll auf Knopfdruck als Satz
    /// dastehen und nicht als grauer Knopf, an dem niemand ablesen kann, woran es liegt.
    /// </remarks>
    public bool CanCreate => IsAvailable && !IsBusy && !IsDone;

    /// <summary>Klappt den Bereich auf oder zu.</summary>
    /// <remarks>
    /// Ein angelegtes Ticket bleibt sichtbar: Zuklappen nähme dem Techniker die Nummer und den
    /// Satz, der sagt, was TANSS geantwortet hat.
    /// </remarks>
    [RelayCommand]
    public void Toggle()
    {
        if (IsOpen && IsDone)
        {
            return;
        }

        IsOpen = !IsOpen;
    }

    /// <summary>
    /// Legt das Ticket an.
    /// </summary>
    /// <remarks>
    /// <b>Wirft nicht</b>, ausser bei einem Abbruch durch den Aufrufer. Jeder Fehlschlag wird zu
    /// einem Satz in <see cref="Message"/>; der Leistungsdialog darüber läuft weiter
    /// (Hausregel 5).
    /// </remarks>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>Der Vorgang.</returns>
    [RelayCommand]
    public async Task CreateAsync(CancellationToken ct = default)
    {
        if (_creation is null)
        {
            Show(UnavailableText, warning: true);
            return;
        }

        if (IsBusy || IsDone)
        {
            return;
        }

        TicketDraft draft = new()
        {
            CompanyId = Company.SelectedCompany?.Id ?? 0,
            Title = Title.Trim(),
            Content = Content.Trim(),
            AssignedToEmployeeId = _assignTo,
        };

        if (draft.Problem is { } problem)
        {
            // Der Satz kommt aus dem Vertrag und wird nicht neu erfunden. Gesendet wird nichts:
            // Was ohne TANSS feststeht, braucht keinen Serverlauf.
            Show(problem, warning: true);
            return;
        }

        IsBusy = true;
        try
        {
            TicketCreateResult result =
                await _creation.CreateAsync(draft, ct).ConfigureAwait(true);

            // AB HIER ist der Aufruf angenommen - gleichgueltig, ob eine Nummer kam. Der Riegel
            // faellt deshalb vor jeder Auswertung; ein zweiter Versuch legte sonst ein zweites
            // Ticket an, und es gaebe keine Kennung, an der die Dublette zu erkennen waere.
            IsDone = true;
            CreatedTicketId = result.TicketId;

            Show(result.Summary, warning: !result.HasTicketId);
            Created?.Invoke(this, result);
        }
        catch (OperationCanceledException)
        {
            // Ein Abbruch ist kein Fehlschlag - aber auch keine Zusage, dass nichts angelegt
            // wurde. Der Riegel bleibt offen, der Satz sagt, dass es offen ist.
            Show("Das Anlegen wurde abgebrochen. Ob TANSS das Ticket dennoch angelegt hat, ist "
                 + "nicht ermittelt — vor einem zweiten Versuch in TANSS nachschlagen.",
                 warning: true);
            throw;
        }
        catch (TanssException ex)
        {
            // Der Typ der Ausnahme traegt Bedeutung und wird nicht umgehuellt; Explain nennt die
            // moeglichen Ursachen und kennzeichnet sie ausdruecklich als ungeprueft.
            Show(TicketCreator.Explain(ex), warning: true);
        }
        catch (Exception ex)
        {
            Show("Das Ticket wurde nicht angelegt: " + Redaction.Scrub(ex.Message)
                 + " Ob die Anfrage TANSS erreicht hat, ist damit nicht ermittelt — vor einem "
                 + "zweiten Versuch in TANSS nachschlagen.",
                 warning: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Gibt die Firmenauswahl frei.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Company.Dispose();
    }

    /// <summary>Setzt Satz und Auffälligkeit in einem Zug.</summary>
    private void Show(string message, bool warning)
    {
        Message = message;
        IsWarning = warning;
    }
}
