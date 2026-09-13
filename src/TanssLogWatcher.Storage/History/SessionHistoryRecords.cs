namespace TanssLogWatcher.Storage.History;

/// <summary>Was aus einer abgeschlossenen Sitzung geworden ist.</summary>
/// <remarks>
/// Eine geschlossene Menge und bewusst nicht dieselbe Aufzählung wie
/// <c>SessionDisposition</c> der Oberfläche: Diese hier kennt zusätzlich den Ausgang nach
/// dem Versand (<see cref="Booked"/>), das ausdrückliche Verwerfen (<see cref="Discarded"/>)
/// und den Fall, in dem sich nichts mehr feststellen lässt (<see cref="Unknown"/>).
/// </remarks>
public enum HistoryDisposition
{
    /// <summary>Eingereiht; ging oder geht mit einem Sendelauf hinaus.</summary>
    Enqueued,

    /// <summary>Bei TANSS angekommen. <see cref="SessionHistoryEntry.TanssSupportId"/> steht dann.</summary>
    Booked,

    /// <summary>Vom Techniker im Abschlussdialog verworfen.</summary>
    Discarded,

    /// <summary>Keine Zuordnung auf einen Fernwartungstyp; es gäbe nichts, worauf gebucht wird.</summary>
    NotMapped,

    /// <summary>Trockenlauf: erkannt und angezeigt, aber nichts eingereiht.</summary>
    DryRun,

    /// <summary>Misslungen — das Einreihen selbst oder der Versand endgültig.</summary>
    Failed,

    /// <summary>
    /// Nicht mehr feststellbar.
    /// </summary>
    /// <remarks>
    /// Entsteht ausschliesslich beim einmaligen Nachtrag: Die Sitzung steht im
    /// Änderungsprotokoll, ihre Warteschlangenzeile ist aber fort. Ein geratener Ausgang
    /// wäre hier schlimmer als ein eingestandener.
    /// </remarks>
    Unknown,
}

/// <summary>Woher eine Verlaufszeile stammt.</summary>
public enum HistoryOrigin
{
    /// <summary>Beim Abschluss der Sitzung geschrieben; alle Angaben sind gemessen.</summary>
    Live,

    /// <summary>Einmalig aus Warteschlange und Änderungsprotokoll nachgetragen.</summary>
    Backfill,
}

/// <summary>
/// Der Zustand eines einzelnen Aufzeichnungsabschnitts.
/// </summary>
/// <remarks>
/// Dieselben vier Werte, die <c>recordings.state</c> als Zeichenkette führt
/// (<c>recording</c>, <c>kept</c>, <c>purged</c>, <c>missing</c>) — hier als Aufzählung,
/// damit die Frage „darf dieser Pfad geöffnet werden?“ der Übersetzer bewacht und nicht ein
/// Zeichenkettenvergleich, den jede Anzeige für sich schreibt.
/// </remarks>
public enum RecordingSegmentState
{
    /// <summary>Wird gerade geschrieben.</summary>
    Running,

    /// <summary>Abgeschlossen; die Datei liegt.</summary>
    Kept,

    /// <summary>Nach Ablauf der Frist von uns gelöscht.</summary>
    Purged,

    /// <summary>Fort, ohne dass wir gelöscht hätten.</summary>
    Missing,
}

/// <summary>Was von der Aufzeichnung einer Sitzung als Ganzes noch da ist.</summary>
/// <remarks>
/// Der zusammengefasste Befund über <b>alle</b> Abschnitte — für Symbol und Farbe der Anzeige.
/// Der Zustand eines einzelnen Abschnitts steht in <see cref="RecordingSegmentState"/>; die
/// beiden sind nicht dasselbe, denn eine Sitzung kann gleichzeitig liegende und gelöschte
/// Abschnitte haben.
/// </remarks>
public enum RecordingPresence
{
    /// <summary>Es wurde nicht aufgezeichnet.</summary>
    None,

    /// <summary>Mindestens ein Abschnitt wird gerade noch geschrieben.</summary>
    Running,

    /// <summary>Mindestens eine Datei liegt noch. Die Anzeige zeigt das Videosymbol.</summary>
    Present,

    /// <summary>Die Frist ist abgelaufen und wir haben gelöscht. Die Anzeige zeigt das rote X.</summary>
    Purged,

    /// <summary>Die Dateien sind fort, ohne dass wir sie gelöscht hätten.</summary>
    Missing,
}

/// <summary>
/// Ein einzelner Abschnitt der Aufzeichnung einer Sitzung.
/// </summary>
/// <remarks>
/// <para><b>Jeder Abschnitt hat seine eigene Frist.</b> Gemessen trägt eine Sitzung dieser
/// Datenbank drei Dateien mit drei verschiedenen Löschzeitpunkten; sie werden einzeln fällig
/// und einzeln gelöscht. Eine Zusammenfassung, die nur einen Pfad und nur einen Zustand führt,
/// spielt darum vier von fünfzehn aufgezeichneten Sekunden und zeigt nach einem
/// Teil-Löschlauf auf eine Datei, die es nicht mehr gibt.</para>
///
/// <para>Der laufende Stand erzeugt je Sitzung genau eine Datei
/// (<c>RecordingPaths.VideoName</c>). Mehrere Abschnitte sind Altbestand aus einem früheren
/// Stand — er liegt in dieser Datenbank und muss deshalb behandelt werden.</para>
/// </remarks>
public sealed record SessionRecordingSegment
{
    /// <summary>Der wievielte Abschnitt dieser Sitzung; beginnt bei 1.</summary>
    public required int Number { get; init; }

    /// <summary>
    /// Der Pfad des Abschnitts, relativ zur eingestellten Wurzel.
    /// </summary>
    /// <remarks>
    /// Relativ und nicht vollständig, weil die Wurzel in der Konfiguration steht und sich
    /// ändern darf. Zusammengesetzt wird mit <c>RecordingPaths.Root</c>; ob der
    /// zusammengesetzte Pfad tatsächlich unterhalb der Wurzel liegt, prüft
    /// <c>RecordingPaths.IsInside</c> — auch beim Abspielen und nicht nur beim Löschen.
    /// </remarks>
    public required string RelativePath { get; init; }

    /// <summary>Der Zustand genau dieses Abschnitts.</summary>
    public required RecordingSegmentState State { get; init; }

    /// <summary>Die aufgezeichnete Zeit dieses Abschnitts, ohne die Pausen.</summary>
    public TimeSpan Recorded { get; init; }

    /// <summary>Wie viele Bytes dieser Abschnitt belegt oder belegte.</summary>
    public long Bytes { get; init; }

    /// <summary>Der für diesen Abschnitt zugesagte Löschzeitpunkt.</summary>
    /// <remarks>
    /// Je Abschnitt und nicht je Sitzung: Der Zeitpunkt wird beim Beginn des Abschnitts
    /// festgeschrieben, und ein Abschnitt, der zehn Minuten später begann, wird zehn Minuten
    /// später fällig.
    /// </remarks>
    public required DateTimeOffset DeleteAfter { get; init; }

    /// <summary>Wann dieser Abschnitt gelöscht wurde; <c>null</c>, solange er liegt.</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>Warum er gelöscht wurde, in deutscher Prosa aus der Buchführung.</summary>
    public string? DeleteReason { get; init; }

    /// <summary>Liegt die Datei dieses Abschnitts noch?</summary>
    public bool Exists => State is RecordingSegmentState.Running or RecordingSegmentState.Kept;
}

/// <summary>
/// Was die Buchführung über die Aufzeichnung einer Sitzung sagt, zusammengefasst.
/// </summary>
/// <remarks>
/// <para>Sie wird <b>nicht</b> im Verlauf gespeichert, sondern bei jeder Abfrage aus
/// <c>recordings</c> mitgelesen. Der Zustand einer Aufzeichnung ändert sich nach dem Ende der
/// Sitzung noch — genau dann nämlich, wenn die Frist greift. Eine mitgeschriebene Kopie wäre
/// ab diesem Augenblick falsch, und falsch wäre sie ausgerechnet in der Angabe, wegen der die
/// Seite gebaut wird.</para>
///
/// <para><b>Sie führt die Abschnitte, nicht einen Pfad.</b> Alle Zählungen und Summen leiten
/// sich aus <see cref="Segments"/> ab; es gibt keine zweite, danebenliegende Wahrheit, die
/// auseinanderlaufen könnte. Was sich abspielen lässt, sagt <see cref="PlayableSegment"/> —
/// und zwar über genau den Abschnitt, dessen Pfad es herausgibt.</para>
/// </remarks>
public sealed record SessionRecordingSummary
{
    /// <summary>Der Befund, wenn es zu dieser Sitzung keine Aufzeichnung gibt.</summary>
    public static SessionRecordingSummary None { get; } = new();

    /// <summary>
    /// Die Abschnitte dieser Sitzung, nach <see cref="SessionRecordingSegment.Number"/>
    /// aufsteigend.
    /// </summary>
    public IReadOnlyList<SessionRecordingSegment> Segments { get; init; } = [];

    /// <summary>Wie viele Abschnitte es insgesamt gab.</summary>
    public int SegmentCount => Segments.Count;

    /// <summary>Wie viele davon noch geschrieben werden.</summary>
    public int RunningCount => Count(RecordingSegmentState.Running);

    /// <summary>Wie viele davon als Datei noch liegen.</summary>
    public int KeptCount => Count(RecordingSegmentState.Kept);

    /// <summary>Wie viele davon wir nach Ablauf der Frist gelöscht haben.</summary>
    public int PurgedCount => Count(RecordingSegmentState.Purged);

    /// <summary>Wie viele davon fort sind, ohne dass wir sie gelöscht hätten.</summary>
    public int MissingCount => Count(RecordingSegmentState.Missing);

    /// <summary>Die aufgezeichnete Zeit aller Abschnitte zusammen, ohne die Pausen.</summary>
    /// <remarks>
    /// Aller Abschnitte, auch der bereits gelöschten: Gefragt ist, wie lange aufgezeichnet
    /// wurde, nicht wie viel davon heute noch abzuspielen wäre. Was noch liegt, sagt
    /// <see cref="PlayableRecorded"/>.
    /// </remarks>
    public TimeSpan Recorded
    {
        get
        {
            TimeSpan total = TimeSpan.Zero;
            foreach (SessionRecordingSegment segment in Segments)
            {
                total += segment.Recorded;
            }

            return total;
        }
    }

    /// <summary>Die aufgezeichnete Zeit der Abschnitte, die noch als Datei liegen.</summary>
    /// <remarks>
    /// Weicht sie von <see cref="Recorded"/> ab, spielt der Abspielknopf weniger, als die
    /// Sitzung lang war — und die Anzeige muss das sagen, statt den Unterschied zu
    /// verschweigen.
    /// </remarks>
    public TimeSpan PlayableRecorded
    {
        get
        {
            TimeSpan total = TimeSpan.Zero;
            foreach (SessionRecordingSegment segment in Segments)
            {
                if (segment.State == RecordingSegmentState.Kept)
                {
                    total += segment.Recorded;
                }
            }

            return total;
        }
    }

    /// <summary>Wie viele Bytes alle Abschnitte zusammen belegen oder belegten.</summary>
    public long Bytes
    {
        get
        {
            long total = 0;
            foreach (SessionRecordingSegment segment in Segments)
            {
                total += segment.Bytes;
            }

            return total;
        }
    }

    /// <summary>
    /// Wann der nächste noch liegende Abschnitt zur Löschung ansteht; <c>null</c>, wenn keiner
    /// mehr liegt.
    /// </summary>
    /// <remarks>
    /// Der früheste Zeitpunkt unter den Abschnitten, die es noch gibt. Der Löschzeitpunkt
    /// eines bereits gelöschten Abschnitts beantwortet keine Frage mehr und geht hier deshalb
    /// nicht ein.
    /// </remarks>
    public DateTimeOffset? NextDeleteAfter
    {
        get
        {
            DateTimeOffset? earliest = null;
            foreach (SessionRecordingSegment segment in Segments)
            {
                if (segment.Exists && (earliest is null || segment.DeleteAfter < earliest))
                {
                    earliest = segment.DeleteAfter;
                }
            }

            return earliest;
        }
    }

    /// <summary>Wann zuletzt ein Abschnitt gelöscht wurde; <c>null</c>, solange nichts gelöscht ist.</summary>
    public DateTimeOffset? LastDeletedAt => LastDeleted?.DeletedAt;

    /// <summary>Warum zuletzt gelöscht wurde, in deutscher Prosa aus der Buchführung.</summary>
    public string? LastDeleteReason => LastDeleted?.DeleteReason;

    /// <summary>
    /// Der Befund in einem Wort, für Symbol und Farbe der Anzeige.
    /// </summary>
    /// <remarks>
    /// Ein noch laufender Abschnitt schlägt alles; danach zählt, ob überhaupt noch etwas liegt.
    /// Sind alle Abschnitte fort, geht <see cref="RecordingPresence.Purged"/> vor
    /// <see cref="RecordingPresence.Missing"/>: Dass die Frist gegriffen hat, ist die Auskunft,
    /// nach der gefragt wird — dass zusätzlich ein Abschnitt von Hand verschwand, steht in
    /// <see cref="MissingCount"/>.
    /// </remarks>
    public RecordingPresence Presence => this switch
    {
        { SegmentCount: 0 } => RecordingPresence.None,
        { RunningCount: > 0 } => RecordingPresence.Running,
        { KeptCount: > 0 } => RecordingPresence.Present,
        { PurgedCount: > 0 } => RecordingPresence.Purged,
        { MissingCount: > 0 } => RecordingPresence.Missing,
        _ => RecordingPresence.None,
    };

    /// <summary>
    /// Der Abschnitt, den ein Abspielknopf öffnen würde; <c>null</c>, wenn keiner liegt.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Zustand wird an genau dem Abschnitt geprüft, dessen Pfad herausgegeben
    /// wird.</b> Es ist der erste Abschnitt, der noch liegt — nicht der erste überhaupt. Ist
    /// Abschnitt 1 bereits gelöscht und liegen 2 und 3 noch, führt der Knopf auf Abschnitt 2
    /// und nicht auf eine Datei, die es nicht mehr gibt.</para>
    ///
    /// <para>Ein noch laufender Abschnitt zählt hier ausdrücklich nicht: Seine Datei wird
    /// gerade geschrieben, sie ist unvollständig und gehört dem Schreiber.</para>
    ///
    /// <para>Sind es mehrere, sagt die Anzeige das — <see cref="SessionRecordingSegment.Number"/>
    /// und <see cref="SegmentCount"/> geben „Abschnitt 1 von 3“ her. Still den ersten zu
    /// öffnen und die übrigen zu verschweigen, wäre dasselbe stillschweigende Abschneiden,
    /// gegen das diese Zusammenfassung gebaut ist.</para>
    /// </remarks>
    public SessionRecordingSegment? PlayableSegment
    {
        get
        {
            SessionRecordingSegment? found = null;
            foreach (SessionRecordingSegment segment in Segments)
            {
                if (segment.State == RecordingSegmentState.Kept &&
                    (found is null || segment.Number < found.Number))
                {
                    found = segment;
                }
            }

            return found;
        }
    }

    /// <summary>Der Pfad, den ein Abspielknopf öffnen würde; <c>null</c>, wenn keiner liegt.</summary>
    public string? PlayableRelativePath => PlayableSegment?.RelativePath;

    /// <summary>Liegt eine Datei, die sich abspielen liesse?</summary>
    /// <remarks>
    /// Die Frage gilt dem Abschnitt aus <see cref="PlayableSegment"/> und keinem anderen. Eine
    /// Zählung „irgendetwas liegt noch“ neben einem Pfad, der von woanders kommt, ergäbe genau
    /// den Fall, in dem das Videosymbol steht und der Knopf ins Leere greift.
    /// </remarks>
    public bool IsPlayable => PlayableSegment is not null;

    /// <summary>
    /// Gibt es mehr abspielbare Abschnitte als den einen, den der Knopf öffnet?
    /// </summary>
    /// <remarks>
    /// Steht das Kennzeichen, muss die Anzeige sagen, welchen Abschnitt sie öffnet — sonst
    /// spielt der Knopf einen Teil der Sitzung und gibt sich als die ganze aus.
    /// </remarks>
    public bool HasFurtherPlayableSegments => KeptCount > 1;

    private SessionRecordingSegment? LastDeleted
    {
        get
        {
            SessionRecordingSegment? found = null;
            foreach (SessionRecordingSegment segment in Segments)
            {
                if (segment.DeletedAt is { } deleted &&
                    (found is null || deleted > found.DeletedAt))
                {
                    found = segment;
                }
            }

            return found;
        }
    }

    private int Count(RecordingSegmentState state)
    {
        int count = 0;
        foreach (SessionRecordingSegment segment in Segments)
        {
            if (segment.State == state)
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>
/// Eine abgeschlossene Sitzung, so wie der Verlauf sie kennt.
/// </summary>
/// <remarks>
/// <para>Sie trägt, was die Seite „Verlauf“ zeigen soll, und ausdrücklich nicht mehr:
/// Arbeitsplatz, Techniker und Prozesskennung stehen hier <b>nicht</b>. Am Arbeitsplatz des
/// Technikers sind das drei Angaben, die immer dasselbe sagen — und drei personenbezogene
/// Felder, die drei Monate lägen, ohne je eine Frage zu beantworten. Wer sie für eine
/// einzelne Aufzeichnung braucht, findet sie in deren Begleitdatei.</para>
///
/// <para><b>Die Gegenstelle kennt drei Zustände, nicht zwei.</b> Sie steht im Klartext; sie
/// war da und wurde nach Ablauf der Klartextfrist geschwärzt; oder sie wurde nie ermittelt.
/// Die letzten beiden sehen im Feld gleich aus — beide <c>null</c> — und bedeuten
/// Verschiedenes: <see cref="RedactedAt"/> trennt sie, <see cref="IsDestinationRedacted"/> und
/// <see cref="IsDestinationUnknown"/> geben es fertig heraus.</para>
/// </remarks>
public sealed record SessionHistoryEntry
{
    /// <summary>Unsere Sitzungskennung; zugleich die <c>remoteMaintenanceId</c> bei TANSS.</summary>
    public required string SessionId { get; init; }

    /// <summary>Beginn der Sitzung.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Ende der Sitzung.</summary>
    public required DateTimeOffset EndedAt { get; init; }

    /// <summary>Schlüssel der Beobachtungsregel, etwa <c>mstsc</c>.</summary>
    public string ProfileKey { get; init; } = string.Empty;

    /// <summary>Der sprechende Name der Anwendung, etwa „Microsoft Remotedesktop“.</summary>
    public string ProfileName { get; init; } = string.Empty;

    /// <summary>
    /// Die Gegenstelle im Klartext; <c>null</c>, wenn geschwärzt oder nie ermittelt.
    /// </summary>
    /// <remarks>
    /// Derselbe Text, den <c>logging.redact_window_titles</c> im Änderungsprotokoll verbirgt:
    /// bei zehn der sechsunddreissig Profile der ganze Fenstertitel, bei den beiden
    /// Nachrichtenprofilen die Betreffzeile einer E-Mail. Wer die Schwärzung eingeschaltet hat,
    /// hat sie für diesen Text eingeschaltet — der Verlauf darf ihn nicht an dieser
    /// Entscheidung vorbei im Klartext führen.
    /// </remarks>
    public string? Destination { get; init; }

    /// <summary>
    /// Der gesalzene Abdruck der Gegenstelle.
    /// </summary>
    /// <remarks>
    /// <para><b>Zum Gruppieren, nicht zum Anzeigen.</b> Er beantwortet genau eine Frage:
    /// „dieselbe Gegenstelle wie in jener anderen Sitzung?“ — über Sitzungen hinweg. Der
    /// Zusammenhang mit dem Änderungsprotokoll steht ohnehin über die Sitzungskennung, die in
    /// beiden Tabellen liegt; als Begründung für dieses Feld taugt er nicht, denn eine Sitzung
    /// trägt im Protokoll gemessen zwei verschiedene Titelabdrücke.</para>
    ///
    /// <para><b>Acht Hexziffern sind keine Gegenstelle.</b> Der Abdruck gehört nie in ein Feld,
    /// in dem ein Mensch die Gegenstelle erwartet: Er sieht aus wie eine Kundenkennung und ist
    /// keine. Insbesondere ist der Abdruck des Platzhalters
    /// „<c>&lt;&lt;&lt; Kein Titel ermittelt &gt;&gt;&gt;</c>“ hier <b>nicht</b> einzutragen —
    /// dort war nie eine Gegenstelle aufgelöst, das Feld gehört auf <c>null</c> und die Anzeige
    /// sagt „Gegenstelle nicht ermittelt“.</para>
    /// </remarks>
    public string? DestinationFingerprint { get; init; }

    /// <summary>Der Fernwartungstyp in TANSS; <c>0</c>, wenn keiner zugeordnet war.</summary>
    public int RemoteSupportTypeId { get; init; }

    /// <summary>Der Ticketbezug; <c>0</c> bedeutet: keiner.</summary>
    public int TicketId { get; init; }

    /// <summary>Die von TANSS vergebene Kennung, sofern eine entstand.</summary>
    public int? TanssSupportId { get; init; }

    /// <summary>Was aus der Sitzung geworden ist.</summary>
    public required HistoryDisposition Disposition { get; init; }

    /// <summary>Warum es so ausging — derselbe Klartext wie im Änderungsprotokoll.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Ist das Ende geschätzt statt gemessen?</summary>
    public bool EndedEstimated { get; init; }

    /// <summary>Woher die Zeile stammt.</summary>
    public HistoryOrigin Origin { get; init; } = HistoryOrigin.Live;

    /// <summary>
    /// Frühestens wann die Zeile selbst gelöscht wird.
    /// </summary>
    /// <remarks>
    /// <b>Frühestens</b> und nicht „dann“: Solange zu dieser Sitzung noch eine Aufzeichnung
    /// liegt, bleibt die Zeile stehen — sonst läge eine Videodatei da, zu der die Buchführung
    /// nichts mehr sagen kann. Die Aufbewahrungsdauer für Aufzeichnungen darf länger
    /// eingestellt sein als die für den Verlauf; wer aus dieser Angabe eine Zusage auf den Tag
    /// liest, liest eine, die hier niemand gibt.
    /// </remarks>
    public required DateTimeOffset DeleteAfter { get; init; }

    /// <summary>
    /// Wann die Gegenstelle geschwärzt wurde; <c>null</c>, solange sie steht oder nie eine da war.
    /// </summary>
    /// <remarks>
    /// Das Unterscheidungsmerkmal zwischen „war da, Frist abgelaufen“ und „nie ermittelt“.
    /// Ohne es zeigte die Seite beide Male dasselbe und behauptete damit über die eine Hälfte
    /// etwas, das nicht stimmt.
    /// </remarks>
    public DateTimeOffset? RedactedAt { get; init; }

    /// <summary>Wann die Zeile angelegt wurde.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Was die Buchführung über die Aufzeichnung sagt.</summary>
    public SessionRecordingSummary Recording { get; init; } = SessionRecordingSummary.None;

    /// <summary>Die Dauer der Sitzung.</summary>
    /// <remarks>
    /// Bei <see cref="EndedEstimated"/> ist sie es nicht wirklich — gemessen weicht ein aus
    /// dem Protokoll abgeleitetes Ende bis zu fünf Minuten ab. Die Anzeige schreibt dann
    /// „~“ davor; eine Dauer ohne dieses Zeichen ist gemessen.
    /// </remarks>
    public TimeSpan Duration => EndedAt - StartedAt;

    /// <summary>Ist die Sitzung bei TANSS angekommen?</summary>
    public bool IsBooked => Disposition == HistoryDisposition.Booked;

    /// <summary>Steht die Gegenstelle im Klartext?</summary>
    public bool HasDestination => !string.IsNullOrEmpty(Destination);

    /// <summary>War eine Gegenstelle da und ist inzwischen geschwärzt?</summary>
    public bool IsDestinationRedacted => !HasDestination && RedactedAt is not null;

    /// <summary>Wurde nie eine Gegenstelle ermittelt?</summary>
    public bool IsDestinationUnknown => !HasDestination && RedactedAt is null;

    /// <summary>
    /// Die Gegenstelle, wie sie angezeigt werden darf.
    /// </summary>
    /// <remarks>
    /// Eine Stelle und nicht jede Anzeige für sich: Sonst zeigt die eine den Klartext und die
    /// andere acht Hexziffern, und niemand weiss, welche recht hat. Der Abdruck kommt hier
    /// bewusst nicht vor.
    /// </remarks>
    public string DestinationLabel => Destination is { Length: > 0 } plain
        ? plain
        : IsDestinationRedacted
            ? "Gegenstelle geschwärzt"
            : "Gegenstelle nicht ermittelt";
}
