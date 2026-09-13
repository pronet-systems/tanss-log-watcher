using System.Globalization;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly UpdateService? _updates;

    /// <summary>Baut die Anzeige und meldet sich an den Diensten an.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="updates">
    /// Die Prüfung auf neue Fassungen. Bleibt sie offen, zeigt die Fußzeile nichts dazu — für
    /// Prüfstände, die ohne Netz laufen sollen.
    /// </param>
    public ShellViewModel(AppHost host, UpdateService? updates = null) : base(host)
    {
        host.Sessions.ActiveSessionsChanged += OnSessionsChanged;
        host.Uploads.QueueChanged += OnQueueChanged;
        host.TokenRotation.TokenChanged += OnTokenChanged;

        _updates = updates;

        if (updates is not null)
        {
            updates.CheckCompleted += OnUpdateChecked;

            // Der Dienst prueft eine Minute nach dem Start und danach taeglich. Er kann also
            // schon fertig sein, bevor dieses Fenster zum ersten Mal aufgeht - dann ist das
            // Ereignis laengst gefallen. Deshalb hier gleich der Stand von jetzt.
            _updateAvailable = updates.State == UpdateCheckState.UpdateAvailable;
            _updateVersion = updates.Available?.Version.ToString() ?? string.Empty;
        }

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

    /// <summary>
    /// Was läuft, aber nicht in Ordnung ist — der Inhalt der Plakette beim Anklicken.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Befund, aus dem das hier entstanden ist.</b> Die Plakette sagte „verbunden,
    /// mit Warnung“ — und es gab in der ganzen Oberfläche keinen Ort, an dem stand, welche.
    /// Die Warnungen wurden erhoben, in den Zustand gelegt und nie gezeigt. Eine Anzeige, die
    /// auf etwas hinweist, das niemand nachlesen kann, ist schlimmer als keine: Sie
    /// beunruhigt, ohne zu helfen.</para>
    /// <para>Jede Warnung trägt Titel, Grund und Vorschlag — Hausregel 3 — und alle drei
    /// gehören auf den Schirm. Der Vorschlag ist der einzige Teil, mit dem jemand etwas
    /// anfangen kann.</para>
    /// </remarks>
    public IReadOnlyList<RuntimeWarning> Warnings => Status.Warnings;

    /// <summary>Gibt es überhaupt eine Warnung?</summary>
    public bool HasWarnings => Status.HasWarnings;

    /// <summary>
    /// Was steht in der Auskunft, wenn es nichts zu warnen gibt?
    /// </summary>
    /// <remarks>
    /// Ein Klick, der nichts tut, sieht aus wie ein Fehler des Werkzeugs. Deshalb antwortet die
    /// Plakette auch dann, wenn alles in Ordnung ist — dann eben mit genau diesem Satz.
    /// </remarks>
    public string NoWarningText => Status.State switch
    {
        RuntimeState.Working => "Keine Warnung. Verbindung, Token und Konfiguration sind in "
                                + "Ordnung.",
        RuntimeState.Degraded => Status.Reason,
        _ => "Noch nicht eingerichtet — das Zahnrad unter „Verbindung“ öffnet den Assistenten.",
    };

    /// <summary>Steht die Auskunft zur Plakette gerade offen?</summary>
    [ObservableProperty]
    private bool _isWarningsOpen;

    /// <summary>Liegt eine neuere Fassung vor?</summary>
    /// <remarks>
    /// <para><b>Warum das in die Fußzeile gehört.</b> Geprüft wurde schon immer — eine Minute
    /// nach dem Start und danach täglich. Gesagt wurde es nur auf der Seite „Verbindung“, und
    /// die öffnet im Betrieb niemand: Das Werkzeug läuft mit geschlossenem Fenster. Eine
    /// Aktualisierung, von der man nur erfährt, wenn man ohnehin nachsieht, ist keine
    /// Benachrichtigung.</para>
    /// <para>Die Fußzeile ist der richtige Ort und nicht ein Fenster, das sich aufdrängt: Eine
    /// neue Fassung ist keine Störung, sondern eine Auskunft. Sie darf warten, bis jemand
    /// hinsieht — sie darf nur nicht unsichtbar sein.</para>
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateText))]
    private bool _updateAvailable;

    /// <summary>Die Nummer der neuen Fassung.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateText))]
    private string _updateVersion = string.Empty;

    /// <summary>Was in der Fußzeile steht, wenn eine neue Fassung vorliegt.</summary>
    public string UpdateText => UpdateVersion.Length == 0
        ? "Aktualisierung verfügbar"
        : string.Create(CultureInfo.CurrentCulture, $"Version {UpdateVersion} verfügbar");

    /// <summary>Klappt die Auskunft zur Plakette auf und wieder zu.</summary>
    /// <remarks>
    /// Ein Umschalter und nicht bloss ein Aufklappen: Ein zweiter Klick auf dieselbe Stelle
    /// soll wieder schliessen. Sonst muss man daneben treffen, um etwas loszuwerden, das man
    /// selbst geoeffnet hat.
    /// </remarks>
    [RelayCommand]
    private void ToggleWarnings() => IsWarningsOpen = !IsWarningsOpen;

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

        // Die Auskunft der Plakette haengt am Zustand und aendert sich mit ihm. Ohne diese drei
        // Zeilen stuende beim naechsten Oeffnen die Warnung von vorhin da.
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(NoWarningText));
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Host.Sessions.ActiveSessionsChanged -= OnSessionsChanged;
            Host.Uploads.QueueChanged -= OnQueueChanged;
            Host.TokenRotation.TokenChanged -= OnTokenChanged;

            if (_updates is not null)
            {
                _updates.CheckCompleted -= OnUpdateChecked;
            }
        }

        base.Dispose(disposing);
    }

    partial void OnIsPausedChanged(bool value) => Host.Sessions.IsEnabled = !value;

    /// <summary>Übernimmt das Ergebnis einer Prüfung auf neue Fassungen.</summary>
    /// <remarks>
    /// <b>Der Dienst löst auf einem Hintergrundstrang aus</b> — er sagt das selbst. Die
    /// Zuweisung an eine gebundene Eigenschaft muss deshalb über den Strang der Oberfläche
    /// laufen; dafür gibt es <see cref="RuntimeNotifier"/>, den dieselbe Laufzeit schon für
    /// jede andere Meldung benutzt.
    /// </remarks>
    /// <param name="sender">Der Dienst.</param>
    /// <param name="state">Wie die Prüfung ausgegangen ist.</param>
    private void OnUpdateChecked(object? sender, UpdateCheckState state) =>
        Host.Notifier.Post(() =>
        {
            UpdateAvailable = state == UpdateCheckState.UpdateAvailable;
            UpdateVersion = _updates?.Available?.Version.ToString() ?? string.Empty;
        });

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
