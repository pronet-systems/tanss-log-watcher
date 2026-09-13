using Xunit;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Die Zeitachse der Aufzeichnung. Sie ist der Teil, der im Streitfall zählt.
/// </summary>
public sealed class RecordingClockTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Der Kernfall: fünfundvierzig Minuten Sitzung, fünfundzwanzig davon minimiert. Die Datei
    /// muss zwanzig Minuten lang sein und nicht fünfundvierzig.
    /// </summary>
    [Fact]
    public void Eine_Pause_zaehlt_nicht_zur_aufgezeichneten_Zeit()
    {
        RecordingClock clock = new(framesPerSecond: 4);

        clock.Start(Start);
        clock.Pause(Start + TimeSpan.FromMinutes(10));
        clock.Resume(Start + TimeSpan.FromMinutes(35));

        TimeSpan recorded = clock.Elapsed(Start + TimeSpan.FromMinutes(45));

        Assert.Equal(TimeSpan.FromMinutes(20), recorded);
    }

    [Fact]
    public void Waehrend_der_Pause_steht_die_Uhr()
    {
        RecordingClock clock = new(framesPerSecond: 4);

        clock.Start(Start);
        clock.Pause(Start + TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), clock.Elapsed(Start + TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(30), clock.Elapsed(Start + TimeSpan.FromMinutes(5)));
        Assert.True(clock.IsPaused);
        Assert.False(clock.IsRunning);
    }

    /// <summary>
    /// Die Zeitstempel werden gerechnet und nicht aufaddiert. Bei drei Bildern je Sekunde geht
    /// die Sekunde nicht glatt auf — aufaddiert liefe die Datei mit jeder Stunde weiter weg.
    /// </summary>
    [Fact]
    public void Die_Zeitstempel_driften_auch_bei_krummer_Bildrate_nicht()
    {
        RecordingClock clock = new(framesPerSecond: 3);
        clock.Start(Start);

        TimeSpan last = TimeSpan.Zero;

        // Eine Stunde bei drei Bildern je Sekunde.
        for (int i = 0; i < 3 * 3600; i++)
        {
            last = clock.NextFrameTimestamp();
        }

        // Das letzte Bild steht am Anfang seines Abschnitts, also einen Abstand vor dem Ende.
        long ticksPerFrame = TimeSpan.TicksPerSecond / 3;
        Assert.Equal(TimeSpan.FromTicks(((3 * 3600) - 1) * ticksPerFrame), last);
        Assert.Equal(TimeSpan.FromTicks(3 * 3600 * ticksPerFrame), clock.Duration);
    }

    [Fact]
    public void Waehrend_der_Pause_wird_kein_Bild_geschrieben()
    {
        RecordingClock clock = new(framesPerSecond: 4);
        clock.Start(Start);
        _ = clock.NextFrameTimestamp();
        clock.Pause(Start + TimeSpan.FromSeconds(1));

        // Kaeme hier ein Bild durch, entstuende in der Datei Zeit, die es nicht gab.
        _ = Assert.Throws<InvalidOperationException>(() => clock.NextFrameTimestamp());
    }

    [Fact]
    public void Die_Pausen_werden_einzeln_festgehalten()
    {
        RecordingClock clock = new(framesPerSecond: 4);

        clock.Start(Start);
        clock.Pause(Start + TimeSpan.FromMinutes(5));
        clock.Resume(Start + TimeSpan.FromMinutes(8));
        clock.Pause(Start + TimeSpan.FromMinutes(20));
        clock.Resume(Start + TimeSpan.FromMinutes(21));

        Assert.Collection(clock.Pauses,
            first =>
            {
                Assert.Equal(Start + TimeSpan.FromMinutes(5), first.StartedAt);
                Assert.Equal(TimeSpan.FromMinutes(3), first.Length);
            },
            second =>
            {
                Assert.Equal(Start + TimeSpan.FromMinutes(20), second.StartedAt);
                Assert.Equal(TimeSpan.FromMinutes(1), second.Length);
            });
    }

    /// <summary>
    /// Zweimal anhalten ist erlaubt: Die Pause wird an zwei Stellen festgestellt — daran, dass
    /// keine Bilder mehr kommen, und daran, dass das Fenster minimiert ist.
    /// </summary>
    [Fact]
    public void Zweimal_Anhalten_verlaengert_die_Pause_nicht()
    {
        RecordingClock clock = new(framesPerSecond: 4);

        clock.Start(Start);
        clock.Pause(Start + TimeSpan.FromMinutes(5));
        clock.Pause(Start + TimeSpan.FromMinutes(6));
        clock.Resume(Start + TimeSpan.FromMinutes(10));

        Assert.Equal(TimeSpan.FromMinutes(5), Assert.Single(clock.Pauses).Length);
        Assert.Equal(TimeSpan.FromMinutes(10), clock.Elapsed(Start + TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void Fortsetzen_ohne_Pause_tut_nichts()
    {
        RecordingClock clock = new(framesPerSecond: 4);
        clock.Start(Start);
        clock.Resume(Start + TimeSpan.FromMinutes(1));

        Assert.Empty(clock.Pauses);
        Assert.True(clock.IsRunning);
    }

    /// <summary>
    /// Eine rückwärts laufende Wanduhr — Zeitumstellung, Zeitabgleich — darf die Aufzeichnung
    /// nicht verkürzen. Sonst stünde in der Datei weniger Zeit, als gearbeitet wurde.
    /// </summary>
    [Fact]
    public void Eine_rueckwaerts_laufende_Uhr_verkuerzt_die_Aufzeichnung_nicht()
    {
        RecordingClock clock = new(framesPerSecond: 4);

        clock.Start(Start);
        clock.Pause(Start + TimeSpan.FromMinutes(10));
        clock.Resume(Start + TimeSpan.FromMinutes(9));

        Assert.Equal(TimeSpan.Zero, Assert.Single(clock.Pauses).Length);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed(Start - TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Ein_zweiter_Start_wird_abgelehnt()
    {
        RecordingClock clock = new(framesPerSecond: 4);
        clock.Start(Start);

        _ = Assert.Throws<InvalidOperationException>(
            () => clock.Start(Start + TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(61)]
    public void Eine_unbrauchbare_Bildrate_wird_abgelehnt(int framesPerSecond)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new RecordingClock(framesPerSecond));
    }
}
