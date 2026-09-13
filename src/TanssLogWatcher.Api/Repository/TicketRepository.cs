using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.Api.Repository;

/// <summary>Die eigenen Tickets des angemeldeten Technikers.</summary>
/// <remarks>
/// „Eigen“ bestimmt der Server aus <c>loggedInUserId</c>, nicht aus einem Filter. Der Client
/// hängt den Parameter auf dieser <c>/api/v1</c>-Route selbst an; fehlte er, käme keine leere
/// Liste, sondern eine 403.
/// </remarks>
public sealed class TicketRepository : ITicketRepository
{
    private readonly ITanssClient _client;

    /// <summary>Baut das Repository.</summary>
    /// <param name="client">Der HTTP-Zugang.</param>
    public TicketRepository(ITanssClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
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
    public async Task<Ticket?> FindAsync(int ticketId, CancellationToken ct = default)
    {
        if (ticketId <= 0)
        {
            return null;
        }

        try
        {
            return await _client.GetAsync<Ticket>(TanssRoutes.TicketById(ticketId), ct: ct)
                .ConfigureAwait(false);
        }
        catch (TanssNotFoundException)
        {
            // Die Antwort auf "gibt es dieses Ticket?" lautet nein. Das ist kein Fehlschlag,
            // und der Aufrufer soll dafuer keinen Fang schreiben muessen.
            return null;
        }
    }
}
