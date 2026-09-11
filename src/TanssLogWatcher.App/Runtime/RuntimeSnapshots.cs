using System.Globalization;
using TanssLogWatcher.Monitoring.Model;
using TanssLogWatcher.Storage.Queue;

namespace TanssLogWatcher.App.Runtime;

/// <summary>
/// Eine Sitzung, so wie die Oberfläche sie sieht.
/// </summary>
/// <remarks>
/// <para><b>Bewusst eine Abschrift und nicht <see cref="WatchedSession"/> selbst.</b> Jene
/// Klasse ist veränderlich und gehört der Zustandsmaschine: Ihr <c>Destination</c> und ihr
/// <c>EndedAt</c> werden im nächsten Takt fortgeschrieben. Eine Liste in der Oberfläche, die
/// solche Stücke hielte, änderte sich ohne Benachrichtigung unter der Anzeige weg — und
/// zeigte nach dem Ende der Sitzung deren Werte von jetzt, nicht die von damals.</para>
///
/// <para>Die Abschrift trägt deshalb auch schon die Zuordnung: Welcher Fernwartungstyp gilt
/// und wie der Kommentar lautet, entscheidet sich beim Abschluss der Sitzung. Später
/// nachgerechnet hinge beides an Einstellungen, die sich seither geändert haben könnten.</para>
/// </remarks>
public sealed record SessionSnapshot
{
    /// <summary>Unsere Sitzungskennung; zugleich die <c>remoteMaintenanceId</c> bei TANSS.</summary>
    public required string SessionId { get; init; }

    /// <summary>Schlüssel der Beobachtungsregel, etwa <c>mstsc</c>.</summary>
    public required string ProfileKey { get; init; }

    /// <summary>Der sprechende Name des Profils aus dem Katalog, etwa „Remotedesktopverbindung“.</summary>
    public required string ProfileName { get; init; }

    /// <summary>Die Gegenstelle — Rechnername, Adresse oder Kennung.</summary>
    public required string Destination { get; init; }

    /// <summary>Prozesskennung der beobachteten Anwendung.</summary>
    public int ProcessId { get; init; }

    /// <summary>Beginn der Sitzung.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Ende der Sitzung; <c>null</c>, solange sie läuft.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Der Fernwartungstyp in TANSS, oder <see cref="MonitoringSetting.NotMonitored"/>.</summary>
    public int RemoteSupportTypeId { get; init; } = MonitoringSetting.NotMonitored;

    /// <summary>Der Kommentar, den TANSS an der Fernwartung zeigt.</summary>
    public string Comment { get; init; } = string.Empty;

    /// <summary>Läuft die Sitzung noch?</summary>
    public bool IsRunning => EndedAt is null;

    /// <summary>
    /// Ist das Profil auf einen Fernwartungstyp abgebildet?
    /// </summary>
    /// <remarks>
    /// Ohne Zuordnung wird die Sitzung erkannt und angezeigt, aber nirgends gebucht — es gäbe
    /// keinen Typ, auf den. Die Anzeige soll das sagen, statt eine gebuchte Sitzung
    /// vorzutäuschen.
    /// </remarks>
    public bool IsMapped => RemoteSupportTypeId != MonitoringSetting.NotMonitored;

    /// <summary>Die Dauer zum Bezugszeitpunkt; bei beendeten Sitzungen die endgültige.</summary>
    /// <param name="now">Der Bezugszeitpunkt, üblicherweise jetzt.</param>
    public TimeSpan Duration(DateTimeOffset now) => (EndedAt ?? now) - StartedAt;
}

/// <summary>Was mit einer abgeschlossenen Sitzung geschehen ist.</summary>
public enum SessionDisposition
{
    /// <summary>In die Warteschlange aufgenommen; geht mit dem nächsten Sendelauf hinaus.</summary>
    Enqueued,

    /// <summary>Die Kennung stand bereits in der Warteschlange und wurde nicht erneut aufgenommen.</summary>
    AlreadyQueued,

    /// <summary>Keine Zuordnung auf einen Fernwartungstyp; es gäbe nichts, worauf gebucht würde.</summary>
    NotMapped,

    /// <summary>Trockenlauf: erkannt und angezeigt, aber nichts eingereiht.</summary>
    DryRun,

    /// <summary>Das Einreihen selbst ist fehlgeschlagen. Der Grund steht dabei.</summary>
    Failed,
}

/// <summary>Eine abgeschlossene Sitzung samt dem, was mit ihr geschehen ist.</summary>
/// <param name="Session">Die Sitzung.</param>
/// <param name="Disposition">Wohin sie gegangen ist.</param>
/// <param name="Reason">Der Klartextgrund — auch im Erfolgsfall, damit die Anzeige nie rät.</param>
public sealed record SessionClosed(SessionSnapshot Session, SessionDisposition Disposition,
                                   string Reason);

/// <summary>Der Stand der Warteschlange in Zahlen.</summary>
/// <remarks>
/// <para><see cref="Pending"/> und Geschwister zählen über SQL und sehen jede Zeile;
/// <see cref="PendingItems"/> überspringt die unlesbaren. Beides steht nebeneinander, weil es
/// nur bis zur Obergrenze der Abfrage übereinstimmt — eine Anzeige „ausstehend: 4“ neben drei
/// Einträgen ist sonst nicht zu erklären.</para>
/// </remarks>
public sealed record QueueSnapshot
{
    /// <summary>Der Stand, wenn sich die Warteschlange gar nicht öffnen liess.</summary>
    /// <remarks>
    /// Ausdrücklich ein eigener Wert und nicht schlicht „alles null“: Eine leere Warteschlange
    /// und eine unlesbare Warteschlange sehen in Zahlen gleich aus, bedeuten aber das
    /// Gegenteil. <see cref="IsAvailable"/> trennt sie.
    /// </remarks>
    public static QueueSnapshot Unavailable { get; } = new() { IsAvailable = false };

    /// <summary>Liess sich die Warteschlange lesen?</summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>Wartet auf den nächsten fälligen Versuch.</summary>
    public int Pending { get; init; }

    /// <summary>Gerade unterwegs.</summary>
    public int Sending { get; init; }

    /// <summary>Aufgegeben; wird nicht mehr von selbst versucht.</summary>
    public int Failed { get; init; }

    /// <summary>Bei TANSS angekommen und als Beleg aufbewahrt.</summary>
    public int Done { get; init; }

    /// <summary>Zeilen, deren Nutzlast sich nicht mehr lesen lässt.</summary>
    public int Unreadable { get; init; }

    /// <summary>Die ausstehenden Einträge, jüngste zuerst.</summary>
    public IReadOnlyList<QueuedUpload> PendingItems { get; init; } = [];

    /// <summary>Die aufgegebenen Einträge, jüngste zuerst.</summary>
    public IReadOnlyList<QueuedUpload> FailedItems { get; init; } = [];

    /// <summary>Der Satz zu den unlesbaren Zeilen, oder <c>null</c>, wenn es keine gibt.</summary>
    public string? UnreadableNotice { get; init; }

    /// <summary>Liegt überhaupt etwas an?</summary>
    public bool HasWork => Pending + Sending + Failed > 0;

    /// <summary>Wann dieser Stand erhoben wurde.</summary>
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

/// <summary>Der Ausgang eines einzelnen Sendeversuchs.</summary>
public enum UploadOutcome
{
    /// <summary>Bei TANSS angelegt.</summary>
    Uploaded,

    /// <summary>Stand schon dort; abgehakt statt ein zweites Mal gesendet.</summary>
    AlreadyPresent,

    /// <summary>Zurückgestellt; ein späterer Versuch kann es heilen.</summary>
    Deferred,

    /// <summary>
    /// Zurückgestellt, weil die Existenzprüfung selbst fehlgeschlagen ist.
    /// </summary>
    /// <remarks>
    /// Der wichtigste Ausgang dieser ganzen Aufzählung. „Unbekannt“ heisst <b>nicht</b> „nicht
    /// vorhanden“: Zu senden hiesse, die Dublette in Kauf zu nehmen, die TANSS nicht abfängt
    /// und die nur noch per direktem Datenbankzugriff zu entfernen wäre.
    /// </remarks>
    OutcomeUnknown,

    /// <summary>Aufgegeben; kein Warten heilt das.</summary>
    GivenUp,
}

/// <summary>Was mit einem Warteschlangeneintrag geschehen ist.</summary>
/// <param name="RemoteMaintenanceId">Der Eintrag.</param>
/// <param name="Outcome">Der Ausgang.</param>
/// <param name="Reason">Der Klartextgrund, bereits geschwärzt.</param>
/// <param name="TanssSupportId">Die von TANSS vergebene Kennung, falls angelegt.</param>
public sealed record UploadReport(string RemoteMaintenanceId, UploadOutcome Outcome,
                                  string Reason, int? TanssSupportId = null);

/// <summary>Das Ergebnis eines Sendelaufs.</summary>
/// <remarks>
/// Als Ganzes und nicht als Folge einzelner Meldungen, damit eine Schaltfläche „Jetzt senden“
/// in einem Satz sagen kann, was sie bewirkt hat.
/// </remarks>
public sealed record UploadRunResult
{
    /// <summary>Ein Lauf, der gar nicht stattgefunden hat.</summary>
    public static UploadRunResult None { get; } = new();

    /// <summary>Wie viele Einträge aus „unterwegs“ zurückgeholt wurden.</summary>
    public int Requeued { get; init; }

    /// <summary>Wie viele Einträge versucht wurden.</summary>
    public int Attempted { get; init; }

    /// <summary>Wie viele bei TANSS angelegt wurden.</summary>
    public int Uploaded { get; init; }

    /// <summary>Wie viele schon dort standen.</summary>
    public int AlreadyPresent { get; init; }

    /// <summary>Wie viele zurückgestellt wurden.</summary>
    public int Deferred { get; init; }

    /// <summary>Wie viele aufgegeben wurden.</summary>
    public int GivenUp { get; init; }

    /// <summary>Die Einzelmeldungen, in der Reihenfolge des Laufs.</summary>
    public IReadOnlyList<UploadReport> Reports { get; init; } = [];

    /// <summary>Ein Satz für die Statuszeile.</summary>
    public string Summary => Attempted == 0
        ? (Requeued > 0
            ? string.Create(CultureInfo.CurrentCulture,
                $"Nichts fällig. {Requeued} Eintrag/Einträge wurden zurückgeholt.")
            : "Nichts fällig.")
        : string.Create(CultureInfo.CurrentCulture,
            $"{Attempted} versucht: {Uploaded} hochgeladen, {AlreadyPresent} standen bereits "
            + $"in TANSS, {Deferred} zurückgestellt, {GivenUp} aufgegeben.");
}

/// <summary>Der Stand des Arbeitstokens.</summary>
/// <remarks>
/// Gelesen, nicht geprüft: Die Signatur prüft TANSS, und der Schlüssel dazu liegt dort. Alles
/// hier dient der Betriebsführung — wann läuft es ab, lohnt eine Erneuerung — und niemals
/// einer Zugriffsentscheidung.
/// </remarks>
public sealed record TokenSnapshot
{
    /// <summary>Liess sich das Token lesen und entsiegeln?</summary>
    public bool IsReadable { get; init; }

    /// <summary>Wann es abläuft; <c>null</c>, wenn es keinen <c>exp</c>-Anspruch trägt.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Wann es ausgestellt wurde.</summary>
    public DateTimeOffset? IssuedAt { get; init; }

    /// <summary>Verbleibende Tage; negativ, wenn bereits abgelaufen.</summary>
    public double DaysRemaining { get; init; }

    /// <summary>Ab wie vielen Resttagen erneuert wird (<c>tanss.rotate_before_days</c>).</summary>
    public int RotateBeforeDays { get; init; }

    /// <summary>
    /// Darf der Mitarbeiter Token prägen? <c>null</c> heisst: noch nicht gefragt.
    /// </summary>
    /// <remarks>
    /// Die Antwort kostet einen Netzaufruf (<c>isForTesting=true</c>, folgenlos und nicht
    /// protokolliert). <c>null</c> ist deshalb ein eigener Wert und nicht <c>false</c>: „weiss
    /// ich nicht“ und „darf nicht“ führen zu verschiedenen Handgriffen.
    /// </remarks>
    public bool? CanRotate { get; init; }

    /// <summary>Der Klartextgrund, wenn etwas nicht stimmt; sonst <c>null</c>.</summary>
    public string? Problem { get; init; }

    /// <summary>Ist das Token abgelaufen?</summary>
    public bool IsExpired => IsReadable && ExpiresAt is { } expiry && expiry <= DateTimeOffset.Now;

    /// <summary>Ist die Erneuerung fällig?</summary>
    public bool IsDue => IsReadable && ExpiresAt is not null
        && DaysRemaining <= RotateBeforeDays;

    /// <summary>Wann die Erneuerung fällig wird.</summary>
    public DateTimeOffset? DueAt =>
        ExpiresAt is { } expiry ? expiry - TimeSpan.FromDays(RotateBeforeDays) : null;
}
