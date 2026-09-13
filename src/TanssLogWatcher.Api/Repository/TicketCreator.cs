using System.Text.Json;
using System.Text.Json.Serialization;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.Api.Repository;

/// <summary>
/// Legt ein Ticket an — <c>POST /api/v1/tickets</c>.
/// </summary>
/// <remarks>
/// <para><b>Bauart wie <see cref="RemoteSupportRepository.CreateWithDiagnosticsAsync"/>:</b>
/// ein schreibender Aufruf ohne jede Wiederholung, der Umschlag wird mitgelesen
/// (<c>PostWithMetaAsync</c>), und eine Antwort ohne Kennung endet in einem <b>Hinweis</b>
/// statt in einer Ausnahme. Der Grund ist derselbe: Eine Ausnahme an dieser Stelle liest sich
/// wie „nichts passiert“ und verleitet zum zweiten Versuch — und der legt ein zweites Ticket
/// an. Für Tickets ist das sogar heikler als für Fernwartungen: Es gibt keine eigene Kennung
/// wie <c>remoteMaintenanceId</c>, über die sich eine Dublette nachträglich erkennen ließe,
/// und damit auch keine Existenzprüfung.</para>
///
/// <para><b>Gesendet werden ausschließlich die fünf rechtefreien Felder</b> — siehe
/// <see cref="TicketDraft"/>. Kein Feld mit Rechtenummer geht hinaus, damit ein fehlendes
/// Recht nicht den ganzen Vorgang scheitern lässt.</para>
///
/// <para><b>Was nicht belegt ist.</b> Das Anlegen ist gegen keine Produktivinstanz gemessen
/// worden; belegt ist allein die Beschreibung zu 10.10.0. Sie nennt kein Pflichtfeld und keine
/// Fehlerantwort. Deshalb: <see cref="Explain"/> benennt die in Frage kommenden Ursachen eines
/// Fehlschlags ausdrücklich als ungeprüft, und keine Meldung dieses Typs behauptet, sie kenne
/// den Grund.</para>
///
/// <para><b>Erreichbar über <see cref="ITicketCreation"/>.</b> Der Vertrag steht daneben und
/// nicht auf <c>ITicketRepository</c> — der bleibt schmal und liest nur. Die Oberfläche nimmt
/// die Schnittstelle und nicht diesen Typ, damit ein Dialog, der Tickets anlegt, ohne TANSS
/// prüfbar bleibt.</para>
/// </remarks>
public sealed class TicketCreator : ITicketCreation
{
    /// <summary>
    /// Die Ursachen, die bei einem abgewiesenen POST in Frage kommen — sämtlich ungeprüft.
    /// </summary>
    /// <remarks>
    /// Bewusst als Aufzählung von Möglichkeiten formuliert und nicht als Diagnose. Die
    /// Beschreibung gibt nichts her, woraus sich die Pflichtfelder ablesen ließen, und eine
    /// erfundene Ursache schickt den Techniker auf eine Fährte, die es nicht gibt
    /// (Hausregel 2).
    /// </remarks>
    private const string UnprovenCauses =
        "Welche Felder TANSS beim Anlegen eines Tickets erzwingt, ist nicht belegt: Das "
        + "Rumpfschema der Beschreibung zu 10.10.0 führt kein einziges Pflichtfeld, und "
        + "beschrieben ist nur die Antwort 201. In Frage kommen daher, ungeprüft: (1) der "
        + "angemeldete Mitarbeiter hat kein Recht auf die gewählte Firma — die Beschreibung "
        + "hält zu companyId fest, sie lasse sich nur mit Zugriff auf die Firma setzen; "
        + "(2) diese Instanz verlangt einen Auftraggeber (remitterId), denn die Beschreibung "
        + "kennt einen Schalter, um genau diese Prüfung abzuschalten; (3) ein Pflichtfeld, das "
        + "dieses Werkzeug bewusst nicht sendet, weil es ein Recht voraussetzt.";

    private readonly ITanssClient _client;

    /// <summary>Baut den Anleger.</summary>
    /// <param name="client">Der HTTP-Zugang.</param>
    public TicketCreator(ITanssClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>
    /// Ergänzt die Meldung eines Fehlschlags um die ungeprüften Ursachen.
    /// </summary>
    /// <remarks>
    /// <para>Die Ausnahme selbst wird <b>nicht</b> umgehüllt: Ihr Typ trägt Bedeutung —
    /// <c>TanssAuthException</c> löst andernorts die Tokenerneuerung aus —, und ein
    /// Umhüllen ginge auf Kosten dieses Typs. Wer dem Techniker etwas anzeigen will, ruft
    /// diese Methode; wer auf den Typ reagiert, fängt weiter wie bisher.</para>
    /// <para>Der Rumpf des Entwurfs geht nicht in die Meldung ein. Betreff und Beschreibung
    /// tragen Kundendaten, und die haben in einer Fehlermeldung nichts zu suchen.</para>
    /// </remarks>
    /// <param name="error">Der Fehlschlag, so wie er aus <see cref="CreateAsync"/> kam.</param>
    /// <returns>Ein anzeigbarer deutscher Text.</returns>
    public static string Explain(TanssException error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return "Das Ticket wurde nicht angelegt. " + error.Message + " " + UnprovenCauses;
    }

    /// <summary>
    /// Legt das Ticket an.
    /// </summary>
    /// <remarks>
    /// <para>Wirft eine <see cref="TanssException"/>, wenn TANSS abweist — den anzeigbaren Text
    /// dazu liefert <see cref="Explain"/>. Eine <b>Zeitüberschreitung</b> ist damit nicht
    /// gleichbedeutend mit „nicht angelegt“: Ob die Anfrage angekommen ist, lässt sich von hier
    /// aus nicht sagen, und ein zweiter Versuch legt gegebenenfalls ein zweites Ticket an. Der
    /// Aufrufer schlägt in diesem Fall in TANSS nach, statt blind zu wiederholen.</para>
    /// </remarks>
    /// <param name="draft">Der Entwurf. Muss <see cref="TicketDraft.IsComplete"/> erfüllen.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>Die neue Ticketnummer samt dem, was TANSS im Umschlag dazu gesagt hat.</returns>
    /// <exception cref="ArgumentException">
    /// Der Entwurf ist unvollständig. Es wird dann <b>nichts gesendet</b>; der Text ist der
    /// Satz aus <see cref="TicketDraft.Problem"/>.
    /// </exception>
    public async Task<TicketCreateResult> CreateAsync(TicketDraft draft,
                                                      CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.Problem is { } problem)
        {
            // Gar nicht erst senden: Was hier auffaellt, braucht keinen Serverlauf, und ein
            // Ticket ohne Firma waere in TANSS nur mit Handarbeit wieder einzufangen.
            throw new ArgumentException(problem, nameof(draft));
        }

        TicketCreateBody body = new()
        {
            CompanyId = draft.CompanyId,
            Title = draft.Title.Trim(),
            Content = Filled(draft.Content),
            RemitterId = Set(draft.RemitterId),
            AssignedToEmployeeId = Set(draft.AssignedToEmployeeId),
        };

        // Bewusst ohne Wiederholung: schreibend. PostWithMetaAsync statt PostAsync, weil die
        // Nummer im Inhalt und der Firmenname im Umschlag steht - beides wird gebraucht.
        // Die Route ist dieselbe wie die der Ticketsuche; die Benennung der Konstanten stammt
        // vom lesenden PUT. Ein eigener Name dafuer gehoert in TanssRoutes.cs.
        (Ticket? created, IReadOnlyDictionary<string, object?> meta) =
            await _client.PostWithMetaAsync<Ticket>(TanssRoutes.TicketSearch, body, ct: ct)
                .ConfigureAwait(false);

        int id = created?.Id ?? 0;

        // Der Server hat das letzte Wort ueber die Firma: Steht im Inhalt eine andere als die
        // gesendete, wird der Name zu der nachgeschlagen, die wirklich gespeichert wurde.
        int companyId = created is { CompanyId: > 0 } ? created.CompanyId : draft.CompanyId;
        (string? note, bool? editable) = ReadProperties(meta);

        return new TicketCreateResult
        {
            TicketId = id,
            Ticket = created,
            CompanyName = LinkedEntities.From(meta).CompanyName(companyId),
            Note = note,
            Editable = editable,
            Warning = id > 0
                ? null
                : "TANSS hat die Anfrage angenommen, aber keine Ticketnummer genannt. Ob das "
                  + "Ticket angelegt wurde, ist in TANSS nachzuschlagen — auf keinen Fall "
                  + "erneut senden: Ein zweiter Aufruf legt ein zweites Ticket an, und es gibt "
                  + "keine Kennung, an der sich die Dublette später erkennen ließe.",
        };
    }

    /// <summary>Liest <c>meta.properties</c>, soweit die Beschreibung es festlegt.</summary>
    /// <remarks>
    /// <para>Belegt sind dort <c>fields</c>, <c>extras</c>, <c>editable</c> und <c>message</c>
    /// (Beschreibung zu 10.10.0, 201-Antwort des Anlegens). Gelesen werden nur die beiden, die
    /// sich ohne Deutung anzeigen lassen. <c>fields</c> und <c>extras</c> bleiben liegen: Ihr
    /// Beispiel zeigt <c>someFieldIndex: {}</c>, also nichts, woraus sich ein Sinn ableiten
    /// ließe — und geraten wird hier nichts.</para>
    /// <para>Wirft nie. Ein unlesbarer Umschlag kostet die Bemerkung, nicht das Ticket
    /// (Hausregel 5).</para>
    /// </remarks>
    private static (string? Note, bool? Editable) ReadProperties(
        IReadOnlyDictionary<string, object?> meta)
    {
        if (!meta.TryGetValue("properties", out object? raw) || raw is not JsonElement properties
            || properties.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        string? note = properties.TryGetProperty("message", out JsonElement message)
                       && message.ValueKind == JsonValueKind.String
            ? Filled(message.GetString())
            : null;

        bool? editable = properties.TryGetProperty("editable", out JsonElement flag)
            ? flag.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

        return (note, editable);
    }

    /// <summary>Leertext gilt als „nicht gesetzt“ und geht gar nicht erst hinaus.</summary>
    private static string? Filled(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>0 gilt als „nicht gesetzt“ und geht gar nicht erst hinaus.</summary>
    private static int? Set(int value) => value > 0 ? value : null;

    /// <summary>
    /// Der Rumpf des Anlegens — genau die fünf rechtefreien Felder, und nur die gesetzten.
    /// </summary>
    /// <remarks>
    /// <para><b>Warum die nicht gesetzten Felder wegbleiben.</b> Die Einstellung dieses Hauses
    /// schreibt sonst jedes Feld mit (<c>DefaultIgnoreCondition.Never</c>), und das ist bei den
    /// Fernwartungen auch richtig so — dort ist nachgemessen, dass TANSS zwischen „Feld fehlt“
    /// und „Feld ist 0“ unterscheidet. Für das Anlegen eines Tickets ist genau das
    /// <b>nicht</b> gemessen. Ein mitgeschicktes <c>"remitterId": 0</c> wäre die Behauptung, es
    /// gebe einen Auftraggeber mit der Kennung 0; ein fehlendes Feld behauptet nichts. Im
    /// Zweifel also schweigen.</para>
    /// <para>Ein privater Satztyp wie <c>RemoteSupportRepository.RemoteSupportQuery</c>: Der
    /// Rumpf ist Sache dieser Route und hat außerhalb nichts zu suchen.</para>
    /// </remarks>
    private sealed record TicketCreateBody
    {
        [JsonPropertyName("companyId")]
        public required int CompanyId { get; init; }

        [JsonPropertyName("title")]
        public required string Title { get; init; }

        [JsonPropertyName("content")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Content { get; init; }

        [JsonPropertyName("remitterId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? RemitterId { get; init; }

        [JsonPropertyName("assignedToEmployeeId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? AssignedToEmployeeId { get; init; }
    }
}
