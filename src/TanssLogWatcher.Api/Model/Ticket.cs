using System.Text.Json.Serialization;

namespace TanssLogWatcher.Api.Model;

/// <summary>Ein Ticket, reduziert auf das, was die Ticketauswahl beim Sitzungsende braucht.</summary>
public sealed record Ticket
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("companyId")] public int CompanyId { get; init; }
    [JsonPropertyName("statusId")] public int StatusId { get; init; }
    [JsonPropertyName("typeId")] public int TypeId { get; init; }

    public override string ToString() => $"#{Id} {Title}";
}

/// <summary>
/// Der Filter der Ticketsuche.
/// </summary>
/// <remarks>
/// <para>Nur die Felder, die dieses Werkzeug braucht. Die Schnittstelle nimmt weit mehr
/// entgegen — Abteilungen, Projekte, Zeitfenster, Kennzeichen —, aber was nicht gesendet wird,
/// gilt als nicht gefiltert, und ein Feld abzubilden, das niemand setzt, ist ein Feld, das
/// irgendwann falsch gesetzt wird.</para>
/// <para><b>Ohne <see cref="Staff"/> kommt alles zurück, was der Anmeldende sehen darf</b> —
/// nachgemessen 101 Tickets gegenüber 20 eigenen. Für eine Auswahl beim Buchen ist das zu
/// viel; gebucht wird auf das eigene Ticket.</para>
/// </remarks>
public sealed record TicketSearch
{
    /// <summary>Die Mitarbeiter, deren Tickets gesucht werden.</summary>
    [JsonPropertyName("staff")] public IReadOnlyList<int> Staff { get; init; } = [];

    /// <summary>
    /// Sollen auch erledigte Tickets mitkommen?
    /// </summary>
    /// <remarks>
    /// Vorbelegt mit <c>false</c>, und das ist keine Sparsamkeit: Auf ein erledigtes Ticket zu
    /// buchen ist fast immer ein Versehen. Nachgemessen kamen mit <c>true</c> 572 statt 20
    /// Tickets zurück — die Auswahl wäre unbrauchbar.
    /// </remarks>
    [JsonPropertyName("includeDoneTickets")] public bool IncludeDoneTickets { get; init; }

    /// <summary>
    /// Die Firmen, deren Tickets gesucht werden.
    /// </summary>
    /// <remarks>
    /// <para>Das Feld, das dem Abschlussdialog gefehlt hat (Beschreibung 10.10.0, Zeile 1022:
    /// <c>companies</c>, <c>fetch only ticket from these companies</c>). Gebucht wird auf das
    /// Ticket <b>des Kunden</b>, und das ist häufig nicht das eigene: Wer eine Fernwartung für
    /// den Kollegen übernimmt, findet dessen Ticket nur so.</para>
    /// <para><b>Firmen ODER Mitarbeiter, nicht beides zugleich.</b> Beide Filter gemeinsam
    /// ergäben „meine Tickets bei diesem Kunden“ und liessen genau das Ticket weg, auf das
    /// gebucht werden soll.</para>
    /// <para>Leer heisst „nicht nach Firma gefiltert“; das Feld bleibt dann weg, statt als
    /// leere Liste hinauszugehen — was TANSS aus einer leeren Liste macht, ist nicht gemessen.</para>
    /// </remarks>
    [JsonPropertyName("companies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? Companies { get; init; }
}

/// <summary>
/// Wie die Ticketabfrage zu einer Firma ausgegangen ist.
/// </summary>
/// <remarks>
/// Dreiwertig aus demselben Grund wie überall in diesem Werkzeug: „diese Firma hat kein offenes
/// Ticket“ und „die Frage liess sich nicht klären“ sehen beide wie eine leere Liste aus.
/// </remarks>
public enum CompanyTicketOutcome
{
    /// <summary>Es gibt offene Tickets.</summary>
    Found,

    /// <summary>Die Firma hat <b>nachweislich</b> kein offenes Ticket.</summary>
    NoTickets,

    /// <summary><b>Nicht ermittelt.</b> Die Abfrage ist gescheitert.</summary>
    Undetermined,
}

/// <summary>
/// Die offenen Tickets einer Firma — und, wenn TANSS ihn genannt hat, ihr Name.
/// </summary>
/// <remarks>
/// <para><b>Warum der Name hier mitkommt.</b> Zu <c>/api/v1/companies</c> gibt es in der ganzen
/// Beschreibung nur <c>post</c>; ein <c>GET /api/v1/companies/{id}</c> existiert nicht. Aus
/// einer <c>companyId</c> allein lässt sich also kein Name holen. Er steht ausschliesslich im
/// <c>meta</c>-Block einer Antwort, die diese Firma ohnehin trägt — bei den Ticketrouten
/// ausdrücklich annotiert (<c>x-linked-entities: companies</c>). Ein einziger Aufruf beantwortet
/// damit beides: welche Tickets es gibt und wie der Kunde heisst.</para>
/// <para><b>Der Name kann trotzdem ausbleiben</b>, etwa wenn die Firma kein offenes Ticket hat
/// und <c>meta</c> leer kommt. Dann steht hier <see langword="null"/>, und die Oberfläche zeigt
/// die Kennung — „Firma 10413“ ist eine Auskunft, ein erfundener Name wäre keine (Hausregel 2).</para>
/// </remarks>
public sealed record CompanyTickets
{
    /// <summary>Wie die Antwort zu lesen ist.</summary>
    public CompanyTicketOutcome Outcome { get; init; }

    /// <summary>Die offenen Tickets, jüngste zuerst.</summary>
    public IReadOnlyList<Ticket> Tickets { get; init; } = [];

    /// <summary>Der Firmenname aus <c>meta.linkedEntities.companies</c>, oder <see langword="null"/>.</summary>
    public string? CompanyName { get; init; }

    /// <summary>Ein fertiger deutscher Satz für die Oberfläche.</summary>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>Ist die Aussage belastbar? <see langword="false"/> heisst: nicht ermittelt.</summary>
    public bool IsCertain => Outcome != CompanyTicketOutcome.Undetermined;
}

/// <summary>Ein Techniker aus <c>/api/tanss.x/v1/technicians</c>.</summary>
public sealed record Technician
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("firstName")] public string? FirstName { get; init; }
    [JsonPropertyName("lastName")] public string? LastName { get; init; }
    [JsonPropertyName("emailAddress")] public string? EmailAddress { get; init; }
}

/// <summary>
/// Wie die Prüfung einer Ticketnummer ausgegangen ist.
/// </summary>
/// <remarks>
/// <para><b>Drei Antworten, nicht zwei.</b> Ein Rückgabewert <c>Ticket?</c> kennt nur „gefunden“
/// und „nicht gefunden“ — und muss deshalb einen Netzfehler zu einem der beiden schlagen. Beide
/// Ausgänge wären falsch: „gefunden“ verschweigt einen Zahlendreher, „nicht gefunden“ behauptet
/// über ein Ticket, das es sehr wohl gibt, das Gegenteil. <see cref="Undetermined"/> ist der
/// dritte Fall, und er ist der wichtige.</para>
/// <para><b>Hausregel 5 steckt in <see cref="TicketCheck.BlocksBooking"/>.</b> Nur
/// <see cref="DoesNotExist"/> hält das Buchen an. Eine misslungene Prüfung tut es nicht: Im
/// Abschlussdialog hängt Arbeitszeit am Knopf, und ein Aussetzer der Leitung darf
/// den Bericht des Technikers nicht kosten.</para>
/// </remarks>
public enum TicketCheckOutcome
{
    /// <summary>
    /// Es wurde keine Ticketnummer angegeben; TANSS wurde gar nicht erst gefragt.
    /// </summary>
    /// <remarks>
    /// Kein Fehler. Ohne Ticketbezug zu buchen ist erlaubt — TANSS führt die 0 selbst als „kein
    /// Ticket“. Dieser Fall darf in der Oberfläche niemals wie <see cref="DoesNotExist"/>
    /// aussehen.
    /// </remarks>
    NoTicketNumber,

    /// <summary>Das Ticket gibt es. <see cref="TicketCheck.Summary"/> trägt Titel und Firma.</summary>
    Exists,

    /// <summary>
    /// Das Ticket gibt es <b>nachweislich</b> nicht.
    /// </summary>
    /// <remarks>
    /// Nachgemessen am 13.09.2026 gegen eine Instanz der Version 10.10.0: <c>GET
    /// /api/v1/tickets/999999999</c> antwortet mit <b>404</b> und dem Rumpf
    /// <c>{"error":{"text":"OBJECT_NOT_FOUND","localizedText":"Das Objekt wurde nicht
    /// gefunden","type":"DataNotFoundException","traceId":"…"}}</c>. Die Beschreibung der
    /// Schnittstelle kennt zu dieser Route nur 200 und 403; das 404 ist gemessen und real.
    /// </remarks>
    DoesNotExist,

    /// <summary>
    /// <b>Nicht ermittelt.</b> Es ließ sich nicht klären, ob es das Ticket gibt.
    /// </summary>
    /// <remarks>
    /// Netzfehler, Zeitüberschreitung, 5xx, eine 403 — und der 404, der nicht
    /// <c>OBJECT_NOT_FOUND</c> meldet. Die Oberfläche zeigt
    /// <see cref="TicketCheck.Explanation"/> und lässt weiterarbeiten.
    /// </remarks>
    Undetermined,
}

/// <summary>
/// Das Ergebnis einer Ticketprüfung: die Antwort <b>und</b> die Aussage darüber, wie belastbar
/// sie ist.
/// </summary>
/// <remarks>
/// <para><b>Warum Titel und Firma mitkommen.</b> Eine Prüfung, die nur „Ticket 5000 gibt es“
/// sagt, fängt den häufigsten Fehler nicht: den Zahlendreher auf ein <i>anderes vorhandenes</i>
/// Ticket. „#5000 Serverstörung — Müller GmbH“ fängt ihn, weil der Techniker sofort sieht, dass
/// er bei einem fremden Kunden gelandet ist. Der Firmenname steht nachgemessen nur im
/// <c>meta</c>-Block unter <c>linkedEntities.companies</c> — deshalb geht die Prüfung über
/// <c>ITanssMetaRead</c>.</para>
/// <para><b>Nichts an diesem Ergebnis ist geraten.</b> Wo TANSS keinen Namen genannt hat, steht
/// „Firma 886“ und nicht ein Name, der passen könnte (Hausregel 2).</para>
/// </remarks>
public sealed record TicketCheck
{
    /// <summary>Wie die Antwort zu lesen ist.</summary>
    public TicketCheckOutcome Outcome { get; init; }

    /// <summary>Die geprüfte Nummer, so wie sie hereinkam.</summary>
    public int TicketId { get; init; }

    /// <summary>
    /// Das Ticket — nur bei <see cref="TicketCheckOutcome.Exists"/> gesetzt.
    /// </summary>
    /// <remarks>
    /// Bei <see cref="TicketCheckOutcome.Undetermined"/> bewusst <see langword="null"/>: Ein
    /// halbes Ticket aus einer misslungenen Prüfung anzuzeigen wäre eine vorgetäuschte Prüfung.
    /// </remarks>
    public Ticket? Ticket { get; init; }

    /// <summary>
    /// Der Firmenname zum Ticket, oder <see langword="null"/>, wenn das Ticket keine Firma trägt.
    /// </summary>
    /// <remarks>
    /// Hat TANSS die Firma nicht benannt, steht hier „Firma 886“ — die Kennung, sichtbar als
    /// solche. Auch das trennt noch zwei verschiedene Tickets voneinander, und es erfindet
    /// nichts.
    /// </remarks>
    public string? CompanyName { get; init; }

    /// <summary>Der HTTP-Status, an dem die Prüfung hing; <see langword="null"/>, wenn keiner anfiel.</summary>
    /// <remarks>Nur zur Fehlersuche im Protokoll. Die Oberfläche zeigt <see cref="Explanation"/>.</remarks>
    public int? Status { get; init; }

    /// <summary>
    /// Die technische Meldung zum Fehlschlag, bereits geschwärzt; sonst <see langword="null"/>.
    /// </summary>
    /// <remarks>Gehört ins Protokoll, nicht in den Dialog — sie ist lang und englisch gefärbt.</remarks>
    public string? Failure { get; init; }

    /// <summary>Ein fertiger deutscher Satz für die Oberfläche.</summary>
    /// <remarks>
    /// Fertig formuliert und nicht bloß ein Schlüsselwort, damit niemand in der Oberfläche aus
    /// <see cref="TicketCheckOutcome.Undetermined"/> doch wieder „Ticket gibt es nicht“ macht.
    /// </remarks>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>
    /// Die Zeile, die den Zahlendreher auffallen lässt: „#5000 Serverstörung — Müller GmbH“.
    /// </summary>
    /// <remarks>
    /// Leer, solange kein Ticket vorliegt. Die Nummer stammt aus <see cref="TicketId"/> und
    /// nicht aus dem Rumpf — so steht dort auch dann die geprüfte Nummer, wenn TANSS das Feld
    /// <c>id</c> einmal nicht mitschickt.
    /// </remarks>
    public string Summary
    {
        get
        {
            if (Ticket is null)
            {
                return string.Empty;
            }

            string head = string.IsNullOrWhiteSpace(Ticket.Title)
                ? $"#{TicketId}"
                : $"#{TicketId} {Ticket.Title.Trim()}";

            return string.IsNullOrWhiteSpace(CompanyName) ? head : head + " — " + CompanyName;
        }
    }

    /// <summary>Ist die Aussage belastbar? <see langword="false"/> heißt: nicht ermittelt.</summary>
    public bool IsCertain => Outcome != TicketCheckOutcome.Undetermined;

    /// <summary>
    /// Darf dieses Ergebnis das Buchen anhalten?
    /// </summary>
    /// <remarks>
    /// <b>Nur bei <see cref="TicketCheckOutcome.DoesNotExist"/>.</b> Auf eine Nummer zu buchen,
    /// zu der es kein Ticket gibt, ist schlimmer als ohne Ticket zu buchen: Die Leistung taucht
    /// in keiner Auswertung auf und fällt niemandem auf. Eine <i>misslungene</i> Prüfung hält
    /// dagegen nichts an — sonst kostete ein Aussetzer der Leitung den Bericht (Hausregel 5).
    /// </remarks>
    public bool BlocksBooking => Outcome == TicketCheckOutcome.DoesNotExist;
}
