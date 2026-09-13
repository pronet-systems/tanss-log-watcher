using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Auth;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Monitoring;
using TanssLogWatcher.Monitoring.Model;
using TanssLogWatcher.Storage.Queue;

namespace TanssLogWatcher.Cli.Tests;

/// <summary>
/// Ein Tokenspeicher im Arbeitsspeicher.
/// </summary>
/// <remarks>
/// Der echte hängt an DPAPI und damit am Windows-Konto des Ausführenden. Ein Test, der ihn
/// benutzte, schriebe in das Profil des Entwicklers und liesse sich auf einem Bauserver nicht
/// wiederholen.
/// </remarks>
internal sealed class FakeTokenStore : ITokenStore
{
    private string _token;

    public FakeTokenStore(string token) => _token = token;

    /// <summary>Was zuletzt geschrieben wurde — der Nachweis, dass ein Wechsel ankam.</summary>
    public string? Written { get; private set; }

    /// <summary>Wirft beim Lesen, statt ein Token zu liefern.</summary>
    public Exception? ReadFails { get; init; }

    public string Read() => ReadFails is null ? _token : throw ReadFails;

    public void Write(string token)
    {
        Written = token;
        _token = token;
    }

    /// <summary>
    /// Baut ein JWT mit vorgegebenem Ablauf — Kopf und Signatur sind Fülltext.
    /// </summary>
    /// <remarks>
    /// Die Signatur wird nirgends geprüft; das ist keine Nachlässigkeit des Tests, sondern die
    /// Zusage von <see cref="TanssAuth"/>: Über Zugriff entscheidet ausschliesslich TANSS, hier
    /// wird nur der Ablauf gelesen.
    /// </remarks>
    public static string JwtExpiring(DateTimeOffset expiry)
    {
        string payload = Base64Url(
            $"{{\"exp\":{expiry.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)},"
            + $"\"iat\":{(expiry.ToUnixTimeSeconds() - 86400).ToString(CultureInfo.InvariantCulture)}}}");

        return "Bearer " + Base64Url("{\"alg\":\"HS256\"}") + "." + payload + ".Fuelltext";
    }

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Ein Zugang, der nur das beantwortet, was die geprüften Befehle wirklich abfragen.
/// </summary>
/// <remarks>
/// Gebraucht wird er allein für den Trockentest der Erneuerungsfähigkeit und für die Probe
/// beim Tokenwechsel; beides läuft über <c>GET</c>.
/// </remarks>
internal sealed class FakeTanssClient : ITanssClient
{
    /// <summary>Was <c>GET /api/v1/jwts/tanss_app</c> liefert; <c>null</c> heisst „nichts“.</summary>
    public MintedToken? Minted { get; init; }

    /// <summary>Womit ein <c>GET</c> scheitert; <c>null</c> heisst „es gelingt“.</summary>
    public Exception? GetFails { get; init; }

    /// <summary>Was <c>remoteSupports/systems</c> liefert.</summary>
    public IReadOnlyList<RemoteSupportSystem> Systems { get; init; } = [];

    /// <summary>Wie oft geprägt wurde — der Nachweis für „genau ein Versuch“.</summary>
    public int MintCalls { get; private set; }

    public Task<T?> GetAsync<T>(string path, IDictionary<string, string?>? query = null,
                                CancellationToken ct = default)
    {
        if (GetFails is not null)
        {
            throw GetFails;
        }

        if (path.StartsWith(TanssRoutes.MintToken, StringComparison.Ordinal))
        {
            MintCalls++;
            return Task.FromResult((T?)(object?)Minted);
        }

        if (path.StartsWith(TanssRoutes.RemoteSupportSystems, StringComparison.Ordinal))
        {
            return Task.FromResult((T?)(object?)new List<RemoteSupportSystem>(Systems));
        }

        return Task.FromResult<T?>(default);
    }

    public Task<T?> PutAsync<T>(string path, object? body = null,
                                IDictionary<string, string?>? query = null,
                                CancellationToken ct = default) =>
        Task.FromResult<T?>(default);

    public Task<T?> PostAsync<T>(string path, object? body = null,
                                 IDictionary<string, string?>? query = null,
                                 CancellationToken ct = default) =>
        Task.FromResult<T?>(default);

    public Task DeleteAsync(string path, IDictionary<string, string?>? query = null,
                            CancellationToken ct = default) => Task.CompletedTask;

    public Task<(T? Content, IReadOnlyDictionary<string, object?> Meta)> PostWithMetaAsync<T>(
        string path, object? body = null, IDictionary<string, string?>? query = null,
        CancellationToken ct = default) =>
        Task.FromResult<(T?, IReadOnlyDictionary<string, object?>)>(
            (default, new Dictionary<string, object?>(StringComparer.Ordinal)));

    public void Dispose()
    {
    }
}

/// <summary>Die Technikerliste, wahlweise mit Fehler.</summary>
internal sealed class FakeTechnicianRepository : ITechnicianRepository
{
    public IReadOnlyList<Technician> Technicians { get; init; } = [];

    /// <summary>Womit die Abfrage scheitert; <c>null</c> heisst „sie gelingt“.</summary>
    public Exception? Fails { get; init; }

    public Task<IReadOnlyList<Technician>> ListAsync(CancellationToken ct = default) =>
        Fails is null ? Task.FromResult(Technicians) : throw Fails;
}

/// <summary>
/// Fernwartungen, mit Zählern für das, worauf es ankommt.
/// </summary>
/// <remarks>
/// <see cref="CreateCalls"/> ist der eigentliche Prüfstein: TANSS dedupliziert nicht, und die
/// Zusage dieses Werkzeugs lautet, dass eine bereits angekommene Fernwartung kein zweites Mal
/// gesendet wird. Diese Zusage ist genau dann geprüft, wenn der Zähler bei null bleibt.
/// </remarks>
internal sealed class FakeRemoteSupportRepository : IRemoteSupportRepository
{
    /// <summary>Was die Existenzprüfung antwortet.</summary>
    public bool Exists { get; init; }

    /// <summary>Womit die Existenzprüfung scheitert — der Fall „Ausgang unbekannt“.</summary>
    public Exception? ExistsFails { get; init; }

    /// <summary>Womit das Anlegen scheitert.</summary>
    public Exception? CreateFails { get; init; }

    /// <summary>Die Anbindungen der Instanz.</summary>
    public IReadOnlyList<RemoteSupportSystem> Systems { get; init; } = [];

    /// <summary>Womit die Abfrage der Anbindungen scheitert.</summary>
    public Exception? SystemsFails { get; init; }

    public int ExistsCalls { get; private set; }

    public int CreateCalls { get; private set; }

    public Task<RemoteSupportRead> CreateAsync(RemoteSupportWrite item,
                                               CancellationToken ct = default)
    {
        CreateCalls++;
        return CreateFails is null
            ? Task.FromResult(new RemoteSupportRead
            {
                Id = 38584 + CreateCalls,
                RemoteMaintenanceId = item.RemoteMaintenanceId,
                TypeId = item.TypeId,
            })
            : throw CreateFails;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Greift auf <see cref="CreateAsync"/> zurück statt einen zweiten Pfad zu führen: Zwei
    /// Anlegewege in einer Attrappe laufen auseinander, sobald jemand nur einen davon pflegt —
    /// und die Tests bezeugten dann ein Verhalten, das es nirgends gibt.
    /// </remarks>
    public async Task<RemoteSupportCreateResult> CreateWithDiagnosticsAsync(
        RemoteSupportWrite item, CancellationToken ct = default)
    {
        RemoteSupportRead created = await CreateAsync(item, ct).ConfigureAwait(false);

        return new RemoteSupportCreateResult(created, AttributionConfirmed: true, Warning: null);
    }

    public Task<bool> ExistsAsync(string remoteMaintenanceId, DateTimeOffset around,
                                  CancellationToken ct = default) =>
        ExistsAsync(remoteMaintenanceId, around, around, ct);

    public Task<bool> ExistsAsync(string remoteMaintenanceId, DateTimeOffset sessionStart,
                                  DateTimeOffset sessionEnd, CancellationToken ct = default)
    {
        ExistsCalls++;
        return ExistsFails is null ? Task.FromResult(Exists) : throw ExistsFails;
    }

    public Task<bool> ExistsAsync(RemoteSupportWrite session, CancellationToken ct = default) =>
        ExistsAsync(session.RemoteMaintenanceId, DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch, ct);

    public Task<IReadOnlyList<RemoteSupportRead>> ListAsync(Timeframe timeframe,
                                                            string? text = null,
                                                            CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RemoteSupportRead>>([]);

    public Task<IReadOnlyList<RemoteSupportSystem>> ListSystemsAsync(
        CancellationToken ct = default) =>
        SystemsFails is null ? Task.FromResult(Systems) : throw SystemsFails;
}

/// <summary>
/// Die Warteschlange im Arbeitsspeicher — mit demselben Verhalten, auf das es ankommt.
/// </summary>
/// <remarks>
/// Nachgebildet sind die Zustandswechsel, nicht die Datenbank: dass <c>MarkFailed</c> ohne
/// Folgetermin aufgibt und mit Folgetermin zurückstellt, dass <c>outcome_unknown</c> allein von
/// <c>MarkDone</c> gelöscht wird, und dass <c>Count</c> auch unlesbare Zeilen mitzählt. Genau
/// diese Feinheiten prüfen die Tests.
/// </remarks>
internal sealed class FakeUploadQueue : IUploadQueue
{
    private readonly Dictionary<string, QueuedUpload> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenSession> _open = new(StringComparer.Ordinal);

    /// <summary>Wie viele Zeilen der abgefragte Zustand als unlesbar meldet.</summary>
    public int Unreadable { get; init; }

    /// <summary>Legt einen Eintrag unmittelbar in einen Zustand.</summary>
    public void Seed(QueuedUpload item) => _items[item.RemoteMaintenanceId] = item;

    /// <summary>Der aktuelle Stand eines Eintrags.</summary>
    public QueuedUpload? Peek(string id) =>
        _items.TryGetValue(id, out QueuedUpload? item) ? item : null;

    /// <inheritdoc/>
    /// <remarks>
    /// Bildet die Bedingung der echten Warteschlange nach: Geändert wird nur, was noch wartet.
    /// Eine Attrappe, die auch geleaste Einträge änderte, liesse genau den Fehler durch, gegen
    /// den die Bedingung dort steht.
    /// </remarks>
    public bool UpdatePayload(RemoteSupportWrite payload)
    {
        if (!_items.TryGetValue(payload.RemoteMaintenanceId, out QueuedUpload? item)
            || item.State != QueueState.Pending)
        {
            return false;
        }

        _items[payload.RemoteMaintenanceId] = item with { Payload = payload };
        return true;
    }

    /// <summary>Wartete der zuletzt eingereihte Eintrag auf die Entscheidung des Technikers?</summary>
    public bool LastAwaitedDecision { get; private set; }

    /// <inheritdoc/>
    public bool Release(string remoteMaintenanceId)
    {
        if (!_items.TryGetValue(remoteMaintenanceId, out QueuedUpload? item)
            || item.State != QueueState.Pending)
        {
            return false;
        }

        _items[remoteMaintenanceId] = item with { AwaitingDecision = false };
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Das Spiegelbild von <see cref="Release"/>, mit derselben Bedingung.</remarks>
    public bool Hold(string remoteMaintenanceId)
    {
        if (!_items.TryGetValue(remoteMaintenanceId, out QueuedUpload? item)
            || item.State != QueueState.Pending)
        {
            return false;
        }

        _items[remoteMaintenanceId] = item with { AwaitingDecision = true };
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Bildet die Bedingung der echten Warteschlange nach: Zugeteilt wird ohne Rücksicht auf
    /// Fälligkeit und Warten, aber nur, was noch wartet — und das Warten auf die Entscheidung
    /// ist danach aufgehoben.
    /// </remarks>
    public QueuedUpload? LeaseOne(string remoteMaintenanceId)
    {
        if (!_items.TryGetValue(remoteMaintenanceId, out QueuedUpload? item)
            || item.State != QueueState.Pending)
        {
            return null;
        }

        _items[remoteMaintenanceId] = item with
        {
            State = QueueState.Sending,
            AwaitingDecision = false,
        };

        return _items[remoteMaintenanceId];
    }

    /// <inheritdoc/>
    public bool Remove(string remoteMaintenanceId)
    {
        if (!_items.TryGetValue(remoteMaintenanceId, out QueuedUpload? item)
            || item.State != QueueState.Pending)
        {
            return false;
        }

        return _items.Remove(remoteMaintenanceId);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Das Warten auf die Entscheidung wird mitgeschrieben, damit ein Test es prüfen kann —
    /// und es wird in <see cref="Lease"/> auch beachtet: Eine Attrappe, die eine wartende Zeile
    /// zuteilte, liesse genau den Fehler durch, gegen den das Kennzeichen steht.
    /// </remarks>
    public bool Enqueue(RemoteSupportWrite item, bool awaitDecision = false)
    {
        if (_items.ContainsKey(item.RemoteMaintenanceId))
        {
            return false;
        }

        LastAwaitedDecision = awaitDecision;

        _items[item.RemoteMaintenanceId] = new QueuedUpload
        {
            RemoteMaintenanceId = item.RemoteMaintenanceId,
            Payload = item,
            State = QueueState.Pending,
            CreatedAt = DateTimeOffset.UnixEpoch,
            NextAttemptAt = DateTimeOffset.UnixEpoch,
            AwaitingDecision = awaitDecision,
        };

        return true;
    }

    public IReadOnlyList<QueuedUpload> Lease(int max)
    {
        List<QueuedUpload> leased = [];
        foreach (string id in _items.Keys.ToList())
        {
            if (leased.Count >= max || _items[id].State != QueueState.Pending
                || _items[id].AwaitingDecision)
            {
                continue;
            }

            _items[id] = _items[id] with { State = QueueState.Sending };
            leased.Add(_items[id]);
        }

        return leased;
    }

    public void MarkDone(string remoteMaintenanceId) =>
        _items[remoteMaintenanceId] = _items[remoteMaintenanceId] with
        {
            State = QueueState.Done,
            Attempts = _items[remoteMaintenanceId].Attempts + 1,
            OutcomeUnknown = false,
            LastError = null,
        };

    public void MarkFailed(string remoteMaintenanceId, string errorText,
                           DateTimeOffset? nextAttempt) =>
        _items[remoteMaintenanceId] = _items[remoteMaintenanceId] with
        {
            // Ohne Folgetermin heisst aufgeben, mit Folgetermin heisst zurueckstellen.
            State = nextAttempt is null ? QueueState.Failed : QueueState.Pending,
            Attempts = _items[remoteMaintenanceId].Attempts + 1,
            LastError = errorText,
            NextAttemptAt = nextAttempt ?? DateTimeOffset.UnixEpoch,
        };

    public int RequeueStuck(TimeSpan olderThan)
    {
        int count = 0;
        foreach (string id in _items.Keys.ToList())
        {
            if (_items[id].State != QueueState.Sending)
            {
                continue;
            }

            _items[id] = _items[id] with
            {
                State = QueueState.Pending,
                Attempts = _items[id].Attempts + 1,
                OutcomeUnknown = true,
            };
            count++;
        }

        return count;
    }

    public QueuedUpload? Find(string remoteMaintenanceId) => Peek(remoteMaintenanceId);

    public QueueListing List(QueueState state, int limit = 100) => new()
    {
        Items = [.. _items.Values.Where(item => item.State == state).Take(limit)],
        Unreadable = Unreadable,
    };

    public int Count(QueueState state) =>
        _items.Values.Count(item => item.State == state)
        + (state == QueueState.Pending ? Unreadable : 0);

    public int PurgeCompleted(TimeSpan olderThan) => 0;

    public void SaveOpenSession(OpenSession session) =>
        _open[session.RemoteMaintenanceId] = session;

    public bool RemoveOpenSession(string remoteMaintenanceId) =>
        _open.Remove(remoteMaintenanceId);

    public IReadOnlyList<OpenSession> LoadOpenSessions() => [.. _open.Values];

    public void Dispose()
    {
    }
}

/// <summary>Eine feste Fensterliste.</summary>
internal sealed class FakeWindowSource : IWindowSource
{
    public IReadOnlyList<WindowInfo> Windows { get; init; } = [];

    public IReadOnlyList<WindowInfo> GetVisibleWindows() => Windows;
}

/// <summary>Eine feste Prozessliste.</summary>
internal sealed class FakeProcessSource : IProcessSource
{
    public IReadOnlyList<ProcessInfo> Processes { get; init; } = [];

    public IReadOnlyList<ProcessInfo> Snapshot() => Processes;
}

/// <summary>Feste Gegenstellen.</summary>
internal sealed class FakeTcpConnectionSource : ITcpConnectionSource
{
    public IReadOnlyList<IPAddress> Remotes { get; init; } = [];

    public IReadOnlyList<IPAddress> GetRemoteAddresses(int processId, IEnumerable<int> childIds) =>
        Remotes;
}

/// <summary>Nutzlasten für die Warteschlange, ohne jedes Mal alle Pflichtfelder zu tippen.</summary>
internal static class Payloads
{
    public static RemoteSupportWrite For(string id, int typeId = 1005) => new()
    {
        TypeId = typeId,
        EmployeeId = 42,
        StartTime = 1_760_000_000,
        EndTime = 1_760_003_600,
        RemoteMaintenanceId = id,
        Comment = "Fernwartung: pruefling",
    };

    /// <summary>Die Nutzlast als JSON — für Tests, die eine beschädigte Zeile brauchen.</summary>
    public static string ToJson(RemoteSupportWrite item) => JsonSerializer.Serialize(item);
}

/// <summary>Eine Rückwärtsauflösung, die nichts auflöst und nie wartet.</summary>
/// <remarks>
/// Im Durchlauf wird ohnehin nie gewartet: Die Zustandsmaschine fragt nur den
/// Zwischenspeicher. Ein Test, der echte DNS-Antworten bräuchte, hinge am Netz des
/// Bauservers.
/// </remarks>
internal sealed class FakeHostNameResolver : IHostNameResolver
{
    public string? GetCachedHostName(IPAddress address) => null;

    public Task<string?> ResolveAsync(IPAddress address, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}

/// <summary>Eine Fensterliste, die sich zwischen zwei Durchläufen ändern lässt.</summary>
/// <remarks>
/// Sitzungsbeginn und -ende entstehen ausschliesslich aus dem Unterschied zweier
/// Momentaufnahmen. Ohne eine veränderliche Quelle liesse sich ein Sitzungsende gar nicht
/// nachstellen.
/// </remarks>
internal sealed class MutableWindowSource : IWindowSource
{
    public List<WindowInfo> Windows { get; } = [];

    public IReadOnlyList<WindowInfo> GetVisibleWindows() => [.. Windows];
}
