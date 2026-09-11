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

    /// <summary>Baut die Seite und holt die Timer.</summary>
    /// <param name="host">Die Laufzeit.</param>
    public TimersViewModel(AppHost host) : base(host)
    {
        _ticker = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _ticker.Tick += OnTick;
        _ticker.Start();

        _ = LoadAsync();
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

    /// <summary>Das Ticket für den neuen Timer; leer für keines.</summary>
    [ObservableProperty]
    private string _newTicket = string.Empty;

    /// <summary>Gibt es etwas anzuzeigen?</summary>
    public bool IsEmpty => Timers.Count == 0;

    /// <summary>Holt die Timer neu.</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        if (Host.Composition is not { } composition)
        {
            Timers.Clear();
            Summary = "Ohne Einrichtung gibt es keine Timer zu holen. "
                + "Das Zahnrad unter „Verbindung“ öffnet den Assistenten.";
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
            await LoadAsync().ConfigureAwait(true);
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

        int ticketId = 0;
        string ticket = NewTicket.Trim().TrimStart('#');

        if (ticket.Length > 0
            && !int.TryParse(ticket, NumberStyles.None, CultureInfo.CurrentCulture, out ticketId))
        {
            Summary = $"„{NewTicket}“ ist keine Ticketnummer. Erwartet wird eine Zahl.";
            return;
        }

        IsBusy = true;
        try
        {
            _ = await composition.Timers.CreateAsync(title, ticketId).ConfigureAwait(true);

            NewTitle = string.Empty;
            NewTicket = string.Empty;

            await LoadAsync().ConfigureAwait(true);
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
    /// Löscht einen Timer.
    /// </summary>
    /// <remarks>
    /// Ohne Rückfrage an dieser Stelle, aber mit einer Schaltfläche, die zurückhaltend gestaltet
    /// ist: Ein Timer ist erfasste Arbeitszeit, und was hier verschwindet, ist in TANSS weg.
    /// Die Rückfrage stellt die Ansicht.
    /// </remarks>
    /// <param name="row">Die zu löschende Zeile.</param>
    [RelayCommand]
    private async Task DeleteAsync(TimerRow? row)
    {
        if (row is null || Host.Composition is not { } composition)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await composition.Timers.DeleteAsync(row.Id).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
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
    protected override void OnStatusUpdated(AppStatus status) => _ = LoadAsync();

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
