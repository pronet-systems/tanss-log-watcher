using TanssLogWatcher.Storage.Queue;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

public sealed class BackoffTests
{
    [Fact]
    public void Die_Rueckstauzeit_waechst()
    {
        TimeSpan previous = TimeSpan.Zero;

        for (int attempt = 1; attempt <= 8; attempt++)
        {
            TimeSpan current = Backoff.For(attempt, jitter: 0.5);
            Assert.True(current > previous,
                $"Versuch {attempt}: {current} ist nicht größer als {previous}.");
            previous = current;
        }
    }

    [Fact]
    public void Der_Deckel_haelt_auch_bei_groesster_Streuung()
    {
        for (int attempt = 1; attempt <= 200; attempt++)
        {
            foreach (double jitter in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                Assert.True(Backoff.For(attempt, jitter) <= Backoff.Cap,
                    $"Versuch {attempt} bei Streuung {jitter} überschreitet den Deckel.");
            }
        }
    }

    [Fact]
    public void Ohne_Streuung_ist_es_die_reine_Verdopplung()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), Backoff.For(1, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(30), Backoff.For(2, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(60), Backoff.For(3, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(120), Backoff.For(4, 0.5));
    }

    [Fact]
    public void Die_Streuung_bleibt_in_ihren_Grenzen()
    {
        TimeSpan plain = Backoff.For(5, 0.5);

        TimeSpan tolerance = TimeSpan.FromSeconds(1);
        Assert.InRange(Backoff.For(5, 0.0), (plain * 0.8) - tolerance, (plain * 0.8) + tolerance);
        Assert.InRange(Backoff.For(5, 1.0), (plain * 1.2) - tolerance, (plain * 1.2) + tolerance);
    }

    [Fact]
    public void Der_Deckel_wird_tatsaechlich_erreicht()
    {
        // Ohne diesen Fall pruefte der Deckel-Test nur Werte, die ihn nie beruehren.
        Assert.Equal(Backoff.Cap, Backoff.For(40, 0.5));
        Assert.Equal(Backoff.Cap, Backoff.For(40, 1.0));
    }

    [Fact]
    public void Ein_unsinniger_Versuchszaehler_ergibt_trotzdem_einen_brauchbaren_Wert()
    {
        Assert.Equal(Backoff.For(1, 0.5), Backoff.For(0, 0.5));
        Assert.Equal(Backoff.For(1, 0.5), Backoff.For(-3, 0.5));
        Assert.True(Backoff.For(1, 0.0) >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Die_zufaellige_Streuung_bleibt_im_selben_Rahmen()
    {
        TimeSpan plain = Backoff.For(3, 0.5);

        for (int round = 0; round < 200; round++)
        {
            TimeSpan drawn = Backoff.Next(3);
            Assert.InRange(drawn, plain * 0.8, plain * 1.2);
        }
    }

    [Fact]
    public void Der_naechste_Versuch_liegt_in_der_Zukunft()
    {
        DateTimeOffset now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

        DateTimeOffset next = Backoff.NextAttemptAfter(now, attempt: 2);

        Assert.InRange(next, now + (Backoff.Base * 2 * 0.8), now + (Backoff.Base * 2 * 1.2));
    }
}
