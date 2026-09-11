using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.Storage.Queue;

/// <summary>Zustand eines Warteschlangeneintrags.</summary>
public enum QueueState
{
    /// <summary>Wartet auf den nächsten fälligen Versuch.</summary>
    Pending,

    /// <summary>Einem Arbeiter zugeteilt und gerade unterwegs.</summary>
    Sending,

    /// <summary>Bei TANSS angekommen. Die Zeile bleibt stehen und verhindert eine Doppelbuchung.</summary>
    Done,

    /// <summary>Aufgegeben. Wird nicht mehr von selbst versucht.</summary>
    Failed,
}

/// <summary>Ein Eintrag der Warteschlange.</summary>
/// <remarks>
/// Die Nutzlast ist ein fertiges <see cref="RemoteSupportWrite"/>. Sie entsteht genau einmal,
/// beim Abschluss der Sitzung, und wird danach nicht mehr angefasst. Ein Eintrag, dessen
/// Nutzlast beim Senden nachgerechnet würde, hinge von Einstellungen ab, die sich seither
/// geändert haben könnten — und die hochgeladene Fernwartung wäre nicht mehr die beobachtete.
/// </remarks>
public sealed record QueuedUpload
{
    /// <summary>Unsere Sitzungskennung und zugleich der Primärschlüssel.</summary>
    public required string RemoteMaintenanceId { get; init; }

    /// <summary>Die Fernwartung, so wie sie an TANSS geht.</summary>
    public required RemoteSupportWrite Payload { get; init; }

    /// <summary>Zustand.</summary>
    public required QueueState State { get; init; }

    /// <summary>Bisherige Sendeversuche.</summary>
    public int Attempts { get; init; }

    /// <summary>Wann der Eintrag in die Warteschlange kam.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Ab wann er wieder versucht werden darf.</summary>
    public DateTimeOffset NextAttemptAt { get; init; }

    /// <summary>Wann er zuletzt einem Arbeiter zugeteilt wurde.</summary>
    public DateTimeOffset? LeasedAt { get; init; }

    /// <summary>Wann er abgeschlossen wurde.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Der zuletzt gemeldete Fehler, für die Anzeige im Tray.</summary>
    public string? LastError { get; init; }

    /// <summary>
    /// Der Ausgang des letzten Sendeversuchs ist ungeklärt.
    /// </summary>
    /// <remarks>
    /// <para>Gesetzt von <see cref="IUploadQueue.RequeueStuck"/>: Der Prozess verschwand
    /// zwischen Zuteilung und Rückmeldung. Ob TANSS die Fernwartung trotzdem angenommen hat,
    /// weiß hier niemand.</para>
    ///
    /// <para><b>Der Aufrufer muss danach entscheiden.</b> Steht das Kennzeichen, ist vor dem
    /// Wiederholen die Existenzprüfung zu stellen (<c>PUT /api/v1/remoteSupports</c>, deren
    /// Textfilter auch die <c>remoteMaintenanceId</c> durchsucht). TANSS dedupliziert nicht:
    /// Ein zweiter POST mit derselben Kennung erzeugte nachweislich einen zweiten Datensatz
    /// (IDs 38584 und 38585). <see cref="Attempts"/> taugt als Ersatz nicht — der Zähler
    /// steigt auch nach einer eindeutigen Ablehnung, bei der nichts angekommen ist.</para>
    ///
    /// <para>Das Kennzeichen bleibt stehen, bis <see cref="IUploadQueue.MarkDone"/> den
    /// Eintrag abschließt. Ein weiterer misslungener Versuch klärt den früheren nicht auf.</para>
    /// </remarks>
    public bool OutcomeUnknown { get; init; }
}

/// <summary>
/// Das Ergebnis einer Abfrage der Warteschlange: die lesbaren Einträge <b>und</b> die Zahl
/// der Zeilen, die dabei übersprungen wurden.
/// </summary>
/// <remarks>
/// <para>Die Zahl gehört zwingend zur Liste. Ohne sie widersprachen sich Anzeige und
/// Zählung: <see cref="IUploadQueue.Count"/> zählt über SQL und sieht jede Zeile,
/// <see cref="IUploadQueue.List"/> überspringt die unlesbaren — der Techniker las
/// „ausstehend: 4“ neben drei Einträgen und hatte keinen Anhaltspunkt, wo der vierte
/// geblieben ist. Genau in diesem Zustand ist die Warteschlange aber erklärungsbedürftig:
/// Eine unlesbare Zeile ist eine Fernwartung, die niemand hochladen wird.</para>
///
/// <para>Übersprungen wird weiterhin und mit Absicht — eine einzige zerstörte Nutzlast darf
/// die Anzeige nicht kosten. Sie wird nur nicht mehr verschwiegen.</para>
/// </remarks>
public sealed record QueueListing
{
    /// <summary>Die lesbaren Einträge, jüngste zuerst.</summary>
    public required IReadOnlyList<QueuedUpload> Items { get; init; }

    /// <summary>
    /// Wie viele Zeilen innerhalb derselben Abfrage unlesbar waren.
    /// </summary>
    /// <remarks>
    /// Bezieht sich auf das abgefragte Fenster, nicht auf den ganzen Zustand: Wurde die
    /// Obergrenze erreicht, sagt die Zahl nichts über die Zeilen dahinter.
    /// </remarks>
    public int Unreadable { get; init; }

    /// <summary>Wie viele Zeilen die Abfrage insgesamt angefasst hat.</summary>
    public int Examined => Items.Count + Unreadable;

    /// <summary>
    /// Ein Satz für die Kommandozeile, oder <c>null</c>, wenn es nichts zu erklären gibt.
    /// </summary>
    /// <remarks>
    /// Steht hier und nicht in der Kommandozeile, damit jede Anzeige dieselbe Erklärung gibt
    /// — und damit keine sie vergisst.
    /// </remarks>
    public string? UnreadableNotice => Unreadable switch
    {
        0 => null,
        1 => "1 Eintrag ist unlesbar und deshalb oben nicht aufgeführt, in der Zählung aber "
             + "enthalten. " + Advice,
        _ => $"{Unreadable} Einträge sind unlesbar und deshalb oben nicht aufgeführt, in der "
             + "Zählung aber enthalten. " + Advice,
    };

    /// <summary>Was mit einer unlesbaren Zeile zu tun ist. Einmal formuliert, überall gleich.</summary>
    private const string Advice =
        "Ihre Nutzlast ist beschädigt; der nächste Sendeversuch setzt sie mit Begründung auf "
        + "„failed“. Die Fernwartung muss dann von Hand nachgetragen werden — ob sie nicht "
        + "doch schon in TANSS steht, beantwortet PUT /api/v1/remoteSupports.";
}

/// <summary>
/// Eine Fernwartung, die gerade läuft.
/// </summary>
/// <remarks>
/// <para>Diese Zeilen sind der Grund, warum ein Absturz nichts kostet. Sie werden mit jedem
/// Beobachtungstakt fortgeschrieben; beim Start liest der Dienst sie wieder ein und kann eine
/// unterbrochene Sitzung entweder fortsetzen oder mit <see cref="LastSeenAt"/> als Ende
/// abschließen. Ohne sie wäre der Beginn einer laufenden Fernwartung nach einem Neustart
/// unbekannt — und damit die ganze Sitzung verloren, denn rückwirkend lässt er sich aus der
/// Prozessliste nicht ermitteln.</para>
///
/// <para><see cref="LastSeenAt"/> ist bewusst getrennt von <see cref="StartedAt"/>: Nach einem
/// Absturz ist der letzte beobachtete Augenblick die einzige belastbare Aussage über das Ende.
/// Die aktuelle Uhrzeit zu nehmen, hieße, dem Kunden die Zeit zu berechnen, in der der
/// Rechner aus war.</para>
/// </remarks>
public sealed record OpenSession
{
    /// <summary>Unsere Sitzungskennung, üblicherweise eine GUID.</summary>
    public required string RemoteMaintenanceId { get; init; }

    /// <summary>Schlüssel der Beobachtungsregel, etwa <c>rdp</c>.</summary>
    public required string MonitorKey { get; init; }

    /// <summary>Fernwartungstyp in TANSS.</summary>
    public required int RemoteSupportTypeId { get; init; }

    /// <summary>Beginn der Sitzung.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Letzter Takt, in dem die Sitzung noch beobachtet wurde.</summary>
    public required DateTimeOffset LastSeenAt { get; init; }

    /// <summary>Prozesskennung der beobachteten Anwendung. 0, wenn unbekannt.</summary>
    public int ProcessId { get; init; }

    /// <summary>Die Gegenstelle, wie sie erkannt wurde — Rechnername, Adresse oder Kennung.</summary>
    public string? Target { get; init; }

    /// <summary>Gerätename für TANSS.</summary>
    public string? DeviceName { get; init; }

    /// <summary>Benutzername für TANSS.</summary>
    public string? UserName { get; init; }

    /// <summary>Bemerkung für TANSS.</summary>
    public string Comment { get; init; } = string.Empty;

    /// <summary>Ticketbezug. 0 bedeutet: keiner.</summary>
    public int TicketId { get; init; }
}
