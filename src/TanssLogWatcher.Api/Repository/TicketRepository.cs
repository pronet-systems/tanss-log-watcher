using System.Globalization;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.Api.Repository;

/// <summary>Die eigenen Tickets des angemeldeten Technikers.</summary>
/// <remarks>
/// „Eigen“ bestimmt der Server aus <c>loggedInUserId</c>, nicht aus einem Filter. Der Client
/// hängt den Parameter auf dieser <c>/api/v1</c>-Route selbst an; fehlte er, käme keine leere
/// Liste, sondern eine 403.
/// </remarks>
public sealed class TicketRepository : ITicketRepository, ITicketVerification
{
    /// <summary>Der Satzteil, der an jede Erklärung eines unklaren Ausgangs gehängt wird.</summary>
    /// <remarks>
    /// Steht an einer Stelle, weil er in jedem einzelnen Fall gelten muss: Eine misslungene
    /// Prüfung darf das Buchen nicht kosten (Hausregel 5). Wer einen Zweig hinzufügt und diesen
    /// Satz vergisst, baut genau die Falle wieder ein, gegen die dieser Typ gerichtet ist.
    /// </remarks>
    private const string BookingStaysPossible =
        " Gebucht werden kann trotzdem — die Prüfung ist ein Hinweis, keine Schranke.";

    private readonly ITanssClient _client;
    private readonly ITanssMetaRead? _meta;

    /// <summary>Baut das Repository.</summary>
    /// <remarks>
    /// Erfüllt der Zugang zusätzlich <see cref="ITanssMetaRead"/>, liest die Ticketprüfung den
    /// <c>meta</c>-Block mit und kann den Firmennamen nennen. Erfüllt er ihn nicht, läuft alles
    /// wie bisher, nur ohne Namen — das kostet Bequemlichkeit, nicht die Prüfung.
    /// </remarks>
    /// <param name="client">Der HTTP-Zugang.</param>
    public TicketRepository(ITanssClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _meta = client as ITanssMetaRead;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Ticket>> ListOwnAsync(CancellationToken ct = default)
    {
        List<Ticket>? tickets = await _client
            .GetAsync<List<Ticket>>(TanssRoutes.OwnTickets, ct: ct).ConfigureAwait(false);

        return tickets ?? [];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Ticket>> SearchOpenAsync(int employeeId,
                                                             CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(employeeId);

        // PUT, obwohl es liest: Der Filter geht im Rumpf, und dafuer hat ein GET keinen Platz.
        // Wiederholen ist hier unbedenklich - der Aufruf aendert nichts.
        List<Ticket>? tickets = await _client
            .PutAsync<List<Ticket>>(TanssRoutes.TicketSearch,
                new TicketSearch { Staff = [employeeId], IncludeDoneTickets = false }, ct: ct)
            .ConfigureAwait(false);

        return tickets is null
            ? []
            : [.. tickets.OrderByDescending(ticket => ticket.Id)];
    }

    /// <inheritdoc />
    public async Task<CompanyTickets> ListForCompanyAsync(int companyId,
                                                          CancellationToken ct = default)
    {
        if (companyId <= 0)
        {
            // Ohne Firma gibt es nichts zu filtern. Ungefiltert zu fragen kaeme einer
            // Ticketliste des ganzen Hauses gleich - gemessen 101 statt 20 Eintraege.
            return new CompanyTickets
            {
                Outcome = CompanyTicketOutcome.Undetermined,
                Explanation = "Ohne Firma lässt sich keine Ticketliste abrufen. Es wurde nicht "
                    + "gefragt.",
            };
        }

        TicketSearch filter = new() { Companies = [companyId], IncludeDoneTickets = false };

        try
        {
            (IReadOnlyList<Ticket> tickets, LinkedEntities names) =
                await SearchAsync(filter, ct).ConfigureAwait(false);

            // Der Name kommt aus dem Umschlag oder gar nicht: Aus einer companyId einen Namen
            // zu holen, gibt es in der ganzen Beschreibung keinen Weg (zu /api/v1/companies
            // steht nur post). Find und nicht CompanyName - jenes lieferte "Firma 886" und
            // liesse sich hier nicht mehr von einem echten Namen unterscheiden.
            string? company = names.Find(LinkedEntities.Companies, companyId);

            return tickets.Count > 0
                ? new CompanyTickets
                {
                    Outcome = CompanyTicketOutcome.Found,
                    Tickets = tickets,
                    CompanyName = company,
                    Explanation = string.Create(CultureInfo.CurrentCulture,
                        $"{tickets.Count} offene(s) Ticket(s) dieser Firma. Erledigte sind nicht "
                        + $"dabei — auf ein abgeschlossenes Ticket zu buchen ist fast immer ein "
                        + $"Versehen."),
                }
                : new CompanyTickets
                {
                    Outcome = CompanyTicketOutcome.NoTickets,
                    CompanyName = company,
                    Explanation = "Diese Firma hat kein offenes Ticket. Gebucht werden kann "
                        + "trotzdem — ohne Ticketbezug oder auf eine eingetippte Nummer.",
                };
        }
        catch (TanssException ex)
        {
            // Hausregel 5: Die Liste ist hin, der Dialog nicht. Welche Rechte diese Abfrage
            // verlangt, ist uebrigens NICHT ermittelt - die Beschreibung fuehrt zu keiner Route
            // Rollen, und der einzige einschlaegige Satz steht am Feld ticket.companyId ("can
            // only be set if the user has access to the company").
            return new CompanyTickets
            {
                Outcome = CompanyTicketOutcome.Undetermined,
                Explanation = "Die Tickets dieser Firma liessen sich nicht abrufen: "
                    + Redaction.Scrub(ex.Message) + " Ob sie welche hat, ist damit nicht "
                    + "ermittelt. Gebucht werden kann trotzdem.",
            };
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Der schmale Weg.</b> Er kennt nur „Ticket“ und <see langword="null"/> und wirft bei
    /// allem anderen. Wer die drei Fälle auseinanderhalten muss — und das muss jede Oberfläche —,
    /// nimmt <see cref="CheckAsync"/>.
    /// </remarks>
    public async Task<Ticket?> FindAsync(int ticketId, CancellationToken ct = default)
    {
        if (ticketId <= 0)
        {
            return null;
        }

        try
        {
            return (await ReadAsync(ticketId, ct).ConfigureAwait(false)).Ticket;
        }
        catch (TanssNotFoundException)
        {
            // Die Antwort auf "gibt es dieses Ticket?" lautet nein. Das ist kein Fehlschlag,
            // und der Aufrufer soll dafuer keinen Fang schreiben muessen.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<TicketCheck> CheckAsync(int ticketId, CancellationToken ct = default)
    {
        if (ticketId <= 0)
        {
            // TANSS fuehrt die 0 selbst als "kein Ticket". Danach zu fragen hiesse, auf eine
            // Frage zu antworten, die niemand gestellt hat.
            return new TicketCheck
            {
                Outcome = TicketCheckOutcome.NoTicketNumber,
                TicketId = ticketId,
                Explanation = "Es wurde keine Ticketnummer angegeben. Gebucht wird ohne "
                    + "Ticketbezug.",
            };
        }

        try
        {
            (Ticket? ticket, LinkedEntities names) =
                await ReadAsync(ticketId, ct).ConfigureAwait(false);

            if (ticket is null)
            {
                // 200 mit leerem Rumpf oder null-Inhalt: weder ein Ticket noch eine Absage.
                return Undetermined(ticketId,
                    "TANSS hat mit Erfolg geantwortet, aber kein Ticket genannt.", 200);
            }

            if (ticket.Id != 0 && ticket.Id != ticketId)
            {
                // NICHT GEMESSEN, nur abgefangen: dass TANSS ein anderes Ticket herausgibt als
                // das angefragte. Faende es statt, waere das angezeigte Ticket eine Luege.
                return Undetermined(ticketId,
                    $"TANSS hat auf die Anfrage nach Ticket {ticketId} das Ticket {ticket.Id} "
                    + "geliefert. Welches gemeint ist, ist damit offen.", 200);
            }

            // Ohne Firma am Ticket bleibt der Name leer, statt "Firma 0" zu behaupten.
            string? company = ticket.CompanyId > 0 ? names.CompanyName(ticket.CompanyId) : null;

            return new TicketCheck
            {
                Outcome = TicketCheckOutcome.Exists,
                TicketId = ticketId,
                Ticket = ticket,
                CompanyName = company,
                Status = 200,
                Explanation = $"Ticket {ticketId} gibt es. Bitte prüfen, ob Titel und Firma zum "
                    + "Vorgang passen — ein Zahlendreher trifft oft ein echtes Ticket beim "
                    + "falschen Kunden.",
            };
        }
        catch (TanssNotFoundException ex)
        {
            // GEMESSEN am 13.09.2026: 404 mit error.text = OBJECT_NOT_FOUND heisst "gibt es
            // nicht". Nur dieser Beleg darf das Buchen anhalten. Ein 404 ohne diese Marke kann
            // ebenso gut eine Route sein, die es in dieser TANSS-Fassung nicht mehr gibt - die
            // hier benutzten Routen sind ueberwiegend undokumentiert -, und dafuer den
            // Techniker auszubremsen waere falsch.
            return Mentions(ex.Detail, "NOT_FOUND")
                ? new TicketCheck
                {
                    Outcome = TicketCheckOutcome.DoesNotExist,
                    TicketId = ticketId,
                    Status = ex.Status,
                    Failure = ex.Message,
                    Explanation = $"Ticket {ticketId} gibt es in TANSS nicht — TANSS meldet "
                        + "OBJECT_NOT_FOUND. Eine Leistung auf eine Nummer ohne Ticket taucht in "
                        + "keiner Auswertung auf; bitte die Nummer berichtigen oder ohne Ticket "
                        + "buchen.",
                }
                : Undetermined(ticketId,
                    $"TANSS hat die Anfrage nach Ticket {ticketId} mit 404 beantwortet, aber "
                    + "ohne OBJECT_NOT_FOUND. Das kann heißen, dass es das Ticket nicht gibt — "
                    + "oder dass es die Route in dieser TANSS-Fassung nicht mehr gibt.",
                    ex.Status, ex.Message);
        }
        catch (TanssAuthException ex)
        {
            // 403 ist NICHT GEMESSEN. Die Beschreibung nennt sie zu dieser Route, sagt aber
            // nicht, wofuer sie steht: fehlendes Recht, fremdes Ticket, abgelaufenes Token -
            // alle drei sind denkbar. Dass sie "gibt es nicht" bedeutet, ist NIRGENDS belegt,
            // und sie so zu deuten hiesse, ein vorhandenes Ticket fuer erfunden zu erklaeren.
            return Undetermined(ticketId,
                $"TANSS hat die Auskunft über Ticket {ticketId} verweigert (HTTP "
                + $"{ex.Status ?? 403}). Ob es das Ticket gibt, ist damit nicht ermittelt: Eine "
                + "403 kann ein fehlendes Recht, ein fremdes Ticket oder ein abgelaufenes Token "
                + "bedeuten.", ex.Status, ex.Message);
        }
        catch (TanssUnreachableException ex)
        {
            return Undetermined(ticketId,
                $"Ob es Ticket {ticketId} gibt, ließ sich nicht ermitteln: TANSS war nicht "
                + "erreichbar oder hat nicht rechtzeitig geantwortet.", ex.Status, ex.Message);
        }
        catch (TanssException ex)
        {
            // Der Sammelfang, und er ist Absicht: 5xx, ein unlesbarer Rumpf, eine geaenderte
            // Antwortform. Was hier ankommt, ist immer dasselbe - die Frage bleibt offen.
            return Undetermined(ticketId,
                $"Ob es Ticket {ticketId} gibt, ließ sich nicht ermitteln: TANSS hat die Anfrage "
                + "abgewiesen oder unverständlich beantwortet.", ex.Status, ex.Message);
        }
    }

    /// <summary>Baut das Ergebnis „nicht ermittelt“ samt dem Satz, der das Buchen freigibt.</summary>
    private static TicketCheck Undetermined(int ticketId, string reason, int? status = null,
                                            string? failure = null) =>
        new()
        {
            Outcome = TicketCheckOutcome.Undetermined,
            TicketId = ticketId,
            Status = status,
            Failure = failure,
            Explanation = reason + BookingStaysPossible,
        };

    /// <summary>Enthält der Fehlertext von TANSS diese Marke?</summary>
    private static bool Mentions(string? detail, string needle) =>
        detail is not null && detail.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Schickt einen Ticketfilter und gibt Inhalt <b>und</b> Namensverzeichnis heraus.
    /// </summary>
    /// <remarks>
    /// Erfüllt der Zugang <see cref="ITanssMetaRead"/> nicht, kommt
    /// <see cref="LinkedEntities.Empty"/> zurück und der Firmenname bleibt aus — derselbe
    /// HTTP-Aufruf, nur ohne Umschlag.
    /// </remarks>
    private async Task<(IReadOnlyList<Ticket> Tickets, LinkedEntities Names)> SearchAsync(
        TicketSearch filter, CancellationToken ct)
    {
        List<Ticket>? tickets;
        LinkedEntities names;

        if (_meta is null)
        {
            tickets = await _client
                .PutAsync<List<Ticket>>(TanssRoutes.TicketSearch, filter, ct: ct)
                .ConfigureAwait(false);
            names = LinkedEntities.Empty;
        }
        else
        {
            (List<Ticket>? content, IReadOnlyDictionary<string, object?> meta) =
                await _meta.PutWithMetaAsync<List<Ticket>>(TanssRoutes.TicketSearch, filter,
                                                           ct: ct).ConfigureAwait(false);
            tickets = content;
            names = LinkedEntities.From(meta);
        }

        return (tickets is null ? [] : [.. tickets.OrderByDescending(ticket => ticket.Id)],
                names);
    }

    /// <summary>
    /// Liest ein Ticket samt Namensverzeichnis — der eine Weg, den beide Abfragen nehmen.
    /// </summary>
    /// <remarks>
    /// Erfüllt der Zugang <see cref="ITanssMetaRead"/> nicht, kommt
    /// <see cref="LinkedEntities.Empty"/> zurück, und die Firma erscheint als „Firma 886“.
    /// Derselbe HTTP-Aufruf, dieselbe Wiederholungsregel — nur der Umschlag wird dann
    /// weggeworfen.
    /// </remarks>
    private async Task<(Ticket? Ticket, LinkedEntities Names)> ReadAsync(int ticketId,
                                                                        CancellationToken ct)
    {
        string route = TanssRoutes.TicketById(ticketId);

        if (_meta is null)
        {
            return (await _client.GetAsync<Ticket>(route, ct: ct).ConfigureAwait(false),
                    LinkedEntities.Empty);
        }

        (Ticket? ticket, IReadOnlyDictionary<string, object?> meta) =
            await _meta.GetWithMetaAsync<Ticket>(route, ct: ct).ConfigureAwait(false);

        return (ticket, LinkedEntities.From(meta));
    }
}
