using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Monitoring.Model;
using TanssLogWatcher.Storage.Queue;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Eine Sitzung, wie eine Zeile der Sitzungsliste sie zeigt.
/// </summary>
/// <remarks>
/// <para><b>Ein veränderliches Stück, obwohl <see cref="SessionSnapshot"/> unveränderlich
/// ist.</b> Der Grund ist allein die Dauer: Sie tickt. Würde für jede Sekunde die ganze Zeile
/// ersetzt, verlöre die Liste bei jedem Takt Auswahl und Bildlaufposition — bei einer
/// Anzeige, die im Sekundentakt läuft, ist das unbenutzbar.</para>
/// <para>Alles andere kommt aus dem Abbild und ändert sich nicht mehr: Was beim Abschluss
/// galt, gilt für diese Zeile.</para>
/// </remarks>
public sealed partial class SessionRow : ObservableObject
{
    private readonly SessionSnapshot _snapshot;

    /// <summary>Übernimmt ein Abbild und rechnet die Dauer zum Bezugszeitpunkt aus.</summary>
    /// <param name="snapshot">Das Abbild der Sitzung.</param>
    /// <param name="typeName">Der sprechende Name des Fernwartungstyps, falls bekannt.</param>
    /// <param name="color">Die Farbe der Anbindung als Hexwert ohne Raute.</param>
    /// <param name="now">Der Bezugszeitpunkt für die erste Dauer.</param>
    public SessionRow(SessionSnapshot snapshot, string? typeName, string? color,
                      DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _snapshot = snapshot;
        Color = color ?? string.Empty;

        // Ohne Zuordnung steht hier ausdruecklich ein Satz und keine Zahl: Eine "-1" in der
        // Spalte FERNWARTUNGSTYP liest sich wie ein Fehler, gemeint ist aber eine bewusste
        // Einstellung.
        TypeText = !snapshot.IsMapped
            ? "nicht überwacht"
            : typeName ?? string.Create(CultureInfo.CurrentCulture, $"Typ {snapshot.RemoteSupportTypeId}");

        _duration = Texts.Ticking(snapshot.Duration(now));
    }

    /// <summary>Unsere Sitzungskennung; zugleich die <c>remoteMaintenanceId</c> bei TANSS.</summary>
    public string SessionId => _snapshot.SessionId;

    /// <summary>Die Gegenstelle — Rechnername, Adresse oder Kennung.</summary>
    public string Destination => string.IsNullOrWhiteSpace(_snapshot.Destination)
        ? "Kein Ziel ermittelt"
        : _snapshot.Destination;

    /// <summary>Der sprechende Name des Profils, etwa „Remotedesktopverbindung“.</summary>
    public string Application => _snapshot.ProfileName;

    /// <summary>Der Fernwartungstyp im Klartext.</summary>
    public string TypeText { get; }

    /// <summary>Die Farbe der Anbindung als Hexwert ohne Raute; leer, wenn unbekannt.</summary>
    public string Color { get; }

    /// <summary>Der Beginn als Uhrzeit.</summary>
    public string StartText => Texts.Clock(_snapshot.StartedAt);

    /// <summary>Läuft die Sitzung noch?</summary>
    public bool IsRunning => _snapshot.IsRunning;

    /// <summary>Ist das Profil auf einen Fernwartungstyp abgebildet?</summary>
    public bool IsMapped => _snapshot.IsMapped;

    /// <summary>Der Zustand als Plakettentext.</summary>
    public string StateText => _snapshot.IsRunning ? "läuft" : "beendet";

    /// <summary>Die Dauer; tickt, solange die Sitzung läuft.</summary>
    [ObservableProperty]
    private string _duration;

    /// <summary>Rechnet die Dauer neu — einmal je Sekunde aus dem Seitenmodell.</summary>
    /// <param name="now">Der Bezugszeitpunkt.</param>
    public void Refresh(DateTimeOffset now)
    {
        if (_snapshot.IsRunning)
        {
            Duration = Texts.Ticking(_snapshot.Duration(now));
        }
    }
}

/// <summary>Ein Warteschlangeneintrag, wie eine Zeile ihn zeigt.</summary>
/// <remarks>
/// Die Beschreibung entsteht aus der Nutzlast und nicht aus der Sitzung: Der Eintrag überlebt
/// den Neustart, die Sitzung nicht. Was hier steht, muss deshalb aus dem lesbar sein, was in
/// <c>state.db</c> liegt.
/// </remarks>
public sealed record QueueRow
{
    /// <summary>Baut eine Zeile aus einem Warteschlangeneintrag.</summary>
    /// <param name="item">Der Eintrag.</param>
    /// <param name="typeName">Der sprechende Name des Fernwartungstyps, falls bekannt.</param>
    /// <param name="now">Der Bezugszeitpunkt für „wartet bis“.</param>
    public QueueRow(QueuedUpload item, string? typeName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);

        RemoteMaintenanceId = item.RemoteMaintenanceId;
        LastError = item.LastError ?? string.Empty;
        CreatedText = Texts.Moment(item.CreatedAt);
        AttemptsText = item.Attempts == 0 ? "—" : Texts.Number(item.Attempts);

        string target = string.IsNullOrWhiteSpace(item.Payload.DeviceName)
            ? item.Payload.UserName
            : item.Payload.DeviceName;

        string label = typeName ?? string.Create(CultureInfo.CurrentCulture,
            $"Fernwartungstyp {item.Payload.TypeId}");

        Description = string.IsNullOrWhiteSpace(target) ? label : $"{label} ({target})";

        // Der unbekannte Ausgang ist ein eigener Zustand und nicht bloss "Wiederholung":
        // Vor dem naechsten Versuch wird nachgefragt, statt blind zu senden - TANSS erkennt
        // Dubletten nicht selbst.
        StateText = item.OutcomeUnknown
            ? "Ausgang unbekannt"
            : item.State switch
            {
                // "wartet auf Entscheidung" und nicht bloss "wartet": Der Unterschied ist der
                // Grund, warum diese Zeile nicht von selbst hinausgeht - es fehlt eine Antwort
                // und nicht die Zeit.
                QueueState.Pending when item.AwaitingDecision => "wartet auf Entscheidung",
                QueueState.Pending => item.Attempts == 0 ? "wartet" : "Wiederholung",
                QueueState.Sending => "unterwegs",
                QueueState.Done => "gesendet",
                QueueState.Failed => "aufgegeben",
                _ => item.State.ToString(),
            };

        // Der Grund, warum "Jetzt senden" scheinbar nichts tat: Der Eintrag stand als
        // "ausstehend" da, war aber zurueckgehalten - und nirgends stand, warum.
        IsHeld = item.State == QueueState.Pending
                 && (item.AwaitingDecision || item.NextAttemptAt > now);

        HoldText = IsHeld
            ? (item.AwaitingDecision
                ? "Wartet auf eine Entscheidung — ohne sie geht diese Sitzung NICHT nach TANSS, "
                  + "und zwar unbefristet. Drei Wege hinaus: „Ändern“ holt den Abschlussdialog "
                  + "zurück, „Jetzt senden“ schickt sie mit der automatischen Beschreibung, und "
                  + "beim nächsten Start wird der Dialog ohnehin erneut vorgelegt."
                : $"Rückstau nach einem Fehlversuch, nächster Versuch um "
                  + $"{Texts.Clock(item.NextAttemptAt.ToLocalTime())}. „Jetzt senden“ zieht ihn vor.")
            : string.Empty;
    }

    /// <summary>Die Kennung des Eintrags.</summary>
    public string RemoteMaintenanceId { get; }

    /// <summary>Was gesendet werden soll, in einem Satz.</summary>
    public string Description { get; }

    /// <summary>Der letzte Fehler; leer, wenn es keinen gab.</summary>
    public string LastError { get; }

    /// <summary>Wann der Eintrag entstanden ist.</summary>
    public string CreatedText { get; }

    /// <summary>Wie oft schon versucht wurde.</summary>
    public string AttemptsText { get; }

    /// <summary>Der Zustand als Plakettentext.</summary>
    public string StateText { get; }

    /// <summary>Wird dieser Eintrag gerade zurückgehalten?</summary>
    public bool IsHeld { get; }

    /// <summary>Warum und bis wann er zurückgehalten wird; leer, wenn er es nicht wird.</summary>
    public string HoldText { get; }
}

/// <summary>Ein Timer aus TANSS, wie eine Zeile ihn zeigt.</summary>
/// <remarks>
/// <para><b>Veränderlich, weil ein laufender Timer tickt.</b> Wie bei
/// <see cref="SessionRow"/>: Würde für jede Sekunde die ganze Zeile ersetzt, verlöre die Liste
/// bei jedem Takt Auswahl und Bildlaufposition.</para>
/// <para><b>Gerechnet wird nicht hier.</b> <see cref="TanssTimer.IsRunning"/> und
/// <see cref="TanssTimer.Elapsed"/> gehören dem Modell und rechnen in TANSS-Sekunden; eine
/// zweite Fassung davon in der Oberfläche liefe früher oder später auseinander — und zwar um
/// genau den laufenden Abschnitt, der die Anzeige überhaupt interessant macht.</para>
/// </remarks>
public sealed partial class TimerRow : ObservableObject
{
    private readonly TanssTimer _timer;

    /// <summary>Baut eine Zeile aus einem Timer.</summary>
    /// <param name="timer">Der Timer aus TANSS.</param>
    /// <param name="now">Der Bezugszeitpunkt für die erste Dauer.</param>
    public TimerRow(TanssTimer timer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(timer);

        _timer = timer;
        Id = timer.Id;
        Title = string.IsNullOrWhiteSpace(timer.Title) ? "Ohne Titel" : timer.Title;
        TicketText = timer.TicketId == 0
            ? "kein Ticket"
            : string.Create(CultureInfo.CurrentCulture, $"#{timer.TicketId}");

        _elapsedText = Format(timer.Elapsed(now));
    }

    /// <summary>Die Kennung in TANSS.</summary>
    public int Id { get; }

    /// <summary>Der Titel.</summary>
    public string Title { get; }

    /// <summary>Das zugehörige Ticket, oder „kein Ticket“.</summary>
    public string TicketText { get; }

    /// <summary>Läuft der Timer?</summary>
    public bool IsRunning => _timer.IsRunning;

    /// <summary>Der Zustand als Plakettentext.</summary>
    public string StateText => IsRunning ? "läuft" : "angehalten";

    /// <summary>Die Beschriftung der Umschaltfläche.</summary>
    public string ToggleText => IsRunning ? "Anhalten" : "Starten";

    /// <summary>Die bisher erfasste Zeit als <c>hh:mm:ss</c>; tickt, solange der Timer läuft.</summary>
    [ObservableProperty]
    private string _elapsedText;

    /// <summary>Rechnet die Dauer neu — einmal je Sekunde aus dem Seitenmodell.</summary>
    /// <param name="now">Der Bezugszeitpunkt.</param>
    public void Refresh(DateTimeOffset now)
    {
        if (_timer.IsRunning)
        {
            ElapsedText = Format(_timer.Elapsed(now));
        }
    }

    private static string Format(TimeSpan value)
    {
        TimeSpan clamped = value < TimeSpan.Zero ? TimeSpan.Zero : value;

        return string.Create(CultureInfo.CurrentCulture,
            $"{(int)clamped.TotalHours:00}:{clamped.Minutes:00}:{clamped.Seconds:00}");
    }
}

/// <summary>Ein Ticket, wie eine Auswahl es zeigt.</summary>
/// <remarks>
/// <b>Es gibt keine Zeile „kein Ticket“ mit der Kennung 0.</b> Wer keines will, wählt nichts —
/// und ein leeres Auswahlfeld sagt dasselbe, ohne dass eine erfundene Kennung durch den Code
/// wandert. Anders als bei den Fernwartungsanbindungen, wo „nicht überwachen“ eine echte
/// Einstellung ist und deshalb eine eigene Zeile bekommt.
/// </remarks>
public sealed record TicketRow
{
    /// <summary>Baut eine Zeile aus einem Ticket.</summary>
    /// <param name="ticket">Das Ticket aus TANSS.</param>
    public TicketRow(Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        Id = ticket.Id;
        Title = string.IsNullOrWhiteSpace(ticket.Title) ? "Ohne Titel" : ticket.Title.Trim();
    }

    /// <summary>Die Ticketnummer.</summary>
    public int Id { get; }

    /// <summary>Der Titel.</summary>
    public string Title { get; }

    /// <summary>Nummer und Titel, wie sie im Auswahlfeld stehen.</summary>
    /// <remarks>
    /// Die Nummer vorn: Danach wird gesucht, und danach fragt der Kunde. Die Suche des
    /// Auswahlfelds läuft über diesen Text, also über beides — Nummer und Titel.
    /// </remarks>
    public string Display => string.Create(CultureInfo.CurrentCulture, $"#{Id} — {Title}");

    /// <inheritdoc />
    public override string ToString() => Display;
}

/// <summary>Eine Fernwartungsanbindung aus TANSS.</summary>
public sealed record SystemRow
{
    /// <summary>Die ausdrückliche Wahl „Nicht überwachen“.</summary>
    /// <remarks>
    /// Ein leeres Auswahlfeld sagt nicht, ob nichts zugeordnet oder nichts geladen ist. Der
    /// synthetische Eintrag mit der Kennung <see cref="MonitoringSetting.NotMonitored"/> macht
    /// den Normalfall sichtbar; die Vorlage führte ihn aus demselben Grund.
    /// </remarks>
    public static SystemRow None { get; } = new()
    {
        Id = MonitoringSetting.NotMonitored,
        Name = "Nicht überwachen",
        Color = string.Empty,
    };

    /// <summary>Baut eine Zeile aus einer Anbindung.</summary>
    /// <param name="system">Die Anbindung aus TANSS.</param>
    public static SystemRow From(RemoteSupportSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        return new SystemRow
        {
            Id = system.Id,
            Name = system.Name,
            Color = system.BackgroundColor ?? string.Empty,
            SupportTypeId = system.SupportTypeId,
        };
    }

    /// <summary>Die Kennung der Anbindung; zugleich der Fernwartungstyp.</summary>
    public int Id { get; init; }

    /// <summary>Der Name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Die Hintergrundfarbe als Hexwert ohne Raute.</summary>
    public string Color { get; init; } = string.Empty;

    /// <summary>Der Leistungstyp, mit dem TANSS daraus eine Leistung macht.</summary>
    public int SupportTypeId { get; init; }
}

/// <summary>Ein Beobachtungsprofil samt seiner Zuordnung.</summary>
public sealed record ProfileRow
{
    /// <summary>Baut eine Zeile aus Einstellung und Katalogeintrag.</summary>
    /// <param name="setting">Die Einstellung aus der Konfiguration.</param>
    /// <param name="profile">Der Katalogeintrag, falls der Schlüssel bekannt ist.</param>
    /// <param name="typeName">Der sprechende Name des Fernwartungstyps, falls bekannt.</param>
    public ProfileRow(MonitoringSetting setting, MonitoringProfile? profile, string? typeName)
    {
        ArgumentNullException.ThrowIfNull(setting);

        Key = setting.Key;
        Application = profile?.TypeDescription ?? setting.Key;
        ExcludedIps = setting.ExcludeIpAddresses?.Replace(';', ' ') ?? string.Empty;
        TypeId = setting.RemoteSupportTypeId;
        IsActive = setting.RemoteSupportTypeId != MonitoringSetting.NotMonitored;

        TypeText = !IsActive
            ? "Nicht überwachen"
            : typeName ?? string.Create(CultureInfo.CurrentCulture, $"Typ {setting.RemoteSupportTypeId}");

        Method = MethodText(profile);
    }

    /// <summary>Wie das Ziel ermittelt wird, in Worten.</summary>
    /// <remarks>
    /// Steht hier und nicht im Ansichtsmodell, weil zwei Seiten dieselbe Spalte zeigen. Zwei
    /// Übersetzungen derselben Aufzählung liefen auseinander, sobald ein Verfahren dazukommt.
    /// </remarks>
    /// <param name="profile">Der Katalogeintrag, oder <c>null</c>.</param>
    public static string MethodText(MonitoringProfile? profile) =>
        profile?.DestinationResolveMethod switch
        {
            DestinationResolveMethod.WindowTitle => "Titel",
            DestinationResolveMethod.WindowTitleRegex => "Muster im Titel",
            DestinationResolveMethod.RemoteHostIpConnection => "Netzwerkverbindung",
            _ => "unbekannt",
        };

    /// <summary>Der Profilschlüssel, etwa <c>mstsc</c>.</summary>
    public string Key { get; }

    /// <summary>Der sprechende Name der Anwendung.</summary>
    public string Application { get; }

    /// <summary>Wie das Ziel ermittelt wird.</summary>
    public string Method { get; }

    /// <summary>Der zugeordnete Fernwartungstyp.</summary>
    public int TypeId { get; }

    /// <summary>Der Fernwartungstyp im Klartext.</summary>
    public string TypeText { get; }

    /// <summary>Die ausgeschlossenen Adressbereiche, durch Leerzeichen getrennt.</summary>
    public string ExcludedIps { get; }

    /// <summary>Wird dieses Profil überhaupt beobachtet?</summary>
    public bool IsActive { get; }
}

/// <summary>Wie eine Prüfung ausgegangen ist.</summary>
/// <remarks>
/// <see cref="Unknown"/> ist eine eigene Stufe und ausdrücklich nicht dasselbe wie
/// <see cref="Ok"/>. Wer beides gleich anzeigt, behauptet Befunde, die niemand erhoben hat —
/// und beendet damit die Fehlersuche, bevor sie anfängt.
/// </remarks>
public enum CheckLevel
{
    /// <summary>Nicht geprüft. Keine Aussage.</summary>
    Unknown,

    /// <summary>Geprüft und in Ordnung.</summary>
    Ok,

    /// <summary>Läuft, verlangt aber Aufmerksamkeit.</summary>
    Warn,

    /// <summary>Geprüft und nicht in Ordnung.</summary>
    Fail,
}

/// <summary>Ein einzelner Befund der Verbindungsprüfung.</summary>
/// <param name="Name">Was geprüft wurde.</param>
/// <param name="Level">Wie es ausging.</param>
/// <param name="Detail">Woraus sich das ergibt — im Klartext und mit der Quelle.</param>
public sealed record CheckRow(string Name, CheckLevel Level, string Detail)
{
    /// <summary>Ist der Befund in Ordnung?</summary>
    public bool IsOk => Level == CheckLevel.Ok;

    /// <summary>Verlangt der Befund Aufmerksamkeit?</summary>
    public bool IsWarn => Level == CheckLevel.Warn;

    /// <summary>Ist der Befund nicht in Ordnung?</summary>
    public bool IsFail => Level == CheckLevel.Fail;

    /// <summary>Wurde gar nicht geprüft?</summary>
    public bool IsUnknown => Level == CheckLevel.Unknown;
}

/// <summary>Ein sichtbares Fenster, wie die Diagnoseseite es zeigt.</summary>
public sealed record WindowRow
{
    /// <summary>Baut eine Zeile aus Fenster, Prozess und Verbindungen.</summary>
    /// <param name="window">Das Fenster.</param>
    /// <param name="processName">Der Name des Prozesses.</param>
    /// <param name="connections">Die Gegenstellen der offenen Verbindungen dieses Prozesses.</param>
    public WindowRow(WindowInfo window, string processName, string connections)
    {
        Process = processName;
        ProcessId = window.ProcessId;
        Handle = string.Create(CultureInfo.InvariantCulture, $"0x{window.WindowHandle:X8}");
        Title = string.IsNullOrWhiteSpace(window.Title) ? "(ohne Titel)" : window.Title;
        IsMainWindow = !window.IsOwned;
        Connections = connections;
    }

    /// <summary>Der Prozessname ohne Endung.</summary>
    public string Process { get; }

    /// <summary>Die Prozesskennung.</summary>
    public int ProcessId { get; }

    /// <summary>Das Fensterhandle in Hexschreibweise.</summary>
    public string Handle { get; }

    /// <summary>Der Fenstertitel.</summary>
    public string Title { get; }

    /// <summary>Ist es das Hauptfenster des Prozesses?</summary>
    public bool IsMainWindow { get; }

    /// <summary>Die Gegenstellen der offenen Verbindungen, durch Komma getrennt.</summary>
    public string Connections { get; }
}
