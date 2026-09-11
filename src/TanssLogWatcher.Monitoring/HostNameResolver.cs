using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TanssLogWatcher.Monitoring;

/// <summary>
/// Rückwärtsauflösung mit Zwischenspeicher, die den Durchlauf niemals anhält.
/// </summary>
/// <remarks>
/// <para>Die Vorlage rief <c>Dns.GetHostEntry</c> mitten in der Erkennungsschleife auf, ohne
/// Zwischenspeicher und ohne Zeitgrenze. Eine einzige nicht antwortende Gegenstelle legte damit
/// die gesamte Sitzungserkennung für Sekunden still.</para>
/// <para><b>Veraltete Einträge werden weiter ausgeliefert, während im Hintergrund erneuert wird.</b>
/// Das ist Absicht: ein zwischendurch ausgefallener Namensdienst würde sonst den Zieltext ändern,
/// und jede Änderung des Ziels ist für die Zustandsmaschine ein Ereignis.</para>
/// <para><b>Der Zwischenspeicher wird geräumt.</b> Das Werkzeug läuft wochenlang als
/// Tray-Anwendung; ohne <see cref="HostNameResolverOptions.Retention"/> und
/// <see cref="HostNameResolverOptions.MaxEntries"/> bliebe jede jemals gesehene Gegenstelle bis zum
/// Programmende im Speicher.</para>
/// </remarks>
public sealed class HostNameResolver : IHostNameResolver, IDisposable
{
    private readonly ConcurrentDictionary<IPAddress, CacheEntry> cache = new();
    private readonly ConcurrentDictionary<IPAddress, byte> inFlight = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly TimeProvider clock;
    private readonly ILogger<HostNameResolver> logger;
    private readonly HostNameResolverOptions options;
    private int disposed;

    /// <summary>Erzeugt den Auflöser.</summary>
    /// <param name="clock">Zeitgeber; <c>null</c> nimmt die Systemuhr.</param>
    /// <param name="logger">Protokoll; <c>null</c> schaltet es ab.</param>
    /// <param name="options">Stellschrauben; <c>null</c> nimmt die Voreinstellungen.</param>
    public HostNameResolver(TimeProvider? clock = null, ILogger<HostNameResolver>? logger = null,
                            HostNameResolverOptions? options = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<HostNameResolver>.Instance;
        this.options = options ?? new HostNameResolverOptions();
    }

    /// <inheritdoc />
    public string? GetCachedHostName(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (cache.TryGetValue(address, out CacheEntry entry))
        {
            if (clock.GetUtcNow() < entry.ExpiresAt)
            {
                return entry.Name;
            }

            ScheduleRefresh(address);
            return entry.Name;
        }

        ScheduleRefresh(address);
        return null;
    }

    /// <inheritdoc />
    public async Task<string?> ResolveAsync(IPAddress address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        string? name = null;
        try
        {
            string? found = await options.Lookup(address, ct).WaitAsync(options.Timeout, ct)
                                         .ConfigureAwait(false);
            name = string.IsNullOrWhiteSpace(found) ? null : found;
        }
        catch (SocketException)
        {
            // Kein PTR-Eintrag. Ein voellig normaler Fall, kein Fehler.
        }
        catch (OperationCanceledException)
        {
            // Abbruch beim Herunterfahren. Der naechste Start versucht es erneut.
        }
        catch (TimeoutException)
        {
            // Zeitgrenze. Der naechste Durchlauf versucht es erneut.
        }
        catch (ArgumentException)
        {
            // Eine Adresse, die der Namensdienst nicht annimmt.
        }
        catch (Exception ex)
        {
            // Jeder andere Fehlschlag des Nachschlags — ein eigener Nachschlag darf alles werfen.
            // Ohne diesen Zweig bliebe der Eintrag ungeschrieben, jeder Durchlauf stiesse dieselbe
            // Aufloesung erneut an, und die Ausnahme verliesse ResolveAsync.
            logger.LogWarning(ex, "Rückwärtsauflösung unerwartet fehlgeschlagen; " +
                                  "die Adresse gilt bis zum Ablauf der Negativfrist als namenlos.");
        }

        DateTimeOffset now = clock.GetUtcNow();
        cache[address] = new CacheEntry(name, now + (name is null ? options.NegativeTtl : options.PositiveTtl));
        Evict(now);
        return name;
    }

    /// <summary>Gibt die Hintergrundauflösungen auf. Mehrfaches Aufrufen ist folgenlos.</summary>
    /// <remarks>
    /// Der Auflöser wird sowohl von einem <c>using</c> als auch vom DI-Behälter entsorgt. Ein
    /// zweites <c>Cancel</c> auf der bereits verworfenen Abbruchsteuerung würde eine
    /// <see cref="ObjectDisposedException"/> werfen und damit das Herunterfahren abreißen.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        shutdown.Cancel();
        shutdown.Dispose();
    }

    private void ScheduleRefresh(IPAddress address)
    {
        if (shutdown.IsCancellationRequested || !inFlight.TryAdd(address, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ResolveAsync(address, shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Rückwärtsauflösung fehlgeschlagen.");
            }
            finally
            {
                inFlight.TryRemove(address, out _);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Räumt den Zwischenspeicher: erst alles, was lange über seiner Haltbarkeit liegt, dann — falls
    /// nötig — die am längsten nicht mehr erneuerten Einträge bis zur Größengrenze.
    /// </summary>
    /// <remarks>
    /// Das Werkzeug läuft als Tray-Anwendung wochenlang durch; bei den verbindungsbasierten Profilen
    /// käme sonst mit jeder neuen Gegenstelle ein Eintrag hinzu, der nie wieder verschwindet.
    /// <para>Die Zusage, veraltete Einträge weiter auszuliefern, gilt weiterhin — bis zu
    /// <see cref="HostNameResolverOptions.Retention"/> beziehungsweise bis zur Größengrenze. Wird
    /// ein geräumter Name später wieder gebraucht, kostet das einen Nachschlag, keinen Sitzungsbruch:
    /// der Bezeichner einer Sitzung ist die Adresse, nicht der Name.</para>
    /// </remarks>
    private void Evict(DateTimeOffset now)
    {
        foreach (KeyValuePair<IPAddress, CacheEntry> pair in cache)
        {
            if (pair.Value.ExpiresAt + options.Retention <= now)
            {
                _ = cache.TryRemove(pair);
            }
        }

        if (options.MaxEntries <= 0 || cache.Count <= options.MaxEntries)
        {
            return;
        }

        KeyValuePair<IPAddress, CacheEntry>[] ordered = [.. cache];
        Array.Sort(ordered, static (left, right) => left.Value.ExpiresAt.CompareTo(right.Value.ExpiresAt));
        for (int i = 0; i < ordered.Length - options.MaxEntries; i++)
        {
            _ = cache.TryRemove(ordered[i]);
        }
    }

    private readonly record struct CacheEntry(string? Name, DateTimeOffset ExpiresAt);
}

/// <summary>Stellschrauben der Rückwärtsauflösung.</summary>
public sealed record HostNameResolverOptions
{
    /// <summary>Haltbarkeit eines gefundenen Namens.</summary>
    public TimeSpan PositiveTtl { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Haltbarkeit eines Fehltreffers. Kürzer, damit ein eben erst eingetragener Name nicht
    /// zehn Minuten lang ungenutzt bleibt.
    /// </summary>
    public TimeSpan NegativeTtl { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Zeitgrenze einer einzelnen Auflösung.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Wie lange ein Eintrag über seine Haltbarkeit hinaus aufgehoben wird, damit er weiter
    /// ausgeliefert werden kann, während im Hintergrund erneuert wird. Danach wird er geräumt.
    /// </summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Die Obergrenze der Tabelle. Wird sie überschritten, weichen die am längsten nicht mehr
    /// erneuerten Einträge. <c>0</c> oder kleiner schaltet die Größengrenze ab.
    /// </summary>
    public int MaxEntries { get; init; } = 1024;

    /// <summary>
    /// Der eigentliche Nachschlag. Voreingestellt ist die Rückwärtsauflösung über
    /// <see cref="Dns"/>; austauschbar, damit die Zwischenspeicherlogik ohne Namensdienst geprüft
    /// werden kann.
    /// </summary>
    /// <remarks>
    /// <see cref="Dns"/> kennt für eine <see cref="IPAddress"/> keine Abbruchsteuerung. Die
    /// Zeitgrenze wird deshalb nur für den Aufrufer durchgesetzt; der Aufruf selbst läuft im
    /// Hintergrund aus.
    /// </remarks>
    public Func<IPAddress, CancellationToken, Task<string?>> Lookup { get; init; } =
        static async (address, _) =>
        {
            IPHostEntry entry = await Dns.GetHostEntryAsync(address).ConfigureAwait(false);
            return entry.HostName;
        };
}
