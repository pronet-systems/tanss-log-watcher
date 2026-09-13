using System.Globalization;
using TanssLogWatcher.Storage.History;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Was die Verlaufsseite über die Aufzeichnung einer Sitzung sagen darf.
/// </summary>
/// <remarks>
/// <para><b>Es gibt mehr als „da“ und „gelöscht“.</b> Die Buchführung kennt vier Zustände je
/// Abschnitt (<see cref="RecordingSegmentState"/>), die Platte kennt zwei weitere — und beide
/// zusammen ergeben Fälle, die verschieden aussehen müssen, weil sie verschiedene Handlungen
/// nach sich ziehen. Eine Aufzeichnung, deren Frist morgen greift, ist zu sichern; eine, die
/// planmäßig gelöscht wurde, ist erledigt; eine, die ohne unser Zutun verschwunden ist, gehört
/// gemeldet.</para>
///
/// <para><b>Die Farbe folgt einer Regel, nicht dem Gefühl:</b> grün heisst „läuft noch“, gelb
/// „die Frist greift bald oder jetzt“, rot „es gibt kein Video mehr“, grau „dazu lässt sich
/// nichts sagen“. Wer eine fünfte Bedeutung braucht, braucht keine fünfte Farbe, sondern ein
/// anderes Zeichen.</para>
/// </remarks>
public enum RecordingBadge
{
    /// <summary>Es wurde nie aufgezeichnet — die Aufzeichnung war aus oder unzulässig.</summary>
    None,

    /// <summary>Ein Abschnitt wird noch geschrieben; die Datei ist unvollständig.</summary>
    Running,

    /// <summary>Aufgezeichnet, die Datei liegt nachweislich da, die Frist ist noch fern.</summary>
    Available,

    /// <summary>Die Datei liegt da, aber die Löschfrist greift bald.</summary>
    Expiring,

    /// <summary>Die Frist ist abgelaufen; der nächste Aufräumlauf löscht die Datei.</summary>
    Overdue,

    /// <summary>Nach Ablauf der Frist gelöscht. Die Zeile bleibt als Nachweis stehen.</summary>
    Purged,

    /// <summary>Verschwunden, ohne dass wir gelöscht hätten.</summary>
    Missing,

    /// <summary>Der Ordner ist nicht erreichbar — hier wird nichts behauptet.</summary>
    Unknown,
}

/// <summary>
/// Was ein Blick auf die Platte über die Abschnitte <b>einer</b> Sitzung ergeben hat.
/// </summary>
/// <remarks>
/// <para><see cref="NotLooked"/> ist eine eigene Stufe und ausdrücklich nicht „nichts
/// gefunden“: Ein Netzlaufwerk, das gerade nicht verbunden ist, hat nichts gelöscht. Wer beides
/// gleich anzeigt, behauptet einen Verlust, den niemand festgestellt hat — das ist Hausregel 2.</para>
///
/// <para>Geführt werden die <b>Nummern</b> der Abschnitte, die wirklich liegen, und nicht bloß
/// eine Anzahl. Nur so lässt sich sagen, welchen Abschnitt der Abspielknopf öffnet, wenn
/// Abschnitt 1 bereits gelöscht ist und 2 und 3 noch liegen.</para>
/// </remarks>
public sealed record RecordingSighting
{
    /// <summary>Es wurde nicht nachgesehen — der Aufzeichnungsordner war nicht erreichbar.</summary>
    public static RecordingSighting NotLooked { get; } = new();

    /// <summary>Nachgesehen, es lag nichts — oder es gab nichts nachzusehen.</summary>
    public static RecordingSighting Nothing { get; } = new() { Looked = true };

    /// <summary>Konnte überhaupt nachgesehen werden?</summary>
    public bool Looked { get; init; }

    /// <summary>Die Nummern der Abschnitte, deren Datei nachweislich am Platz liegt.</summary>
    public IReadOnlyList<int> Present { get; init; } = [];

    /// <summary>Liegt die Datei dieses Abschnitts?</summary>
    /// <param name="number">Die Nummer des Abschnitts.</param>
    public bool Holds(int number)
    {
        // Bewusst eine Schleife und kein Suchbaum: Eine Sitzung hat gemessen einen bis drei
        // Abschnitte, und ein Satz kostete je Zeile eine Streuwerttabelle.
        foreach (int candidate in Present)
        {
            if (candidate == number)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Eine abgeschlossene Sitzung, wie eine Zeile des Verlaufs sie zeigt.
/// </summary>
/// <remarks>
/// <para><b>Unveränderlich.</b> Anders als <see cref="SessionRow"/> tickt hier nichts: Was beim
/// Abschluss galt, gilt für diese Zeile. Ändert sich etwas an der Aufzeichnung, wird der
/// Ausschnitt neu geholt — das ist eine Abfrage und billiger als eine Zeile, die sich selbst
/// überwacht.</para>
///
/// <para><b>Der Zustand der Aufzeichnung wird hier entschieden und nirgends sonst.</b> Er
/// entsteht aus zwei Quellen — der Buchführung über die Abschnitte und einem Blick auf die
/// Platte —, und die Regel, wie beide zusammengehen, darf es nur einmal geben.</para>
///
/// <para><b>Der Abdruck der Gegenstelle kommt hier nicht vor.</b> Angezeigt wird ausschliesslich
/// <see cref="SessionHistoryEntry.DestinationLabel"/>: der Klartext, sonst „Gegenstelle
/// geschwärzt“ oder „Gegenstelle nicht ermittelt“. Acht Hexziffern sehen aus wie eine
/// Kundenkennung und bedeuten oft genug bloß „kein Titel ermittelt“ — sie hinzuschreiben wäre
/// eine erfundene Angabe.</para>
/// </remarks>
public sealed record HistoryRow
{
    /// <summary>Baut eine Zeile aus einem Verlaufseintrag.</summary>
    /// <param name="entry">Der Eintrag aus dem Verlauf.</param>
    /// <param name="typeName">Der sprechende Name des Fernwartungstyps, falls bekannt.</param>
    /// <param name="color">Die Farbe der Anbindung als Hexwert ohne Raute.</param>
    /// <param name="sighting">Was der Blick auf die Platte ergeben hat.</param>
    /// <param name="warnWindow">Ab welcher Restfrist gewarnt wird.</param>
    /// <param name="now">Der Bezugszeitpunkt.</param>
    public HistoryRow(SessionHistoryEntry entry, string? typeName, string? color,
                      RecordingSighting sighting, TimeSpan warnWindow, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(sighting);

        SessionRecordingSummary recording = entry.Recording;

        SessionId = entry.SessionId;
        Color = color ?? string.Empty;

        // Niemals entry.Destination und niemals der Abdruck: DestinationLabel unterscheidet
        // "geschwaerzt" von "nicht ermittelt", und beides ist eine Aussage, keine Luecke.
        Destination = entry.DestinationLabel;
        DestinationIsPlain = entry.HasDestination;

        // Anwendung und Fernwartungstyp in EINER Zeile statt in zwei Spalten: Sie sagen
        // dasselbe aus zwei Richtungen ("mstsc" und "Remotedesktop"), und eine eigene Spalte
        // dafuer nimmt genau den Platz, den die Aufzeichnung braucht.
        string type = entry.RemoteSupportTypeId <= 0
            ? "nicht überwacht"
            : typeName ?? string.Create(CultureInfo.CurrentCulture,
                $"Typ {entry.RemoteSupportTypeId}");

        string profile = entry.ProfileName.Length > 0 ? entry.ProfileName : "Anwendung unbekannt";

        Subline = string.Create(CultureInfo.CurrentCulture, $"{profile} · {type}");

        StartText = Texts.Moment(entry.StartedAt);

        // Der Vorbehalt steht auf einer zweiten Zeile und nicht als Wort davor. "rund " vor
        // den Text gesetzt ergab bei kurzen Sitzungen "rund unter 1 Minute" - zweimal derselbe
        // Vorbehalt in einem Satz, und in den 95 Bildpunkten der Spalte abgeschnitten zu
        // "rund unter 1 Mi...". Gemessen liegt ein geschaetztes Ende bis zu fuenf Minuten NACH
        // dem tatsaechlichen; die Dauer ist also eher zu lang als zu kurz, und der Hinweistext
        // sagt das.
        DurationText = Texts.Minutes(entry.Duration);
        DurationNote = entry.EndedEstimated ? "geschätzt" : string.Empty;

        DurationTip = entry.EndedEstimated
            ? "Das Ende stammt aus einem Protokolleintrag und nicht aus der Messung. Ein "
              + "solcher Eintrag liegt gemessen bis zu fünf Minuten nach dem tatsächlichen "
              + "Ende — die Sitzung war eher kürzer als hier angegeben."
            : "Gemessen von Beginn bis Ende der Sitzung.";

        Tip = TipForSession(entry);

        TanssText = entry.Disposition switch
        {
            HistoryDisposition.Booked => "gebucht",
            HistoryDisposition.Enqueued => "wartet",
            HistoryDisposition.Discarded => "verworfen",
            HistoryDisposition.Failed => "nicht gebucht",
            HistoryDisposition.DryRun => "Trockenlauf",
            HistoryDisposition.NotMapped => "nicht überwacht",
            _ => "unbekannt",
        };

        // Rot bekommt genau ein Ausgang: eine Sitzung, die nirgends angekommen ist. Sie ist
        // nicht bloss ein Zustand, sie ist unbezahlte Arbeit.
        TanssIsFailure = entry.Disposition == HistoryDisposition.Failed;

        TanssTip = TipForTanss(entry);

        PlayableSegment = FirstLying(recording, sighting);
        int lying = LyingCount(recording, sighting);

        Badge = BadgeFor(recording, sighting, PlayableSegment, warnWindow, now);

        BadgeText = Badge switch
        {
            RecordingBadge.None => "—",
            RecordingBadge.Running => "wird geschlossen",
            RecordingBadge.Available or RecordingBadge.Expiring or RecordingBadge.Overdue
                => "vorhanden",
            RecordingBadge.Purged => "gelöscht",
            RecordingBadge.Missing => "nicht auffindbar",
            _ => "unbekannt",
        };

        RetentionText = RetentionFor(recording, Badge, PlayableSegment, now);
        SegmentText = SegmentNote(recording, Badge, PlayableSegment, lying);
        HasSegmentNote = SegmentText.Length > 0;
        BadgeTip = TipForRecording(recording, Badge, PlayableSegment, lying);

        // Abspielbar ist ausschliesslich, was nachweislich dort liegt und fertig geschrieben
        // ist. Ein Knopf, der eine halb geschriebene Datei oeffnet, zeigt ein kaputtes Video -
        // und der Techniker haelt danach die ganze Aufzeichnung fuer kaputt.
        CanPlay = PlayableSegment is not null
                  && Badge is RecordingBadge.Available or RecordingBadge.Expiring
                              or RecordingBadge.Overdue;

        PlayRelativePath = PlayableSegment?.RelativePath ?? string.Empty;
    }

    /// <summary>Unsere Sitzungskennung.</summary>
    public string SessionId { get; }

    /// <summary>Die Gegenstelle, so wie sie gezeigt werden darf — niemals ein Abdruck.</summary>
    public string Destination { get; }

    /// <summary>Steht dort ein echter Name und nicht der Ersatztext?</summary>
    /// <remarks>
    /// Die Anzeige setzt den Ersatztext daraufhin zurückhaltender: „Gegenstelle geschwärzt“ ist
    /// eine Auskunft über das Feld und kein Rechnername, und beides gleich fett zu setzen
    /// hiesse, den Ersatztext als Namen auszugeben.
    /// </remarks>
    public bool DestinationIsPlain { get; }

    /// <summary>Anwendung und Fernwartungstyp in einer Zeile.</summary>
    public string Subline { get; }

    /// <summary>Die Farbe der Anbindung als Hexwert ohne Raute; leer, wenn unbekannt.</summary>
    public string Color { get; }

    /// <summary>Beginn als Tag und Uhrzeit.</summary>
    public string StartText { get; }

    /// <summary>Die Dauer in vollen Minuten.</summary>
    public string DurationText { get; }

    /// <summary>
    /// Der Vorbehalt unter der Dauer; leer, wo das Ende gemessen ist.
    /// </summary>
    /// <remarks>
    /// Eine eigene Zeile und kein Wort vor der Dauer: <see cref="Texts.Minutes"/> liefert bei
    /// kurzen Sitzungen selbst schon „unter 1 Minute“, und ein „rund“ davor sagte denselben
    /// Vorbehalt ein zweites Mal — in einer Spalte, in der er nicht einmal Platz hat.
    /// </remarks>
    public string DurationNote { get; }

    /// <summary>Gibt es einen Vorbehalt zur Dauer?</summary>
    public bool HasDurationNote => DurationNote.Length > 0;

    /// <summary>Was im Hinweistext über der Dauer steht.</summary>
    public string DurationTip { get; }

    /// <summary>Was im Hinweistext der ersten Spalte steht.</summary>
    public string Tip { get; }

    /// <summary>Der Ausgang als Plakettentext.</summary>
    public string TanssText { get; }

    /// <summary>Ist die Sitzung nirgends angekommen?</summary>
    public bool TanssIsFailure { get; }

    /// <summary>Der Hinweistext der Plakette — dort steht die TANSS-Kennung.</summary>
    public string TanssTip { get; }

    /// <summary>Der Zustand der Aufzeichnung.</summary>
    public RecordingBadge Badge { get; }

    /// <summary>Der Zustand in einem Wort.</summary>
    public string BadgeText { get; }

    /// <summary>Die Zeile darunter: Restfrist, Löschtag oder Grund.</summary>
    public string RetentionText { get; }

    /// <summary>
    /// „Abschnitt 1 von 3“ — leer, solange es nichts zu sagen gibt.
    /// </summary>
    /// <remarks>
    /// Der Satz steht in der Zeile und nicht bloß im Hinweistext: Gemessen trägt eine Sitzung
    /// dieser Datenbank drei Dateien mit drei verschiedenen Löschzeitpunkten. Ein Knopf, der
    /// still den ersten Abschnitt öffnet, spielt vier von fünfzehn aufgezeichneten Sekunden und
    /// gibt sich als die ganze Sitzung aus.
    /// </remarks>
    public string SegmentText { get; }

    /// <summary>Gibt es zu den Abschnitten etwas zu sagen?</summary>
    public bool HasSegmentNote { get; }

    /// <summary>Der Hinweistext zum Zustand — hier steht der ganze Satz.</summary>
    public string BadgeTip { get; }

    /// <summary>Lässt sich diese Aufzeichnung abspielen?</summary>
    public bool CanPlay { get; }

    /// <summary>Der Abschnitt, den der Knopf öffnet; <c>null</c>, wenn keiner liegt.</summary>
    public SessionRecordingSegment? PlayableSegment { get; }

    /// <summary>
    /// Der Pfad unterhalb der Aufzeichnungswurzel, den der Knopf öffnet; leer, wenn keiner.
    /// </summary>
    /// <remarks>
    /// Es ist der Pfad <b>des</b> Abschnitts, dessen Vorhandensein geprüft wurde — nicht der
    /// des ersten Abschnitts. Sonst stünde das Videosymbol, während der Knopf ins Leere griffe.
    /// </remarks>
    public string PlayRelativePath { get; }

    /// <summary>Gehört zu dieser Sitzung überhaupt eine Aufzeichnung?</summary>
    public bool HasRecording => Badge != RecordingBadge.None;

    /// <summary>
    /// Der erste noch liegende Abschnitt — der, den der Knopf öffnen würde.
    /// </summary>
    /// <remarks>
    /// Bewusst nicht <see cref="SessionRecordingSummary.PlayableSegment"/>: Jene Eigenschaft
    /// kennt nur die Buchführung. Hier zählt zusätzlich, dass die Datei beim Nachsehen wirklich
    /// da war — sonst zeigte die Zeile „vorhanden“ über einer Datei, die der Aufräumlauf oder
    /// ein Sicherungsprogramm längst geholt hat.
    /// </remarks>
    private static SessionRecordingSegment? FirstLying(SessionRecordingSummary recording,
                                                       RecordingSighting sighting)
    {
        SessionRecordingSegment? found = null;

        foreach (SessionRecordingSegment segment in recording.Segments)
        {
            if (segment.State != RecordingSegmentState.Kept || !sighting.Holds(segment.Number))
            {
                continue;
            }

            if (found is null || segment.Number < found.Number)
            {
                found = segment;
            }
        }

        return found;
    }

    private static int LyingCount(SessionRecordingSummary recording, RecordingSighting sighting)
    {
        int count = 0;

        foreach (SessionRecordingSegment segment in recording.Segments)
        {
            if (segment.State == RecordingSegmentState.Kept && sighting.Holds(segment.Number))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Die Regel, wie Buchführung und Platte zusammengehen.</summary>
    /// <remarks>
    /// Die Reihenfolge ist dieselbe wie in <see cref="SessionRecordingSummary.Presence"/> — ein
    /// laufender Abschnitt schlägt alles, danach zählt, ob noch etwas liegt. Der Unterschied:
    /// Wo die Buchführung „liegt“ sagt, entscheidet hier der Blick auf die Platte, und wo der
    /// nicht möglich war, wird nichts entschieden.
    /// </remarks>
    private static RecordingBadge BadgeFor(SessionRecordingSummary recording,
                                           RecordingSighting sighting,
                                           SessionRecordingSegment? lying,
                                           TimeSpan warnWindow, DateTimeOffset now)
    {
        // Ohne Zeile in der Buchfuehrung wurde nie aufgezeichnet. Das Dateisystem wird dafuer
        // NICHT befragt: Was dort liegt und uns nicht gehoert, gehoert auch nicht in diese
        // Anzeige - geloescht wuerde es ohnehin nie.
        if (recording.SegmentCount == 0)
        {
            return RecordingBadge.None;
        }

        if (recording.RunningCount > 0)
        {
            return RecordingBadge.Running;
        }

        if (recording.KeptCount > 0)
        {
            if (!sighting.Looked)
            {
                return RecordingBadge.Unknown;
            }

            if (lying is null)
            {
                // Bewusst nur angezeigt und NICHT als "missing" eingetragen: Das Eintragen ist
                // Sache des Aufraeumers. Eine Anzeige, die Grabsteine setzt, setzt sie
                // irgendwann fuer ein Laufwerk, das nur schlief.
                return RecordingBadge.Missing;
            }

            DateTimeOffset deadline = EarliestDeadline(recording, lying);

            if (deadline <= now)
            {
                return RecordingBadge.Overdue;
            }

            return deadline - now <= warnWindow
                ? RecordingBadge.Expiring
                : RecordingBadge.Available;
        }

        // Dass die Frist gegriffen hat, ist die Auskunft, nach der gefragt wird; dass
        // zusaetzlich ein Abschnitt von Hand verschwand, steht im Hinweistext.
        return recording.PurgedCount > 0 ? RecordingBadge.Purged : RecordingBadge.Missing;
    }

    /// <summary>
    /// Die früheste Frist unter den Abschnitten, die noch liegen.
    /// </summary>
    /// <remarks>
    /// Nicht die des abgespielten Abschnitts allein: Warnt die Zeile erst, wenn der erste
    /// Abschnitt fällig wird, verschwindet der dritte womöglich vorher. Die Frist wird über die
    /// Abschnitte gerechnet, deren Datei nachweislich liegt — ein bereits gelöschter Abschnitt
    /// hat keine Frist mehr, vor der zu warnen wäre.
    /// </remarks>
    private static DateTimeOffset EarliestDeadline(SessionRecordingSummary recording,
                                                   SessionRecordingSegment lying)
    {
        DateTimeOffset earliest = lying.DeleteAfter;

        foreach (SessionRecordingSegment segment in recording.Segments)
        {
            if (segment.State == RecordingSegmentState.Kept && segment.DeleteAfter < earliest)
            {
                earliest = segment.DeleteAfter;
            }
        }

        return earliest;
    }

    private static string RetentionFor(SessionRecordingSummary recording, RecordingBadge badge,
                                       SessionRecordingSegment? lying, DateTimeOffset now)
    {
        return badge switch
        {
            RecordingBadge.Available or RecordingBadge.Expiring => lying is null
                ? string.Empty
                : "noch " + Texts.Span(EarliestDeadline(recording, lying) - now),
            RecordingBadge.Overdue => "Frist abgelaufen",
            RecordingBadge.Purged => recording.LastDeletedAt is { } gone
                ? Texts.Day(gone.ToLocalTime())
                : string.Empty,
            RecordingBadge.Unknown => "Ordner nicht erreichbar",
            _ => string.Empty,
        };
    }

    /// <summary>Was über die Abschnitte in der Zeile stehen muss.</summary>
    private static string SegmentNote(SessionRecordingSummary recording, RecordingBadge badge,
                                      SessionRecordingSegment? lying, int lyingCount)
    {
        if (lying is not null && badge is RecordingBadge.Available or RecordingBadge.Expiring
                                         or RecordingBadge.Overdue)
        {
            if (recording.SegmentCount <= 1)
            {
                return string.Empty;
            }

            string note = string.Create(CultureInfo.CurrentCulture,
                $"Abschnitt {lying.Number} von {recording.SegmentCount}");

            // Wenn die Buchfuehrung mehr liegende Abschnitte fuehrt, als auf der Platte zu
            // finden waren, gehoert die Zahl dazu - sonst sieht der Rest wie "alles da" aus.
            int absent = recording.KeptCount - lyingCount;

            return absent > 0
                ? note + string.Create(CultureInfo.CurrentCulture, $", {absent} nicht auffindbar")
                : note;
        }

        if (badge == RecordingBadge.Purged && recording.SegmentCount > 1)
        {
            return Texts.Count(recording.SegmentCount, "Abschnitt", "Abschnitte");
        }

        return string.Empty;
    }

    private static string TipForSession(SessionHistoryEntry entry)
    {
        // Die Sitzungskennung steht im Hinweistext und nicht in einer Spalte: Gefragt wird nach
        // ihr genau dann, wenn jemand den Eintrag in TANSS sucht - selten, aber dann
        // vollstaendig. Eine eigene Spalte laese in neunundneunzig von hundert Faellen niemand.
        string text = entry.DestinationLabel + "\n"
            + string.Create(CultureInfo.CurrentCulture,
                $"Sitzungskennung {entry.SessionId}") + "\n"
            + string.Create(CultureInfo.CurrentCulture,
                $"{Texts.Day(entry.StartedAt)} {Texts.Clock(entry.StartedAt)} bis "
                + $"{Texts.Clock(entry.EndedAt)}");

        if (entry.EndedEstimated)
        {
            text += "\nDas Ende ist geschätzt: Die Sitzung wurde nicht beim Schliessen des "
                + "Fensters, sondern nachträglich abgeschlossen.";
        }

        if (entry.IsDestinationRedacted)
        {
            text += "\nDie Gegenstelle wurde ermittelt und beim Schreiben geschwärzt — sie ist "
                + "nicht unbekannt, sie steht nur nicht im Klartext.";
        }
        else if (entry.IsDestinationUnknown)
        {
            text += "\nEine Gegenstelle wurde nie aufgelöst. Es ist dabei nichts verlorengegangen.";
        }

        if (entry.Origin == HistoryOrigin.Backfill)
        {
            text += "\nNachgetragen aus älteren Aufzeichnungen dieses Werkzeugs; die Angaben "
                + "sind so vollständig, wie sie dort standen.";
        }

        if (entry.Reason.Length > 0)
        {
            text += "\n" + entry.Reason;
        }

        return text;
    }

    private static string TipForTanss(SessionHistoryEntry entry)
    {
        string text = entry.TanssSupportId is { } id
            ? string.Create(CultureInfo.CurrentCulture, $"TANSS-Fernwartung {id}")
            : entry.Disposition switch
            {
                HistoryDisposition.Enqueued =>
                    "Liegt in der Warteschlange und geht beim nächsten Sendelauf hinaus.",
                HistoryDisposition.Discarded =>
                    "Wurde verworfen — es steht nichts in TANSS und es wird auch nichts "
                    + "hineingehen.",
                HistoryDisposition.Failed =>
                    "Nirgends angekommen. Das ist die einzige Zeile dieser Seite, die Geld "
                    + "kostet.",
                HistoryDisposition.DryRun =>
                    "Trockenlauf: erkannt und bewusst nicht gebucht.",
                HistoryDisposition.NotMapped =>
                    "Zu dieser Anwendung ist kein Fernwartungstyp zugeordnet; gebucht wird "
                    + "deshalb nichts.",
                _ =>
                    "Wie es ausgegangen ist, steht nicht fest — beim Senden brach die Verbindung "
                    + "ab, bevor TANSS geantwortet hat.",
            };

        if (entry.TicketId > 0)
        {
            text += string.Create(CultureInfo.CurrentCulture, $"\nTicket {entry.TicketId}");
        }

        return text;
    }

    private static string TipForRecording(SessionRecordingSummary recording, RecordingBadge badge,
                                          SessionRecordingSegment? lying, int lyingCount)
    {
        return badge switch
        {
            RecordingBadge.None =>
                "Zu dieser Sitzung gibt es keine Aufzeichnung. Warum, steht im "
                + "Änderungsprotokoll — üblicherweise war die Aufzeichnung aus oder die "
                + "Kenntnisnahme fehlte.",

            RecordingBadge.Running =>
                "Die Buchführung führt einen Abschnitt, der noch geschrieben wird. Unmittelbar "
                + "nach dem Ende einer Sitzung ist das normal und dauert Sekunden; bleibt es "
                + "stehen, wurde das Werkzeug mitten in der Aufzeichnung beendet. Abgespielt "
                + "wird eine halb geschriebene Datei nicht.",

            RecordingBadge.Available => lying is null
                ? "Liegt im Aufzeichnungsordner."
                : "Liegt im Aufzeichnungsordner und wird frühestens am "
                  + Texts.Day(EarliestDeadline(recording, lying).ToLocalTime())
                  + " gelöscht."
                  + Lengths(recording, lyingCount),

            RecordingBadge.Expiring => lying is null
                ? "Die Löschfrist greift bald."
                : "Die Löschfrist greift am "
                  + Texts.Day(EarliestDeadline(recording, lying).ToLocalTime())
                  + ". Was danach noch gebraucht wird, muss bis dahin an einen anderen Ort."
                  + Lengths(recording, lyingCount),

            RecordingBadge.Overdue =>
                "Die Frist ist abgelaufen. Der Aufräumlauf löscht die Datei beim nächsten "
                + "Durchgang; unter „Aufzeichnung“ lässt sich das sofort auslösen."
                + Lengths(recording, lyingCount),

            RecordingBadge.Purged => recording.LastDeletedAt is { } gone
                ? "Am " + Texts.Day(gone.ToLocalTime()) + " nach Ablauf der Frist gelöscht. "
                  + "Der Eintrag bleibt als Nachweis stehen — für eine Auskunft nach Art. 15 "
                  + "DSGVO ist zu sagen, was es gab und wann es verschwand."
                  + (recording.LastDeleteReason is { Length: > 0 } why ? "\n" + why : string.Empty)
                : "Nach Ablauf der Frist gelöscht.",

            RecordingBadge.Missing =>
                "Die Buchführung führt eine Aufzeichnung, am erwarteten Ort liegt aber keine "
                + "Datei. Gelöscht hat sie nicht dieses Werkzeug — üblich sind ein "
                + "Sicherungslauf, ein Handgriff im Explorer oder ein verschobener Ordner.",

            _ =>
                "Der Aufzeichnungsordner ist gerade nicht erreichbar; ob die Datei dort liegt, "
                + "ist damit offen. Bei einem Netzlaufwerk heisst das meistens: nicht "
                + "verbunden. Es wird deshalb nichts behauptet und nichts eingetragen.",
        };
    }

    /// <summary>Der Satz zur Länge — er steht nur da, wo es eine Datei gibt.</summary>
    /// <remarks>
    /// Die aufgezeichnete Zeit ist ausdrücklich nicht die Dauer der Sitzung: Pausen zählen nicht
    /// mit. Und liegen nicht mehr alle Abschnitte, steht die noch abspielbare Zeit daneben —
    /// sonst verspricht die Zeile eine Länge, die der Knopf nicht mehr einlöst.
    /// </remarks>
    private static string Lengths(SessionRecordingSummary recording, int lyingCount)
    {
        if (recording.Recorded <= TimeSpan.Zero)
        {
            return string.Empty;
        }

        string text = "\nAufgezeichnet sind " + Texts.Minutes(recording.Recorded)
            + "; Pausen zählen nicht mit.";

        if (lyingCount < recording.SegmentCount && recording.PlayableRecorded > TimeSpan.Zero)
        {
            text += "\nAbspielbar sind davon noch " + Texts.Minutes(recording.PlayableRecorded)
                + " — der Rest ist gelöscht oder nicht auffindbar.";
        }

        return text;
    }
}
