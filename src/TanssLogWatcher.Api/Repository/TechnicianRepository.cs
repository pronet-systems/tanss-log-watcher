using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.Api.Repository;

/// <summary>Die Techniker der Instanz.</summary>
/// <remarks>
/// Liegt auf <c>/api/tanss.x/v1</c> und darf deshalb <b>kein</b> <c>loggedInUserId</c> tragen.
/// Dient der Einrichtung: hier findet der Benutzer seine eigene Mitarbeiter-ID, die anschließend
/// die gesamte Attribution der Fernwartungen trägt.
/// </remarks>
public sealed class TechnicianRepository : ITechnicianRepository
{
    private readonly ITanssClient _client;

    /// <summary>Baut das Repository.</summary>
    /// <param name="client">Der HTTP-Zugang.</param>
    public TechnicianRepository(ITanssClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Technician>> ListAsync(CancellationToken ct = default)
    {
        List<Technician>? technicians = await _client
            .GetAsync<List<Technician>>(TanssRoutes.Technicians, ct: ct).ConfigureAwait(false);

        return technicians ?? [];
    }
}
