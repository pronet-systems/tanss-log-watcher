using System.Globalization;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using TanssLogWatcher.App.Runtime;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Die Fußzeile des Hauptfensters und der Hinweistext am Symbol im Infobereich.
/// </summary>
/// <remarks>
/// <para><b>Das ist die Anzeige, die im Betrieb allein sichtbar ist.</b> Im Normalfall läuft
/// das Werkzeug mit geschlossenem Fenster; was der Techniker dann sieht, ist der Hinweistext am
/// Symbol. Er muss deshalb dieselbe Frage beantworten wie die Fußzeile — läuft es, hängt etwas,
/// wie lange trägt das Token — und darf nicht bloß den Programmnamen wiederholen.</para>
///
/// <para><b>Ein Zustand, drei Quellen.</b> Betriebszustand, Sitzungszahl und Token kommen aus
/// drei verschiedenen Diensten. Zusammengesetzt wird hier und nur hier; jede Seite, die sich
/// ihren eigenen Satz zusammenstellte, zeigte früher oder später einen anderen.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class ShellViewModel : RuntimeViewModel
{
    /// <summary>Baut die Anzeige und meldet sich an allen drei Diensten an.</summary>
    /// <param name="host">Die Laufzeit.</param>
    public ShellViewModel(AppHost host) : base(host)
    {
        host.Sessions.ActiveSessionsChanged += OnSessionsChanged;
        host.Uploads.QueueChanged += OnQueueChanged;
        host.TokenRotation.TokenChanged += OnTokenChanged;

        _sessionCount = host.Sessions.ActiveSessions.Count;
        _pending = host.Uploads.Queue.Pending;

        TokenSnapshot token = host.TokenRotation.Token;
        _tokenReadable = token.IsReadable;
        _hasExpiry = token.ExpiresAt is not null;
        _tokenDays = double.IsFinite(token.DaysRemaining)
            ? (int)Math.Floor(token.DaysRemaining)
            : 0;
    }

    /// <summary>Wie viele Sitzungen gerade laufen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionsText))]
    [NotifyPropertyChangedFor(nameof(TrayToolTip))]
    private int _sessionCount;

    /// <summary>Wie viele Einträge auf den Versand warten.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueText))]
    [NotifyPropertyChangedFor(nameof(HasQueue))]
    [NotifyPropertyChangedFor(nameof(TrayToolTip))]
    private int _pending;

    /// <summary>Wie viele Tage das Token noch trägt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenText))]
    [NotifyPropertyChangedFor(nameof(TrayToolTip))]
    private int _tokenDays;

    /// <summary>Liess sich das Token lesen?</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenText))]
    private bool _tokenReadable;

    /// <summary>Trägt das Token überhaupt einen Ablaufanspruch?</summary>
    /// <remarks>
    /// Ohne ihn gibt es keine Restlaufzeit, und eine Zahl an dieser Stelle wäre bedeutungslos —
    /// vorher stand dort das Sättigungsartefakt „Token 2147483647 Tage“.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenText))]
    [NotifyPropertyChangedFor(nameof(TrayToolTip))]
    private bool _hasExpiry;

    /// <summary>Ist die Beobachtung angehalten?</summary>
    /// <remarks>
    /// Der Haken im Menü am Symbol. Er hält die Erkennung an, nicht den Versand: Was schon in
    /// der Warteschlange liegt, geht weiterhin hinaus.
    /// </remarks>
    [ObservableProperty]
    private bool _isPaused;

    /// <summary>Die kurze Beschriftung der Plakette, etwa „verbunden“.</summary>
    public string StateText => Status.State switch
    {
        RuntimeState.Working => Status.HasWarnings ? "verbunden, mit Warnung" : "verbunden",
        RuntimeState.Degraded => "gestört",
        _ => "nicht eingerichtet",
    };

    /// <summary>Die Basisadresse, oder ein Hinweis, dass noch keine eingerichtet ist.</summary>
    public string BaseUrlText => Host.Config?.Tanss.BaseUrl ?? "keine Konfiguration";

    /// <summary>Mitarbeitername und Kennung; solange nichts eingerichtet ist, ein Gedankenstrich.</summary>
    public string EmployeeText => Host.Config is { } config
        ? string.Create(CultureInfo.CurrentCulture, $"Mitarbeiter {config.Tanss.EmployeeId}")
        : "—";

    /// <summary>Wie viele Sitzungen laufen, in Worten.</summary>
    public string SessionsText => SessionCount == 0
        ? "keine laufende Sitzung"
        : Texts.Count(SessionCount, "Sitzung läuft", "Sitzungen laufen");

    /// <summary>Was in der Warteschlange liegt.</summary>
    public string QueueText => Texts.Count(Pending, "Eintrag wartet", "Einträge warten");

    /// <summary>Liegt überhaupt etwas in der Warteschlange?</summary>
    public bool HasQueue => Pending > 0;

    /// <summary>Die Restlaufzeit des Tokens.</summary>
    public string TokenText => !TokenReadable
        ? "kein Token"
        : !HasExpiry
            ? "Token ohne Ablauf"
            : TokenDays < 0
                ? "Token abgelaufen"
                : string.Create(CultureInfo.CurrentCulture, $"Token {TokenDays} Tage");

    /// <summary>
    /// Der Hinweistext am Symbol im Infobereich.
    /// </summary>
    /// <remarks>
    /// Windows schneidet diesen Text bei 127 Zeichen ab. Deshalb steht das Wichtigste vorn: der
    /// Zustand. Wer nur den Anfang sieht, weiss dann immer noch, ob etwas zu tun ist.
    /// </remarks>
    public string TrayToolTip
    {
        get
        {
            if (!IsConfigured)
            {
                return "TANSS Log-Watcher — nicht eingerichtet";
            }

            string head = Status.State == RuntimeState.Degraded
                ? "TANSS Log-Watcher — gestört"
                : "TANSS Log-Watcher";

            string queue = Pending > 0 ? $", {QueueText}" : string.Empty;
            return $"{head}: {SessionsText}{queue}, {TokenText}";
        }
    }

    /// <inheritdoc />
    protected override void OnStatusUpdated(AppStatus status)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(BaseUrlText));
        OnPropertyChanged(nameof(EmployeeText));
        OnPropertyChanged(nameof(TrayToolTip));
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Host.Sessions.ActiveSessionsChanged -= OnSessionsChanged;
            Host.Uploads.QueueChanged -= OnQueueChanged;
            Host.TokenRotation.TokenChanged -= OnTokenChanged;
        }

        base.Dispose(disposing);
    }

    partial void OnIsPausedChanged(bool value) => Host.Sessions.IsEnabled = !value;

    private void OnSessionsChanged(object? sender, IReadOnlyList<SessionSnapshot> sessions) =>
        SessionCount = sessions.Count;

    private void OnQueueChanged(object? sender, QueueSnapshot snapshot) => Pending = snapshot.Pending;

    private void OnTokenChanged(object? sender, TokenSnapshot token)
    {
        TokenReadable = token.IsReadable;
        HasExpiry = token.ExpiresAt is not null;

        // Ein Token ohne exp-Anspruch liefert unendlich viele Resttage; der Wurf auf int
        // saettigt das auf 2.147.483.647. In der Fusszeile stand dann "Token 2147483647 Tage".
        TokenDays = double.IsFinite(token.DaysRemaining)
            ? (int)Math.Floor(token.DaysRemaining)
            : 0;
    }
}
