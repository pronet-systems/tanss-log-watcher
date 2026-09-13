using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Aus einem Timer eine Leistung machen — mit einer Beschreibung je Zeitabschnitt.
/// </summary>
/// <remarks>
/// <para><b>Warum je Abschnitt und nicht einmal für alles.</b> Ein Timer, der dreimal gestartet
/// und gestoppt wurde, sind drei Arbeitsschritte. Ein einziger Text darüber ist für den Kunden
/// nicht nachvollziehbar und für den Techniker eine Woche später auch nicht. TANSS sieht die
/// Gliederung selbst vor: Die vorbereitete Leistung kommt mit einem Texteintrag je
/// Laufabschnitt zurück — nachgemessen, nicht angenommen.</para>
///
/// <para><b>Die Leistung entsteht in zwei Schritten, und dazwischen sitzt dieser Dialog.</b>
/// Erst lässt TANSS sich die Leistung vorbereiten (Stundensatz, Abrechnungsart, Fahrzeug — alles,
/// was aus Kunde und Vertrag folgt), dann trägt der Techniker ein, was er getan hat, und erst
/// dann wird gebucht. Bis auf „Leistung buchen“ verändert dieser Dialog in TANSS nichts.</para>
///
/// <para><b>Gebucht ist gebucht.</b> Eine Leistung lässt sich von hier aus nicht zurücknehmen;
/// das geht nur in TANSS. Deshalb steht die Schaltfläche allein rechts und heißt, was sie
/// tut.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class CreateSupportViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly TimerRow _timer;

    private SupportDraft? _draft;

    /// <summary>Baut den Dialog zu einem Timer und holt die Vorbereitung.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="timer">Der Timer, der zur Leistung werden soll.</param>
    /// <param name="tickets">Die offenen Tickets zur Auswahl.</param>
    public CreateSupportViewModel(AppHost host, TimerRow timer, IReadOnlyList<TicketRow> tickets)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(tickets);

        _host = host;
        _timer = timer;

        foreach (TicketRow ticket in tickets)
        {
            Tickets.Add(ticket);
        }

        TimerTitle = timer.Title;
        _ = LoadAsync();
    }

    /// <summary>Der Dialog ist fertig und darf zugehen.</summary>
    public event EventHandler? Finished;

    /// <summary>Wurde tatsächlich eine Leistung gebucht?</summary>
    /// <remarks>
    /// Die Timerseite liest das, um danach neu zu laden. Ein Abbruch soll keinen Netzaufruf
    /// nach sich ziehen.
    /// </remarks>
    public bool WasBooked { get; private set; }

    /// <summary>Der Titel des Timers.</summary>
    public string TimerTitle { get; }

    /// <summary>Die Zeitabschnitte, jeder mit eigener Beschreibung.</summary>
    public ObservableCollection<SegmentRow> Segments { get; } = [];

    /// <summary>Die offenen Tickets zur Auswahl.</summary>
    public ObservableCollection<TicketRow> Tickets { get; } = [];

    /// <summary>Das gewählte Ticket; <c>null</c> heisst „ohne Ticket“.</summary>
    [ObservableProperty]
    private TicketRow? _selectedTicket;

    /// <summary>Der Text der Leistung — das, was auf der Rechnung steht.</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>Läuft gerade ein Netzaufruf?</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBook))]
    private bool _isBusy = true;

    /// <summary>Liegt eine vorbereitete Leistung vor?</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBook))]
    private bool _isReady;

    /// <summary>Die Rückmeldung; leer, solange nichts zu melden ist.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    /// <summary>Der Satz über den Abschnitten.</summary>
    [ObservableProperty]
    private string _summary = "Die Leistung wird in TANSS vorbereitet …";

    /// <summary>Gibt es etwas zu melden?</summary>
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Darf jetzt gebucht werden?</summary>
    public bool CanBook => IsReady && !IsBusy;

    /// <summary>
    /// Bucht die Leistung in TANSS.
    /// </summary>
    /// <remarks>
    /// Der einzige Schritt in diesem Dialog, der etwas verändert — und er ist nicht
    /// zurückzunehmen.
    /// </remarks>
    [RelayCommand]
    private async Task BookAsync()
    {
        if (_draft is not { } draft || _host.Composition is not { } composition)
        {
            return;
        }

        draft.Text = Text.Trim();
        draft.TicketId = SelectedTicket?.Id ?? 0;

        foreach (SegmentRow row in Segments)
        {
            row.Apply();
        }

        IsBusy = true;
        try
        {
            int id = await composition.Supports.CreateAsync(draft).ConfigureAwait(true);

            WasBooked = true;

            Message = id > 0
                ? string.Create(CultureInfo.CurrentCulture, $"Leistung {id} ist angelegt.")
                : "Die Leistung ist angelegt; eine Kennung hat der Server nicht genannt.";

            IsReady = false;
            Finished?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = "Die Leistung liess sich nicht anlegen: " + Redaction.Scrub(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Schliesst den Dialog, ohne etwas zu buchen.</summary>
    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Lässt TANSS die Leistung vorbereiten.
    /// </summary>
    /// <remarks>
    /// Dieser Schritt legt nichts an. Er holt die Felder, die aus Kunde, Vertrag und Mitarbeiter
    /// folgen — und die Gliederung in Zeitabschnitte, um die es hier geht.
    /// </remarks>
    private async Task LoadAsync()
    {
        if (_host.Composition is not { } composition)
        {
            Summary = "Ohne Einrichtung lässt sich keine Leistung anlegen.";
            IsBusy = false;
            return;
        }

        try
        {
            SupportDraft draft = await composition.Supports
                .PrepareFromTimerAsync(_timer.Id).ConfigureAwait(true);

            _draft = draft;

            Segments.Clear();
            foreach (SupportSegment segment in draft.Segments)
            {
                Segments.Add(new SegmentRow(segment));
            }

            // Der vorbelegte Text ist der Timertitel. Er bleibt stehen, ist aber zu
            // ueberschreiben: Was auf der Rechnung steht, ist selten der Titel eines Timers.
            Text = draft.Text.Trim();

            // Das Ticket des Timers, falls er eines traegt. Die Auswahl bleibt offen, damit
            // eine Leistung auch nachtraeglich einem Ticket zugeordnet werden kann.
            SelectedTicket = draft.TicketId > 0
                ? Tickets.FirstOrDefault(ticket => ticket.Id == draft.TicketId)
                : null;

            Summary = string.Create(CultureInfo.CurrentCulture,
                $"{Texts.Count(draft.Segments.Count, "Zeitabschnitt", "Zeitabschnitte")}, "
                + $"zusammen {Texts.Minutes(draft.Duration)} — so, wie TANSS sie rechnet.");

            IsReady = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Summary = "Die Leistung liess sich nicht vorbereiten: " + Redaction.Scrub(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>
/// Ein Zeitabschnitt im Dialog: wann, wie lange, und was dabei getan wurde.
/// </summary>
/// <remarks>
/// <para>Die Beschreibung steht hier und wird erst beim Buchen in den Block zurückgeschrieben
/// (<see cref="Apply"/>). Sonst hinterliesse ein abgebrochener Dialog halb ausgefüllte
/// Abschnitte in einem Objekt, das anschliessend noch gebucht werden könnte.</para>
/// <para><b>Zwei Dauern, wenn sie auseinandergehen.</b> Gemessen wurde das eine, abgerechnet
/// wird das andere — TANSS rundet nach den Regeln der Instanz. Beides zu zeigen erspart die
/// Rückfrage, warum aus vier Minuten eine Viertelstunde wird.</para>
/// </remarks>
public sealed partial class SegmentRow : ObservableObject
{
    private readonly SupportSegment _segment;

    /// <summary>Baut die Zeile zu einem Abschnitt.</summary>
    /// <param name="segment">Der Abschnitt aus der vorbereiteten Leistung.</param>
    public SegmentRow(SupportSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        _segment = segment;
        _text = segment.Text;

        string start = segment.Date is { } date
            ? Texts.Clock(date.ToLocalTime())
            : "ohne Zeitstempel";

        TimeText = segment.RoundedDuration == segment.Duration
            ? string.Create(CultureInfo.CurrentCulture,
                $"{start} · {Texts.Minutes(segment.Duration)}")
            : string.Create(CultureInfo.CurrentCulture,
                $"{start} · {Texts.Minutes(segment.Duration)} gemessen, "
                + $"{Texts.Minutes(segment.RoundedDuration)} abgerechnet");
    }

    /// <summary>Die Nummer des Abschnitts, wie sie der Techniker sieht.</summary>
    public int Number => _segment.Index + 1;

    /// <summary>Wann und wie lange, in einem Satz.</summary>
    public string TimeText { get; }

    /// <summary>Was in diesem Abschnitt getan wurde.</summary>
    [ObservableProperty]
    private string _text;

    /// <summary>Schreibt die Beschreibung in die Leistung zurück.</summary>
    public void Apply() => _segment.Text = Text.Trim();
}
