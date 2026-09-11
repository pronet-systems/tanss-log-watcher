using TanssLogWatcher.Api.Contract;

namespace TanssLogWatcher.Api.Tests.Fakes;

/// <summary>
/// Ein Zugang, der nur den Grundvertrag erfüllt — <c>ITanssBodyDelete</c> ausdrücklich nicht.
/// </summary>
/// <remarks>
/// Damit lässt sich prüfen, dass das Löschen eines Timers in diesem Fall als Programmierfehler
/// gemeldet wird, statt über einen Abfrageparameter auszuweichen, den TANSS 10.10.0 gar nicht
/// kennt. Jeder Aufruf zählt mit: der Test verlangt, dass gar nicht erst gesendet wird.
/// </remarks>
internal sealed class LimitedClient : ITanssClient
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task<T?> GetAsync<T>(string path, IDictionary<string, string?>? query = null,
                                CancellationToken ct = default) =>
        Task.FromResult(Count<T>());

    public Task<T?> PutAsync<T>(string path, object? body = null,
                                IDictionary<string, string?>? query = null,
                                CancellationToken ct = default) =>
        Task.FromResult(Count<T>());

    public Task<T?> PostAsync<T>(string path, object? body = null,
                                 IDictionary<string, string?>? query = null,
                                 CancellationToken ct = default) =>
        Task.FromResult(Count<T>());

    public Task DeleteAsync(string path, IDictionary<string, string?>? query = null,
                            CancellationToken ct = default)
    {
        Count<object>();
        return Task.CompletedTask;
    }

    public Task<(T? Content, IReadOnlyDictionary<string, object?> Meta)> PostWithMetaAsync<T>(
        string path, object? body = null, IDictionary<string, string?>? query = null,
        CancellationToken ct = default) =>
        Task.FromResult<(T?, IReadOnlyDictionary<string, object?>)>(
            (Count<T>(), new Dictionary<string, object?>(StringComparer.Ordinal)));

    public void Dispose()
    {
    }

    private T? Count<T>()
    {
        Interlocked.Increment(ref _calls);
        return default;
    }
}
