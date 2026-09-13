using System.Globalization;

namespace TanssLogWatcher.Api.Model;

/// <summary>
/// Ein Ticket, so wie dieses Werkzeug es anlegt — auf die fünf rechtefreien Felder beschränkt.
/// </summary>
/// <remarks>
/// <para><b>Warum nur fünf Felder.</b> Das Anlegeschema von <c>POST /api/v1/tickets</c>
/// (Beschreibung zu 10.10.0, ab Zeile 466) führt weit mehr, aber ein Teil davon trägt eine
/// Rechtenummer: <c>assignedToDepartmentId</c> (Recht 365), <c>deadlineDate</c> (216),
/// <c>dueDate</c> (197 und 360), <c>serviceCapAmount</c> (215), <c>reminder</c> (198). Fehlt
/// dem angemeldeten Techniker eines dieser Rechte, scheitert nicht das einzelne Feld, sondern
/// der ganze POST — und der Techniker sähe nur einen Fehler, ohne zu erfahren, woran es lag.
/// Dieses Werkzeug sendet sie deshalb nicht. Ohne Rechtenummer sind: <c>companyId</c> (486),
/// <c>remitterId</c> (496), <c>title</c> (503), <c>content</c> (506),
/// <c>assignedToEmployeeId</c> (517).</para>
///
/// <para><b>Was TANSS wirklich erzwingt, ist UNBELEGT.</b> Im Rumpfschema steht kein einziges
/// <c>required</c>, und dokumentiert ist allein die Antwort 201 — es gibt keine beschriebene
/// Fehlerantwort, aus der sich die Pflichtfelder ablesen ließen. Gegen eine Produktivinstanz
/// ist das Anlegen nicht gemessen worden. <see cref="Problem"/> prüft deshalb nur, was ohne
/// TANSS entschieden werden kann; alles Weitere entscheidet der Server, und
/// <c>TicketCreator.Explain</c> benennt die in Frage kommenden Ursachen ausdrücklich als
/// ungeprüft.</para>
///
/// <para><b>Warum die Firma verlangt wird.</b> Die Beschreibung führt eine eigene Liste
/// <c>/api/v1/tickets/notIdentified</c> (Zeile 427) für „tickets, which couldn't be assigned to
/// a company“. Ein Ticket ohne Firma landet also nicht im Alltagsblick des Technikers, sondern
/// in einem Sammelbecken, das jemand gesondert abarbeiten muss. Eine Firma nachzutragen ist
/// Arbeit; sie gleich mitzugeben kostet nichts.</para>
/// </remarks>
public sealed record TicketDraft
{
    /// <summary>
    /// Die Firma, der das Ticket gehört. Zwingend und größer als 0.
    /// </summary>
    /// <remarks>
    /// Die Beschreibung hält zu diesem Feld fest: „Can only be set if the user has access to the
    /// company.“ Ein Ticket auf eine Firma, für die der angemeldete Techniker kein Recht hat,
    /// wird TANSS also abweisen — mit welcher Meldung, ist nicht belegt.
    /// </remarks>
    public required int CompanyId { get; init; }

    /// <summary>Der Betreff. Zwingend, weil ein Ticket ohne Betreff in keiner Liste auffindbar ist.</summary>
    /// <remarks>
    /// Die Schnittstelle verlangt ihn <b>nicht</b> — das ist eine Regel dieses Werkzeugs. Ein
    /// Ticket mit leerem Betreff wäre in TANSS eine leere Zeile, und niemand schreibt ihn
    /// nachträglich.
    /// </remarks>
    public string Title { get; init; } = string.Empty;

    /// <summary>Die Beschreibung. Darf leer bleiben; dann geht das Feld gar nicht erst hinaus.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// Der Auftraggeber — der Mitarbeiter der Firma, für den gearbeitet wird. 0 heißt „keiner“.
    /// </summary>
    /// <remarks>
    /// <para>Die Auswahl dazu kommt aus <c>GET /api/v1/companies/{companyId}/employees</c>
    /// (Beschreibung Zeile 12903).</para>
    /// <para><b>Möglicherweise Pflicht, nicht nachgemessen.</b> Die Beschreibung kennt einen
    /// Parameter <c>remitterCheck</c> „to omit the check that a remitter is required when
    /// storing tickets“ — es gibt also Instanzen, auf denen ein Auftraggeber verlangt wird. Ob
    /// diese eine dazugehört, ist unbekannt. Der Parameter selbst ist kaputt deklariert
    /// (<c>in: path</c> an einem Pfad ohne Platzhalter, <c>type: bool</c>) und wird von diesem
    /// Werkzeug nicht benutzt.</para>
    /// </remarks>
    public int RemitterId { get; init; }

    /// <summary>Der Techniker, dem das Ticket zugewiesen wird. 0 heißt „niemandem“.</summary>
    /// <remarks>
    /// Ohne Rechtenummer und damit sendbar — im Gegensatz zur Zuweisung an eine
    /// <b>Abteilung</b>, die Recht 365 verlangt und deshalb nicht angeboten wird.
    /// </remarks>
    public int AssignedToEmployeeId { get; init; }

    /// <summary>
    /// Was diesen Entwurf noch am Absenden hindert — als fertiger deutscher Satz, oder
    /// <see langword="null"/>, wenn nichts entgegensteht.
    /// </summary>
    /// <remarks>
    /// <b>Das ist keine Zusage, dass TANSS ihn annimmt.</b> Geprüft wird ausschließlich, was
    /// ohne Rückfrage beim Server feststeht. Die Oberfläche zeigt diesen Satz unverändert an,
    /// statt sich einen eigenen auszudenken.
    /// </remarks>
    public string? Problem
    {
        get
        {
            if (CompanyId <= 0)
            {
                return "Ohne Firma lässt sich kein Ticket anlegen. TANSS führt Tickets ohne "
                    + "Firmenzuordnung in einer eigenen Liste, in der sie niemand sieht.";
            }

            return string.IsNullOrWhiteSpace(Title)
                ? "Ohne Betreff lässt sich kein Ticket anlegen. Er ist das Einzige, woran das "
                  + "Ticket in jeder Liste zu erkennen ist."
                : null;
        }
    }

    /// <summary>Ist der Entwurf absendbar? Gleichbedeutend mit <c>Problem is null</c>.</summary>
    public bool IsComplete => Problem is null;
}

/// <summary>
/// Das Ergebnis eines Anlegevorgangs: die neue Ticketnummer und das, was TANSS dazu gesagt hat.
/// </summary>
/// <remarks>
/// <para>Steht beim Modell und nicht beim Repository — dieselbe Überlegung wie bei
/// <see cref="RemoteSupportCreateResult"/>: Wer das Ergebnis nur anzeigt, soll nicht auf eine
/// Umsetzung zeigen müssen.</para>
/// <para><b>Gelesen statt geraten.</b> Die 201-Antwort trägt die neue Nummer in
/// <c>content.id</c> (Beschreibung Zeilen 778–780, <c>readOnly</c>) und im Umschlag einen
/// <c>meta</c>-Block mit <c>linkedEntities</c> und <c>properties</c>. Beides wird ausgewertet:
/// der Firmenname kommt aus <c>linkedEntities</c>, eine etwaige Serverbemerkung aus
/// <c>properties.message</c>. Nichts davon wird aus Rechten oder Vermutungen abgeleitet.</para>
/// </remarks>
public sealed record TicketCreateResult
{
    /// <summary>Die neue Ticketnummer; 0, wenn TANSS keine genannt hat.</summary>
    public int TicketId { get; init; }

    /// <summary>Das angelegte Ticket, so wie TANSS es zurückgegeben hat; <see langword="null"/>, wenn nichts kam.</summary>
    public Ticket? Ticket { get; init; }

    /// <summary>
    /// Der Firmenname aus <c>meta.linkedEntities.companies</c> — sonst „Firma 886“.
    /// </summary>
    /// <remarks>
    /// <b>Für diese Route nicht gemessen.</b> Dass <c>linkedEntities</c> beim Lesen eines
    /// Tickets die Firmen trägt, ist am 13.09.2026 nachgemessen; dass der 201-Umschlag des
    /// Anlegens sie ebenfalls trägt, zeigt nur das Beispiel der Beschreibung (dort mit
    /// <c>employees</c>). Fehlt der Name, steht hier die Kennung — und niemals ein geratener
    /// Name.
    /// </remarks>
    public string CompanyName { get; init; } = string.Empty;

    /// <summary>Die Bemerkung aus <c>meta.properties.message</c>, falls TANSS eine geschickt hat.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Der Wert aus <c>meta.properties.editable</c>; <see langword="null"/> heißt: TANSS hat
    /// nichts dazu gesagt.
    /// </summary>
    /// <remarks>
    /// Die Beschreibung führt das Feld im 201-Umschlag, sagt aber nicht, worauf es sich bezieht.
    /// Es wird deshalb durchgereicht und nicht gedeutet.
    /// </remarks>
    public bool? Editable { get; init; }

    /// <summary>
    /// Gesetzt, wenn TANSS zwar zugestimmt, aber keine Ticketnummer genannt hat — sonst
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Ausdrücklich <b>keine</b> Ausnahme: Der Aufruf war erfolgreich, und wer daraufhin ein
    /// zweites Mal sendet, legt ein zweites Ticket an. Der Text sagt das.
    /// </remarks>
    public string? Warning { get; init; }

    /// <summary>Hat TANSS eine Ticketnummer genannt?</summary>
    public bool HasTicketId => TicketId > 0;

    /// <summary>
    /// Ein fertiger deutscher Satz für die Oberfläche; niemals leer.
    /// </summary>
    /// <remarks>
    /// Unverändert anzeigen. Wer sich aus <see cref="TicketId"/> selbst einen Text baut,
    /// verliert genau die Fälle, für die dieser Typ gemacht ist — die Antwort ohne Nummer und
    /// die Bemerkung des Servers.
    /// </remarks>
    public string Summary
    {
        get
        {
            string head = HasTicketId
                ? string.Create(CultureInfo.CurrentCulture,
                    $"Ticket {TicketId} für {CompanyName} angelegt.")
                : Warning ?? "TANSS hat keine Ticketnummer genannt.";

            return string.IsNullOrWhiteSpace(Note) ? head : head + " TANSS meldet: " + Note;
        }
    }
}
