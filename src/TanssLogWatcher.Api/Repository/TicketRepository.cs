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
}
