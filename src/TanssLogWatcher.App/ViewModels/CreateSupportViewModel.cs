using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api.Contract;
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
///
/// <para><b>Ein Ticket entsteht hier, wenn keines da ist.</b> Sagt die Prüfung unter dem
/// Ticketfeld „gibt es nicht“, oder gab es nie eines, legt <see cref="NewTicket"/> eines an —
/// samt Firmenauswahl. Die Nummer wandert danach von selbst ins Ticketfeld. Ohne diesen Weg
/// endete der Dialog an dieser Stelle: Ticket in TANSS von Hand anlegen, Nummer abschreiben,
/// Dialog neu öffnen.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class CreateSupportViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly TimerRow _timer;

    private SupportDraft? _draft;
    private bool _disposed;

    /// <summary>Baut den Dialog zu einem Timer und holt die Vorbereitung.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="timer">Der Timer, der zur Leistung werden soll.</param>
    /// <param name="tickets">Die offenen Tickets zur Auswahl.</param>
    /// <param name="verification">
    /// Die Prüfstelle für Ticketnummern. Bleibt sie offen, wird die der Laufzeit genommen —
    /// der Übergabewert ist dafür da, den Dialog ohne Netz prüfbar zu halten.
    /// </param>
    /// <param name="newTicket">
    /// Der Anleger für ein neues Ticket. Bleibt er offen, wird einer aus der Laufzeit gebaut —
    /// mit deren Firmensuche und dem angemeldeten Mitarbeiter als Zuweisung.
    /// </param>
    public CreateSupportViewModel(AppHost host, TimerRow timer, IReadOnlyList<TicketRow> tickets,
                                  ITicketVerification? verification = null,
                                  NewTicketViewModel? newTicket = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(tickets);

        _host = host;
        _timer = timer;

        TicketCheck = verification is not null
            ? new TicketCheckPanel(verification)
            : TicketCheckPanel.For(host.Composition?.Tickets);

        // Ohne Einrichtung bekommt der Anleger zweimal null und sagt das selbst; er wird
        // trotzdem gebaut, damit die Bindungen im Fenster nicht ins Leere zeigen.
        NewTicket = newTicket ?? new NewTicketViewModel(
            host.Composition?.NewTickets,
            host.Composition?.Companies,
            host.Composition?.Config.Tanss.EmployeeId ?? 0);

        NewTicket.Created += OnTicketCreated;

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

    /// <summary>Das gewählte Ticket aus der Liste; <c>null</c> heisst „nichts gewählt“.</summary>
    /// <remarks>
    /// <b>Allein sagt das nichts über die Buchung.</b> Das Auswahlfeld im Fenster ist
    /// bearbeitbar; wer eine Nummer tippt, die in der Liste nicht steht, lässt diese
    /// Eigenschaft auf <c>null</c> stehen. Was gebucht wird, sagt <see cref="TicketId"/>.
    /// </remarks>
    [ObservableProperty]
    private TicketRow? _selectedTicket;

    /// <summary>
    /// Was im Textteil des Auswahlfeldes steht.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Grund, warum es diese Eigenschaft gibt.</b> Das Auswahlfeld ist
    /// <c>IsEditable=True</c>. Gebunden war nur <c>SelectedItem</c>, und eine getippte Nummer
    /// fiel damit lautlos auf 0 — die Leistung wurde ohne Ticket gebucht, und niemand erfuhr
    /// davon. Getippter Text und Auswahl werden jetzt beide gelesen.</para>
    /// <para>Die Bindung steht auf <c>LostFocus</c>: Dann wird aufgelöst und geprüft. Ein
    /// Anschlag je Zeichen führe TANSS jede Zwischenstufe einer Nummer vor.</para>
    /// </remarks>
    [ObservableProperty]
    private string _ticketText = string.Empty;

    /// <summary>
    /// Die Ticketnummer, die tatsächlich gebucht wird; 0 heisst „ohne Ticket“.
    /// </summary>
    /// <remarks>
    /// Aus Auswahl und getipptem Text aufgelöst, siehe <see cref="ResolveTicket"/>. Steht im
    /// Feld etwas, das keine Nummer ist, bleibt sie 0 — <see cref="TicketIsUnreadable"/> ist
    /// dann gesetzt und das Buchen wartet, statt still ohne Ticket zu buchen.
    /// </remarks>
    public int TicketId { get; private set; }

    /// <summary>
    /// Steht im Ticketfeld etwas, das keine Nummer ist?
    /// </summary>
    /// <remarks>
    /// <para><b>Das ist kein Ergebnis der Prüfung, sondern eine Feststellung des Werkzeugs</b>
    /// — und nur deshalb darf es das Buchen anhalten. Was TANSS über eine Nummer sagt, hält
    /// hier nichts an; auch „gibt es nicht“ steht nur da.</para>
    /// <para>Anhalten ist an dieser Stelle gefahrlos: Dieser Dialog hat keine wartende Zeile, es
    /// geht nichts verloren, und der Satz unter dem Feld sagt, was zu tun ist.</para>
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBook))]
    private bool _ticketIsUnreadable;

    /// <summary>Was aus der Ticketeingabe geworden ist.</summary>
    public TicketCheckPanel TicketCheck { get; }

    /// <summary>
    /// Der Anleger für ein neues Ticket — mit eigener Firmenauswahl.
    /// </summary>
    /// <remarks>
    /// Er hängt ausdrücklich an diesem Dialog und nicht an einem eigenen Fenster: Gebraucht wird
    /// er in dem Augenblick, in dem die Prüfung unter dem Ticketfeld sagt, dass es die Nummer
    /// nicht gibt. Was er anlegt, landet über <see cref="OnTicketCreated"/> im Ticketfeld.
    /// </remarks>
    public NewTicketViewModel NewTicket { get; }

    /// <summary>
    /// Die laufende Ticketprüfung, oder eine bereits abgeschlossene.
    /// </summary>
    /// <remarks>
    /// Öffentlich, damit sich die Prüfung ohne Oberfläche abwarten lässt. Das Fenster braucht
    /// sie nicht.
    /// </remarks>
    public Task TicketCheckPending { get; private set; } = Task.CompletedTask;

    /// <summary>Löst auf und prüft, sobald das Auswahlfeld den Fokus verliert.</summary>
    /// <param name="value">Der neue Inhalt des Textteils.</param>
    partial void OnTicketTextChanged(string value) => ResolveTicket();

    /// <summary>
    /// Übernimmt ein eben angelegtes Ticket in das Ticketfeld.
    /// </summary>
    /// <remarks>
    /// <para>Ohne diesen Schritt müsste der Techniker die Nummer aus der Meldung abschreiben —
    /// genau die Handarbeit, derentwegen der Anleger hier sitzt.</para>
    /// <para><b>Ohne Nummer wird nichts übernommen.</b> TANSS kann den Aufruf angenommen haben,
    /// ohne eine zu nennen; dann steht im Ticketfeld weiterhin, was vorher darin stand, und die
    /// Meldung des Anlegers sagt, was zu tun ist. Eine geratene Nummer stünde sonst gleich in
    /// einer gebuchten Leistung (Hausregel 2).</para>
    /// <para>Ein angelegtes Ticket steht anschliessend auch in der Auswahlliste: Wer das Feld
    /// leert und es sich anders überlegt, findet es dort wieder.</para>
    /// </remarks>
    /// <param name="sender">Der Anleger.</param>
    /// <param name="result">Was TANSS geantwortet hat.</param>
    private void OnTicketCreated(object? sender, TicketCreateResult result)
    {
        if (!result.HasTicketId)
        {
            return;
        }

        if (result.Ticket is { } created && Tickets.All(row => row.Id != created.Id))
        {
            Tickets.Add(new TicketRow(created));
        }

        TicketRow? row = Tickets.FirstOrDefault(entry => entry.Id == result.TicketId);
        SelectedTicket = row;

        // Der Textteil ist der massgebliche Weg: Ueber ihn laeuft ResolveTicket, und erst das
        // setzt TicketId - dieselbe Aufloesung, die auch eine getippte Nummer nimmt.
        TicketText = row?.Display
            ?? string.Create(CultureInfo.CurrentCulture, $"#{result.TicketId}");
    }

    /// <summary>
    /// Macht aus Auswahl und getipptem Text eine Ticketnummer — oder sagt, dass es keine gibt.
    /// </summary>
    /// <remarks>
    /// <para>Die Reihenfolge ist die des Zutrauens: eine Zeile der Liste, dann eine gelesene
    /// Nummer, dann der Befund „das ist keine“. Ein leeres Feld heisst „ohne Ticket“ und ist
    /// erlaubt — ausser die Auswahl trägt noch eine Zeile, dann gilt die.</para>
    /// <para>Der Vergleich läuft über <c>TicketRow.Display</c>, weil genau dieser Text im
    /// Auswahlfeld steht, sobald jemand eine Zeile anklickt.</para>
    /// </remarks>
    private void ResolveTicket()
    {
        string text = TicketText.Trim();

        if (text.Length == 0)
        {
            // Leeres Feld. Eine noch stehende Auswahl gilt weiter - sie kann gesetzt worden
            // sein, ohne dass das Feld je den Fokus hatte (siehe LoadAsync).
            TicketId = SelectedTicket?.Id ?? 0;
            TicketIsUnreadable = false;

            if (TicketId > 0)
            {
                TicketCheckPending = TicketCheck.CheckAsync(TicketId);
            }
            else
            {
                TicketCheck.Clear();
            }

            return;
        }

        TicketRow? chosen = Tickets.FirstOrDefault(
            row => string.Equals(text, row.Display, StringComparison.Ordinal));

        if (chosen is not null)
        {
            TicketId = chosen.Id;
            TicketIsUnreadable = false;
            TicketCheckPending = TicketCheck.CheckAsync(TicketId);
            return;
        }

        if (TicketCheckPanel.TryReadNumber(text, out int typed))
        {
            TicketId = typed;
            TicketIsUnreadable = false;
            TicketCheckPending = TicketCheck.CheckAsync(typed);
            return;
        }

        // HIER fiel die Eingabe bisher lautlos auf 0. Jetzt steht sie da, und gebucht wird
        // nicht, bis der Techniker sie berichtigt oder das Feld leert.
        TicketId = 0;
        TicketIsUnreadable = true;
        TicketCheck.ShowProblem($"„{text}“ ist keine Ticketnummer. Erwartet wird eine Zahl oder "
            + "eine Zeile aus der Auswahl; für eine Leistung ohne Ticket bleibt das Feld leer.");
    }

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
    /// <remarks>
    /// <see cref="TicketIsUnreadable"/> hält an, das Ergebnis der Ticketprüfung nicht. Was TANSS
    /// über eine Nummer sagt, steht unter dem Feld; ob damit gebucht wird, entscheidet der
    /// Techniker.
    /// </remarks>
    public bool CanBook => IsReady && !IsBusy && !TicketIsUnreadable;

    /// <summary>
    /// Bucht die Leistung in TANSS.
    /// </summary>
    /// <remarks>
    /// <para>Der einzige Schritt in diesem Dialog, der etwas verändert — und er ist nicht
    /// zurückzunehmen.</para>
    /// <para><b>Gebucht wird <see cref="TicketId"/> und nicht <c>SelectedTicket?.Id ?? 0</c>.</b>
    /// Das Auswahlfeld ist bearbeitbar: Eine getippte Nummer steht im Text und nicht in der
    /// Auswahl, und die alte Zeile liess sie lautlos auf 0 fallen.</para>
    /// </remarks>
    [RelayCommand]
    private async Task BookAsync()
    {
        if (_draft is not { } draft || _host.Composition is not { } composition)
        {
            return;
        }

        if (TicketIsUnreadable)
        {
            // Derselbe Riegel wie in CanBook, und er steht hier noch einmal: Eine Bindung, die
            // den Knopf versehentlich freigibt, darf die Eingabe nicht doch verschwinden lassen.
            Message = "Im Ticketfeld steht keine Nummer. Bitte berichtigen oder das Feld leeren "
                + "— dann wird ohne Ticket gebucht.";
            return;
        }

        draft.Text = Text.Trim();
        draft.TicketId = TicketId;

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
    /// Gibt frei, was mit dem Fenster zugeht.
    /// </summary>
    /// <remarks>
    /// Der Anleger führt eine Firmenauswahl, und die hält einen <c>DispatcherTimer</c> für die
    /// Entprellung. Ein Takt, der nach dem Schliessen weiterliefe, hinge am Strang der
    /// Oberfläche und hielte den ganzen Dialog im Speicher.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NewTicket.Created -= OnTicketCreated;
        NewTicket.Dispose();
    }

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

            // Der Textteil wird mitgesetzt, und das faengt einen zweiten stillen Verlust:
            // Traegt der Timer ein Ticket, das nicht unter den offenen steht - erledigt, fremd
            // oder gar nicht geladen -, blieb das Auswahlfeld leer, und die Nummer war beim
            // Buchen fort. Jetzt steht sie als "#4711" im Feld und geht mit hinaus.
            TicketText = SelectedTicket is { } row
                ? row.Display
                : draft.TicketId > 0
                    ? string.Create(CultureInfo.CurrentCulture, $"#{draft.TicketId}")
                    : string.Empty;

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
