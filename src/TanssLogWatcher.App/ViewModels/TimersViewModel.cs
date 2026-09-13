using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Die Timerseite: die eigenen Timer aus TANSS, mit den Handgriffen, die dazugehören.
/// </summary>
/// <remarks>
/// <para><b>Warum diese Seite überhaupt schreibt.</b> Eine Fernwartung ohne den zugehörigen
/// Timer ist halb erfasste Arbeitszeit. Der Techniker soll beim Abschluss einer Sitzung nicht
/// erst TANSS öffnen müssen, um einen Timer anzuhalten — der Griff dazu gehört dorthin, wo er
/// gerade hinsieht.</para>
///
/// <para><b>TANSS kennt nur einen Umschalter.</b> Es gibt kein „Start“ und kein „Stop“,
/// sondern <c>PUT /timers/{id}/toggle</c>. Zwei Schaltflächen daraus zu machen hiesse, einen
/// Zustand zu behaupten, den nur der Server kennt; deshalb eine Fläche, deren Beschriftung dem
/// zuletzt gelesenen Stand folgt — und nach jedem Umschalten wird neu gelesen.</para>
///
/// <para><b>Geholt wird auf Aufforderung, nicht im Takt.</b> Timer ändern sich selten, und
/// jeder Abruf kostet einen Netzaufruf. Der Sekundentakt hier rechnet nur die vorhandenen
/// Zeilen weiter und fragt nichts ab.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class TimersViewModel : RuntimeViewModel
{
    private readonly DispatcherTimer _ticker;

    private readonly SystemLookup _lookup;

    /// <summary>Baut die Seite und holt die Timer.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="lookup">Der gemeinsame Nachschlag; er bringt die offenen Tickets mit.</param>
    public TimersViewModel(AppHost host, SystemLookup lookup) : base(host)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        _lookup = lookup;

        _ticker = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _ticker.Tick += OnTick;
        _ticker.Start();

        // Ein Aufruf, und er zieht beides mit. Vorher liefen hier zwei Aufgaben nebeneinander
        // los, die auf dieselbe Verbindung zugriffen.
        _ = LoadAsync();
    }

    /// <summary>Holt die offenen Tickets für die Auswahl.</summary>
    /// <remarks>
    /// Die gewählte Zeile wird gehalten, wenn es sie danach noch gibt: Wer ein Ticket gewählt
    /// hat und dann „Neu laden“ drückt, soll seine Wahl nicht verlieren.
    /// </remarks>
    private async Task LoadTicketsAsync()
    {
        _ = await _lookup.RefreshAsync(Host.Composition).ConfigureAwait(true);

        TicketRow? previous = SelectedTicket;

        Tickets.Clear();
        foreach (TicketRow ticket in _lookup.Tickets)
        {
            Tickets.Add(ticket);
        }

        SelectedTicket = Tickets.FirstOrDefault(ticket => ticket.Id == previous?.Id);
        OnPropertyChanged(nameof(TicketHint));
    }

    /// <summary>Die eigenen Timer.</summary>
    public ObservableCollection<TimerRow> Timers { get; } = [];

    /// <summary>Läuft gerade ein Netzaufruf?</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Der Satz über der Liste.</summary>
    [ObservableProperty]
    private string _summary = "Wird geholt …";

    /// <summary>Der Titel eines neu anzulegenden Timers.</summary>
    [ObservableProperty]
    private string _newTitle = string.Empty;

    /// <summary>
    /// Die offenen Tickets zur Auswahl.
    /// </summary>
    /// <remarks>
    /// <b>Eine Auswahl und kein Zahlenfeld.</b> Vorher stand hier eine freie Eingabe, die jede
    /// Zahl annahm — auch eine, zu der es kein Ticket gibt. Eine Leistung auf eine erfundene
    /// Nummer zu buchen ist schlimmer als gar kein Ticket: Sie taucht in keiner Auswertung auf
    /// und fällt niemandem auf.
    /// </remarks>
    public ObservableCollection<TicketRow> Tickets { get; } = [];

    /// <summary>Das gewählte Ticket; <c>null</c> heisst „ohne Ticket“.</summary>
    [ObservableProperty]
    private TicketRow? _selectedTicket;

    /// <summary>
    /// Der Timer, für den die Rückfrage steht; <c>null</c>, wenn keine steht.
    /// </summary>
    /// <remarks>
    /// Die Zeile selbst und nicht ihre Kennung: Die Leiste zeigt Titel, Ticket und die bisher
    /// erfasste Zeit, damit die Frage beantwortbar ist. „Wirklich löschen?“ allein wird
    /// weggeklickt, ohne dass jemand hinsieht.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingDelete))]
    [NotifyPropertyChangedFor(nameof(PendingDeleteText))]
    private TimerRow? _pendingDelete;

    /// <summary>Steht gerade eine Rückfrage?</summary>
    public bool HasPendingDelete => PendingDelete is not null;

    /// <summary>Was in der Rückfrage über den Timer steht.</summary>
    /// <remarks>
    /// Der Zusatz steht INNERHALB der Interpolation und nicht als Verkettung dahinter:
    /// <c>string.Create</c> nimmt die Zeichenkette als Handler, und ein angehängter
    /// Bedingungsausdruck macht daraus eine gewöhnliche Verkettung, auf die diese Überladung
    /// nicht mehr passt (CS1620). Zwei interpolierte Zeichenketten mit <c>+</c> dazwischen
    /// gingen — wie anderswo im Haus —, ein <c>?:</c> geht nicht.
    /// </remarks>
    public string PendingDeleteText
    {
        get
        {
            if (PendingDelete is not { } row)
            {
                return string.Empty;
            }

            string laeuft = row.IsRunning ? " Er läuft gerade." : string.Empty;

            return string.Create(CultureInfo.CurrentCulture,
                $"„{row.Title}“ · {row.TicketText} · {row.ElapsedText} erfasst.{laeuft}");
        }
    }

    /// <summary>Was zur Ticketauswahl zu sagen ist.</summary>
    public string TicketHint => Tickets.Count > 0
        ? Texts.Count(Tickets.Count, "offenes Ticket", "offene Tickets") + " zur Auswahl."
        : "Keine offenen Tickets geladen. Ein Timer lässt sich trotzdem anlegen — dann ohne "
          + "Ticket.";

    /// <summary>Gibt es etwas anzuzeigen?</summary>
    public bool IsEmpty => Timers.Count == 0;

    /// <summary>
    /// Holt alles neu, was diese Seite zeigt: die Timer <b>und</b> die Ticketauswahl.
    /// </summary>
    /// <remarks>
    /// <para><b>Hier hängt „Neu laden“ dran, und bis hierher zog es die Tickets nicht mit.</b>
    /// Die Auswahl wurde einmal im Konstruktor gefüllt und blieb danach stehen: Ein Ticket, das
    /// während der Sitzung aufgemacht wurde, tauchte auch nach dem Klick nicht auf, und eines,
    /// das inzwischen erledigt war, blieb wählbar.</para>
    /// <para>Die Tickets zuerst: Sie füllen die Auswahl über der Liste, und die soll nicht
    /// leer bleiben, während die Timer noch kommen.</para>
    /// </remarks>
    [RelayCommand]
    private async Task LoadAsync()
    {
        await LoadTicketsAsync().ConfigureAwait(true);
        await LoadTimersAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Holt die Timer neu — ohne die Ticketauswahl.
    /// </summary>
    /// <remarks>
    /// Der Weg für alles, was ohnehin gerade an den Timern gearbeitet hat: Umschalten, Anlegen,
    /// Löschen, Buchen und jede Zustandsmeldung. Die Ticketliste ändert sich dabei nicht, und
    /// sie mitzuholen kostete drei weitere Aufrufe an TANSS je Handgriff.
    /// </remarks>
    private async Task LoadTimersAsync()
    {
        // Die Zeilen werden gleich ersetzt; eine Rueckfrage, die auf eine abgeloeste Zeile
        // zeigte, loeschte nach dem Neuladen etwas anderes als das, wonach gefragt wurde.
        PendingDelete = null;

        if (Host.Composition is not { } composition)
        {
            Timers.Clear();
            Summary = "Ohne Einrichtung gibt es keine Timer zu holen. "
                + "Das Zahnrad unter „Einstellungen“ öffnet den Assistenten.";
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        IsBusy = true;
        try
        {
            IReadOnlyList<TanssTimer> timers =
                await composition.Timers.ListAsync().ConfigureAwait(true);

            DateTimeOffset now = Host.Clock.GetLocalNow();

            Timers.Clear();
            foreach (TanssTimer timer in timers.OrderByDescending(t => t.IsRunning)
                                               .ThenByDescending(t => t.StartTime))
            {
                Timers.Add(new TimerRow(timer, now));
            }

            int running = timers.Count(t => t.IsRunning);

            Summary = timers.Count == 0
                ? "Zurzeit ist kein Timer angelegt."
                : string.Create(CultureInfo.CurrentCulture,
                    $"{Texts.Count(timers.Count, "Timer", "Timer")}, davon {running} laufend.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Summary = "Die Timer liessen sich nicht holen: " + Redaction.Scrub(ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>
    /// Startet einen angehaltenen Timer oder hält einen laufenden an.
    /// </summary>
    /// <remarks>
    /// Danach wird die ganze Liste neu gelesen und nicht bloss die eine Zeile fortgeschrieben:
    /// TANSS hält höchstens einen Timer gleichzeitig laufend — ein Start kann also einen
    /// anderen angehalten haben, von dem diese Zeile nichts weiss.
    /// </remarks>
    /// <param name="row">Die Zeile, deren Timer umzuschalten ist.</param>
    [RelayCommand]
    private async Task ToggleAsync(TimerRow? row)
    {
        if (row is null || Host.Composition is not { } composition)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _ = await composition.Timers.ToggleAsync(row.Id).ConfigureAwait(true);
            await LoadTimersAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Summary = "Das Umschalten ist fehlgeschlagen: " + Redaction.Scrub(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Legt einen Timer an.</summary>
    /// <remarks>
    /// Startzeit und Dauer setzt der Server selbst — ein neuer Timer läuft ab dem Anlegen.
    /// </remarks>
    [RelayCommand]
    private async Task CreateAsync()
    {
        if (Host.Composition is not { } composition)
        {
            return;
        }

        string title = NewTitle.Trim();

        if (title.Length == 0)
        {
            Summary = "Ein Timer ohne Titel ist in TANSS später nicht zuzuordnen.";
            return;
        }

        // Keine Pruefung noetig: Was hier steht, kommt aus der Liste der offenen Tickets des
        // Servers. Eine von Hand getippte Nummer gibt es nicht mehr - und damit auch nicht mehr
        // die Moeglichkeit, auf eine erfundene zu buchen.
        int ticketId = SelectedTicket?.Id ?? 0;

        IsBusy = true;
        try
        {
            _ = await composition.Timers.CreateAsync(title, ticketId).ConfigureAwait(true);

            NewTitle = string.Empty;
            SelectedTicket = null;

            await LoadTimersAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Summary = "Der Timer liess sich nicht anlegen: " + Redaction.Scrub(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Öffnet den Dialog, der aus diesem Timer eine Leistung macht.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Weg, der bisher fehlte.</b> Erfasste Zeit, die nie zur Leistung wird, ist
    /// Arbeit, die niemand bezahlt. Bis hierher war der Timer eine Sackgasse: starten, stoppen,
    /// löschen — und das Buchen dann doch wieder in TANSS.</para>
    /// <para>Ein laufender Timer wird vorher nicht angehalten. Das wäre eine Entscheidung über
    /// die Arbeitszeit des Technikers, und die trifft er selbst; der Dialog sagt statt dessen,
    /// dass der laufende Abschnitt noch nicht dabei ist.</para>
    /// </remarks>
    /// <param name="row">Die Zeile, deren Timer zur Leistung werden soll.</param>
    [RelayCommand]
    private async Task BookAsync(TimerRow? row)
    {
        if (row is null || Host.Composition is null)
        {
            return;
        }

        Views.CreateSupportWindow dialog = new(Host, row, [.. Tickets])
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        _ = dialog.ShowDialog();

        if (dialog.ViewModel.WasBooked)
        {
            Summary = "Die Leistung ist gebucht. Der Timer bleibt bestehen — TANSS merkt sich "
                + "an der Leistung, aus welchem Timer sie entstanden ist.";

            await LoadTimersAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Stellt die Rückfrage — und löscht ausdrücklich nichts.
    /// </summary>
    /// <remarks>
    /// <para><b>Ein Timer ist erfasste Arbeitszeit.</b> Was hier verschwindet, ist in TANSS weg
    /// und nicht zurückzuholen. Die Dokumentation dieser Stelle sagte die Rückfrage seit jeher
    /// zu („Die Rückfrage stellt die Ansicht“) — gestellt hat sie niemand, und die Schaltfläche
    /// löschte beim ersten Klick.</para>
    ///
    /// <para><b>Die Rückfrage steht in der Seite und nicht in einem Fenster.</b> Sie ist damit
    /// bloss ein Zustand dieses Ansichtsmodells und lässt sich ohne Oberfläche prüfen. Ein
    /// modaler Dialog wäre genau das nicht gewesen — und eine Zusage, die sich nur von Hand
    /// prüfen lässt, ist die Art Zusage, die hier schon zweimal jahrelang nicht eingelöst war.
    /// </para>
    /// </remarks>
    /// <param name="row">Die Zeile, nach der gefragt werden soll.</param>
    [RelayCommand]
    private void AskDelete(TimerRow? row) => PendingDelete = row;

    /// <summary>Nimmt die Rückfrage zurück; es bleibt alles, wie es war.</summary>
    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    /// <summary>
    /// Löscht den Timer, nach dem gefragt wurde.
    /// </summary>
    /// <remarks>
    /// <para><b>Ohne Übergabewert, und genau darin liegt die Zusage.</b> Dieser Befehl kann nur
    /// löschen, was <see cref="AskDelete"/> zuvor hingelegt hat. Eine Schaltfläche, die ihn aus
    /// Versehen mit einer Zeile bindet, löscht nichts — die Rückfrage lässt sich nicht umgehen,
    /// auch nicht durch einen Fehlgriff im XAML.</para>
    ///
    /// <para>Die Rückfrage wird <b>vor</b> dem Netzaufruf zurückgenommen: Die Leiste soll
    /// verschwinden, sobald entschieden ist, und nicht erst, wenn TANSS geantwortet hat.</para>
    /// </remarks>
    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } row || Host.Composition is not { } composition)
        {
            return;
        }

        PendingDelete = null;

        IsBusy = true;
        try
        {
            await composition.Timers.DeleteAsync(row.Id).ConfigureAwait(true);
            await LoadTimersAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Summary = "Der Timer liess sich nicht löschen: " + Redaction.Scrub(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <inheritdoc />
    protected override void OnStatusUpdated(AppStatus status) => _ = LoadTimersAsync();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ticker.Stop();
            _ticker.Tick -= OnTick;
        }

        base.Dispose(disposing);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        DateTimeOffset now = Host.Clock.GetLocalNow();

        foreach (TimerRow row in Timers)
        {
            row.Refresh(now);
        }
    }
}
