using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.App.Runtime;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Die Sitzungsseite: was gerade läuft, seit wann und worauf es bucht.
/// </summary>
/// <remarks>
/// <para><b>Die Liste wird ersetzt, nicht fortgeschrieben.</b> Die Beobachtung meldet bei jedem
/// Takt den vollständigen Stand. Zeilenweise abzugleichen, was dazugekommen und was weggefallen
/// ist, wäre eine zweite Zustandsmaschine neben der, die es schon gibt — mit der Aussicht, dass
/// beide irgendwann Verschiedenes für richtig halten.</para>
///
/// <para><b>Die Dauer tickt im eigenen Takt.</b> Die Beobachtung meldet in ihrem eigenen; eine
/// Dauer, die nur dann weiterspränge, sähe aus wie eine stehengebliebene Anzeige. Der Takt hier
/// rechnet bloß die vorhandenen Zeilen neu und fragt nichts ab.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class SessionsViewModel : RuntimeViewModel
{
    private readonly SystemLookup _systems;
    private readonly DispatcherTimer _ticker;

    /// <summary>
    /// Wann der Aufwand zuletzt in den Satz über der Liste übernommen wurde.
    /// </summary>
    /// <remarks>
    /// Im Sekundentakt käme jede Sekunde eine neue Zahl — „29 ms“, „31 ms“, „28 ms“ —, und ein
    /// Satz, in dem ohne Zutun dauernd etwas zappelt, zieht den Blick auf die unwichtigste
    /// Stelle der Seite. Alle fünf Sekunden reicht für die Frage, die der Wert beantwortet.
    /// </remarks>
    private DateTimeOffset _cadenceShownAt = DateTimeOffset.MinValue;

    /// <summary>Baut die Seite und meldet sich an der Beobachtung an.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="systems">Der gemeinsame Nachschlag für Typnamen und Farben.</param>
    public SessionsViewModel(AppHost host, SystemLookup systems) : base(host)
    {
        ArgumentNullException.ThrowIfNull(systems);

        _systems = systems;
        _isWatching = host.Sessions.IsEnabled;

        host.Sessions.ActiveSessionsChanged += OnActiveSessionsChanged;
        host.Sessions.ActivityChanged += OnActivityChanged;

        _ticker = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _ticker.Tick += OnTick;
        _ticker.Start();

        Rebuild(host.Sessions.ActiveSessions);
        _ = InitializeAsync();
    }

    /// <summary>Die laufenden Sitzungen.</summary>
    public ObservableCollection<SessionRow> Sessions { get; } = [];

    /// <summary>Läuft die Beobachtung, oder ist sie angehalten?</summary>
    /// <remarks>
    /// Angehalten heisst: Es werden keine neuen Sitzungen mehr erkannt. Was bereits in der
    /// Warteschlange liegt, geht trotzdem hinaus — das Anhalten betrifft das Beobachten, nicht
    /// das Senden.
    /// </remarks>
    [ObservableProperty]
    private bool _isWatching;

    /// <summary>Läuft die Erkennung im Trockenlauf?</summary>
    public bool IsDryRun => Host.Sessions.IsDryRun;

    /// <summary>Warum der Trockenlauf läuft; <c>null</c>, wenn er es nicht tut.</summary>
    public string? DryRunReason => Host.Sessions.DryRunReason;

    /// <summary>Gibt es überhaupt nichts anzuzeigen?</summary>
    public bool IsEmpty => Sessions.Count == 0;

    /// <summary>
    /// Der Satz über der Liste: in welchem Takt geprüft wird und was ein Durchlauf kostet.
    /// </summary>
    /// <remarks>
    /// <para><b>Beide Zahlen kommen von dort, wo sie entstehen.</b> Hier stand fest eingebaut
    /// „Alle 10 Sekunden geprüft“. Das war schon falsch, sobald jemand
    /// <c>watcher.poll_interval_seconds</c> anfasste — und niemand hätte es bemerkt, weil der
    /// Satz ja weiterhin dastand.</para>
    /// <para>Die gemessene Dauer steht daneben, weil sie die einzige Antwort auf die Frage ist,
    /// ob der gewählte Takt auf diesem Rechner trägt.</para>
    /// </remarks>
    public string CadenceText
    {
        get
        {
            TimeSpan interval = Host.Config is { } config
                ? TimeSpan.FromSeconds(config.Watcher.PollIntervalSeconds)
                : TimeSpan.Zero;

            string cadence = interval <= TimeSpan.Zero
                ? "Noch nicht eingerichtet — es wird nichts geprüft."
                : interval == TimeSpan.FromSeconds(1)
                    ? "Jede Sekunde geprüft."
                    : $"Alle {Texts.Seconds(interval)} geprüft.";

            string cost = Host.Sessions.Activity.LastCycle is { } last
                ? $" Der letzte Durchlauf dauerte {Texts.Millis(last)}."
                : string.Empty;

            return cadence + cost
                + " Eine Sitzung endet, wenn das Fenster schließt — dann wird nach Kommentar "
                + "und Ticket gefragt.";
        }
    }

    /// <summary>
    /// Holt den Stand neu.
    /// </summary>
    /// <remarks>
    /// Bewusst nur ein Neulesen und kein erzwungener Takt der Zustandsmaschine. Ein Takt
    /// ausserhalb der Reihe entschiede über Beginn und Ende von Sitzungen — und zwar zu einem
    /// Zeitpunkt, den der Benutzer durch einen Klick bestimmt hat. Beobachtet wird nach der
    /// Uhr, nicht nach der Maus.
    /// </remarks>
    [RelayCommand]
    private void Refresh()
    {
        Rebuild(Host.Sessions.ActiveSessions);
        Tick();
    }

    /// <inheritdoc />
    protected override void OnStatusUpdated(AppStatus status)
    {
        OnPropertyChanged(nameof(IsDryRun));
        OnPropertyChanged(nameof(DryRunReason));
        OnPropertyChanged(nameof(CadenceText));

        // Mit einer frisch geladenen Konfiguration stehen auch die Anbindungen wieder zur
        // Verfuegung - vorher gab es keinen Zugang, ueber den sie zu holen gewesen waeren.
        _ = InitializeAsync();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ticker.Stop();
            _ticker.Tick -= OnTick;
            Host.Sessions.ActiveSessionsChanged -= OnActiveSessionsChanged;
            Host.Sessions.ActivityChanged -= OnActivityChanged;
        }

        base.Dispose(disposing);
    }

    partial void OnIsWatchingChanged(bool value) => Host.Sessions.IsEnabled = value;

    /// <summary>
    /// Übernimmt den gemessenen Aufwand des letzten Durchlaufs in den Satz über der Liste.
    /// </summary>
    /// <remarks>
    /// Der Dienst meldet zweimal je Takt — „Arbeitet“ und danach den Stand. Nur der zweite
    /// trägt einen Messwert; der erste würde die Anzeige bei jedem Takt kurz leeren.
    /// </remarks>
    private void OnActivityChanged(object? sender, Services.ServiceActivity activity)
    {
        if (activity.LastCycle is null)
        {
            return;
        }

        DateTimeOffset now = Host.Clock.GetLocalNow();

        if (now - _cadenceShownAt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _cadenceShownAt = now;
        OnPropertyChanged(nameof(CadenceText));
    }

    private async Task InitializeAsync()
    {
        if (await _systems.RefreshAsync(Host.Composition).ConfigureAwait(true))
        {
            // Namen und Farben stehen erst jetzt fest; die Zeilen tragen sie fest eingebaut.
            Rebuild(Host.Sessions.ActiveSessions);
        }
    }

    private void OnActiveSessionsChanged(object? sender, IReadOnlyList<SessionSnapshot> sessions) =>
        Rebuild(sessions);

    private void OnTick(object? sender, EventArgs e) => Tick();

    private void Tick()
    {
        DateTimeOffset now = Host.Clock.GetLocalNow();

        foreach (SessionRow row in Sessions)
        {
            row.Refresh(now);
        }
    }

    private void Rebuild(IReadOnlyList<SessionSnapshot> sessions)
    {
        DateTimeOffset now = Host.Clock.GetLocalNow();

        Sessions.Clear();

        foreach (SessionSnapshot session in sessions.OrderByDescending(s => s.StartedAt))
        {
            Sessions.Add(new SessionRow(
                session,
                _systems.NameFor(session.RemoteSupportTypeId),
                _systems.ColorFor(session.RemoteSupportTypeId),
                now));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}
