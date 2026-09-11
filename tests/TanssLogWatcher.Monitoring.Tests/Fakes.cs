using System.Net;
using TanssLogWatcher.Monitoring.Model;

namespace TanssLogWatcher.Monitoring.Tests;

/// <summary>Eine Uhr, die nur vorrückt, wenn der Test es sagt.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset now;

    public ManualTimeProvider(DateTimeOffset start) => now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan amount) => now += amount;
}

/// <summary>Fensterliste, die der Test je Durchlauf setzt.</summary>
internal sealed class FakeWindowSource : IWindowSource
{
    public List<WindowInfo> Windows { get; } = [];

    /// <summary>Wird gesetzt, schlägt die Aufzählung fehl — wie ein abgeschalteter Fensterdienst.</summary>
    public Exception? Failure { get; set; }

    public int Calls { get; private set; }

    public IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        Calls++;
        return Failure is null ? [.. Windows] : throw Failure;
    }

    public FakeWindowSource With(params WindowInfo[] windows)
    {
        Windows.Clear();
        Windows.AddRange(windows);
        return this;
    }
}

/// <summary>Prozessliste, die der Test je Durchlauf setzt.</summary>
internal sealed class FakeProcessSource : IProcessSource
{
    public List<ProcessInfo> Processes { get; } = [];

    public Exception? Failure { get; set; }

    public IReadOnlyList<ProcessInfo> Snapshot()
    {
        return Failure is null ? [.. Processes] : throw Failure;
    }

    public FakeProcessSource With(params ProcessInfo[] processes)
    {
        Processes.Clear();
        Processes.AddRange(processes);
        return this;
    }
}

/// <summary>TCP-Gegenstellen je Prozess, frei setzbar.</summary>
internal sealed class FakeTcpConnectionSource : ITcpConnectionSource
{
    public Dictionary<int, List<IPAddress>> ByProcess { get; } = [];

    /// <summary>Prozesse, deren Abfrage fehlschlägt — der Fall „gerade gestorben“.</summary>
    public HashSet<int> Failing { get; } = [];

    public IReadOnlyList<IPAddress> GetRemoteAddresses(int processId, IEnumerable<int> childIds)
    {
        if (Failing.Contains(processId))
        {
            throw new InvalidOperationException($"Prozess {processId} ist verschwunden.");
        }

        List<IPAddress> result = [];
        if (ByProcess.TryGetValue(processId, out List<IPAddress>? own))
        {
            result.AddRange(own);
        }

        foreach (int child in childIds)
        {
            if (ByProcess.TryGetValue(child, out List<IPAddress>? fromChild))
            {
                result.AddRange(fromChild);
            }
        }

        return result;
    }

    public FakeTcpConnectionSource Set(int processId, params string[] addresses)
    {
        ByProcess[processId] = [.. addresses.Select(IPAddress.Parse)];
        return this;
    }
}

/// <summary>Rückwärtsauflösung aus einer festen Tabelle.</summary>
internal sealed class FakeHostNameResolver : IHostNameResolver
{
    public Dictionary<string, string> Names { get; } = [];

    public string? GetCachedHostName(IPAddress address) =>
        Names.TryGetValue(address.ToString(), out string? name) ? name : null;

    public Task<string?> ResolveAsync(IPAddress address, CancellationToken ct = default) =>
        Task.FromResult(GetCachedHostName(address));
}
