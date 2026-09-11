using System.Net;
using Xunit;

namespace TanssLogWatcher.Monitoring.Tests;

/// <summary>
/// Der Zwischenspeicher der Rückwärtsauflösung. Kein echter Namensdienst beteiligt — der Nachschlag
/// ist eine Attrappe.
/// </summary>
public sealed class HostNameResolverTests
{
    private static readonly IPAddress Peer = IPAddress.Parse("203.0.113.5");
    private static readonly DateTimeOffset Start = new(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GefundenerNameWirdZwischengespeichert()
    {
        int lookups = 0;
        using HostNameResolver sut = Build(clock: new ManualTimeProvider(Start), lookup: _ =>
        {
            Interlocked.Increment(ref lookups);
            return "sftp.kunde.example";
        });

        Assert.Equal("sftp.kunde.example", await sut.ResolveAsync(Peer));
        Assert.Equal("sftp.kunde.example", sut.GetCachedHostName(Peer));
        Assert.Equal("sftp.kunde.example", sut.GetCachedHostName(Peer));
        Assert.Equal(1, lookups);
    }

    [Fact]
    public async Task UnbekannteAdresseErgibtNull()
    {
        using HostNameResolver sut = Build(new ManualTimeProvider(Start), _ => null);

        Assert.Null(await sut.ResolveAsync(Peer));
        Assert.Null(sut.GetCachedHostName(Peer));
    }

    /// <summary>
    /// Ein abgelaufener Eintrag wird weiter ausgeliefert, während im Hintergrund erneuert wird.
    /// Sonst änderte ein kurz ausgefallener Namensdienst den Zieltext — und jede Änderung des Ziels
    /// ist für die Zustandsmaschine ein Ereignis.
    /// </summary>
    [Fact]
    public async Task AbgelaufenerEintragWirdWeiterAusgeliefert()
    {
        ManualTimeProvider clock = new(Start);
        using HostNameResolver sut = Build(clock, _ => "sftp.kunde.example");
        await sut.ResolveAsync(Peer);

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal("sftp.kunde.example", sut.GetCachedHostName(Peer));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("sftp.kunde.example", sut.GetCachedHostName(Peer));
    }

    /// <summary>
    /// Ein Fehltreffer hält kürzer als ein Treffer: ein Rechner, dessen Name eben erst eingetragen
    /// wurde, soll nicht zehn Minuten lang namenlos bleiben.
    /// </summary>
    [Fact]
    public async Task FehltrefferWirdFruehererErneuertAlsEinTreffer()
    {
        ManualTimeProvider clock = new(Start);
        int lookups = 0;
        using HostNameResolver sut = Build(clock, _ =>
        {
            Interlocked.Increment(ref lookups);
            return null;
        });

        await sut.ResolveAsync(Peer);
        Assert.Equal(1, Volatile.Read(ref lookups));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(sut.GetCachedHostName(Peer));
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref lookups));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(sut.GetCachedHostName(Peer));
        Assert.True(await WaitForAsync(() => Volatile.Read(ref lookups) >= 2));
    }

    [Fact]
    public async Task TrefferWirdInnerhalbSeinerHaltbarkeitNichtErneuert()
    {
        ManualTimeProvider clock = new(Start);
        int lookups = 0;
        using HostNameResolver sut = Build(clock, _ =>
        {
            Interlocked.Increment(ref lookups);
            return "sftp.kunde.example";
        });

        await sut.ResolveAsync(Peer);
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal("sftp.kunde.example", sut.GetCachedHostName(Peer));

        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref lookups));
    }

    /// <summary>
    /// Der Abruf darf niemals warten. Selbst wenn der Namensdienst hängt, kehrt er sofort zurück —
    /// genau das tat die Vorlage nicht.
    /// </summary>
    [Fact]
    public void AbrufWartetNichtAufDenNamensdienst()
    {
        using ManualResetEventSlim hang = new(false);
        using HostNameResolver sut = Build(new ManualTimeProvider(Start), _ =>
        {
            hang.Wait(TimeSpan.FromSeconds(5));
            return "zu spät";
        });

        DateTimeOffset before = DateTimeOffset.UtcNow;
        Assert.Null(sut.GetCachedHostName(Peer));
        Assert.True(DateTimeOffset.UtcNow - before < TimeSpan.FromSeconds(1));

        hang.Set();
    }

    /// <summary>Viele gleichzeitige Abrufe lösen genau einen Nachschlag aus.</summary>
    [Fact]
    public void GleichzeitigeAbrufeLoesenNurEinenNachschlagAus()
    {
        using ManualResetEventSlim gate = new(false);
        int lookups = 0;
        using HostNameResolver sut = Build(new ManualTimeProvider(Start), _ =>
        {
            Interlocked.Increment(ref lookups);
            gate.Wait(TimeSpan.FromSeconds(5));
            return "sftp.kunde.example";
        });

        Parallel.For(0, 64, _ => sut.GetCachedHostName(Peer));

        int observed = Volatile.Read(ref lookups);
        Assert.True(observed <= 1, $"Erwartet hoechstens ein Nachschlag, tatsaechlich {observed}.");
        gate.Set();
    }

    [Fact]
    public async Task HaengenderNachschlagLaeuftInDieZeitgrenze()
    {
        using ManualResetEventSlim hang = new(false);
        using HostNameResolver sut = Build(new ManualTimeProvider(Start), _ =>
        {
            hang.Wait(TimeSpan.FromSeconds(5));
            return "zu spät";
        }, timeout: TimeSpan.FromMilliseconds(50));

        Assert.Null(await sut.ResolveAsync(Peer));

        hang.Set();
    }

    /// <summary>
    /// Ein zweites <c>Dispose</c> ist folgenlos. Ein <c>using</c> und der DI-Behälter entsorgen
    /// beide — ohne diese Zusage risse das Herunterfahren mit einer
    /// <see cref="ObjectDisposedException"/> ab.
    /// </summary>
    [Fact]
    public async Task DisposeIstMehrfachAufrufbar()
    {
        using HostNameResolver sut = Build(new ManualTimeProvider(Start), _ => "sftp.kunde.example");
        await sut.ResolveAsync(Peer);

        sut.Dispose();
        sut.Dispose();

        Assert.Equal("sftp.kunde.example", sut.GetCachedHostName(Peer));
    }

    /// <summary>
    /// Auch ein unerwarteter Fehlschlag des Nachschlags wird negativ zwischengespeichert und
    /// verlässt <c>ResolveAsync</c> nicht.
    /// </summary>
    /// <remarks>
    /// Sonst fände der Abruf dauerhaft nichts und stieße in jedem Durchlauf erneut eine
    /// Hintergrundauflösung für dieselbe Adresse an — genau die Dauerbelastung des Namensdienstes,
    /// die der Vorlage vorzuwerfen ist.
    /// </remarks>
    [Fact]
    public async Task UnerwarteterFehlschlagWirdNegativZwischengespeichert()
    {
        ManualTimeProvider clock = new(Start);
        int lookups = 0;
        using HostNameResolver sut = Build(clock, _ =>
        {
            Interlocked.Increment(ref lookups);
            throw new InvalidOperationException("Der Namensdienst antwortet mit Unsinn.");
        });

        Assert.Null(await sut.ResolveAsync(Peer));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(sut.GetCachedHostName(Peer));
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref lookups));
    }

    /// <summary>
    /// Der Zwischenspeicher hält seine Größengrenze. Das Werkzeug läuft wochenlang als
    /// Tray-Anwendung; ohne Räumung wüchse die Tabelle mit jeder neuen Gegenstelle.
    /// </summary>
    [Fact]
    public async Task ZwischenspeicherHaeltDieGroessengrenze()
    {
        ManualTimeProvider clock = new(Start);
        using HostNameResolver sut = Build(clock, address => $"host-{address}.example", maxEntries: 3);

        for (int i = 1; i <= 5; i++)
        {
            await sut.ResolveAsync(IPAddress.Parse($"203.0.113.{i}"));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // Entsorgt, damit ein Abruf keine Hintergrunderneuerung mehr anstoesst und die Beobachtung
        // stabil bleibt.
        sut.Dispose();
        Assert.Null(sut.GetCachedHostName(IPAddress.Parse("203.0.113.1")));
        Assert.Null(sut.GetCachedHostName(IPAddress.Parse("203.0.113.2")));
        Assert.NotNull(sut.GetCachedHostName(IPAddress.Parse("203.0.113.5")));
    }

    /// <summary>
    /// Ein Eintrag, der weit über seine Haltbarkeit hinaus ist, wird geräumt. Bis dahin wird er
    /// weiter ausgeliefert — das bleibt die Zusage der Klasse.
    /// </summary>
    [Fact]
    public async Task LangeAbgelaufeneEintraegeWerdenGeraeumt()
    {
        ManualTimeProvider clock = new(Start);
        using HostNameResolver sut = Build(clock, address => $"host-{address}.example",
                                           retention: TimeSpan.FromMinutes(30));
        IPAddress forgotten = IPAddress.Parse("203.0.113.1");
        await sut.ResolveAsync(forgotten);

        clock.Advance(TimeSpan.FromHours(2));
        await sut.ResolveAsync(IPAddress.Parse("203.0.113.2"));

        // Entsorgt, damit der Abruf keine Hintergrunderneuerung mehr anstoesst.
        sut.Dispose();
        Assert.Null(sut.GetCachedHostName(forgotten));
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return false;
    }

    private static HostNameResolver Build(ManualTimeProvider clock, Func<IPAddress, string?> lookup,
                                          TimeSpan? timeout = null, int? maxEntries = null,
                                          TimeSpan? retention = null) =>
        new(clock, null, new HostNameResolverOptions
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(2),
            MaxEntries = maxEntries ?? 1024,
            Retention = retention ?? TimeSpan.FromHours(1),
            Lookup = (address, _) => Task.Run(() => lookup(address)),
        });
}
