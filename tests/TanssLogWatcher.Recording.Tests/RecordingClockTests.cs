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

        long ticksPerFrame = TimeSpan.TicksPerSecond / 3;
        TimeSpan last = TimeSpan.Zero;

        // Eine Stunde bei drei Bildern je Sekunde.
        for (int i = 0; i < 3 * 3600; i++)
        {
            last = clock.NextFrameTimestamp(Start + TimeSpan.FromTicks(i * ticksPerFrame));
        }

        // Das letzte Bild steht am Anfang seines Abschnitts, also einen Abstand vor dem Ende.
        Assert.Equal(TimeSpan.FromTicks(((3 * 3600) - 1) * ticksPerFrame), last);
        Assert.Equal(TimeSpan.FromTicks(3 * 3600 * ticksPerFrame), clock.Duration);
    }

    /// <summary>
    /// Der Herzschlag: Wird nur alle zwei Sekunden ein Bild geschrieben, liegen die
    /// Zeitstempel auch zwei Sekunden auseinander.
    /// </summary>
    /// <remarks>
    /// Die Zusage, die dahintersteht: Ein stehender Bildschirm ergibt eine Datei, die so lang
    /// ist wie die Zeit, in der er stand. Läge der Zeitstempel am Bildzähler, schrumpfte eine
    /// Viertelstunde Lesen auf knapp zwei Sekunden Video — und der Abspieler zeigte eine
    /// Dauer, die es nie gab.
    /// </remarks>
    [Fact]
    public void Ein_Bild_alle_zwei_Sekunden_liegt_auch_zwei_Sekunden_auseinander()
    {
        RecordingClock clock = new(framesPerSecond: 4);
        clock.Start(Start);

        Assert.Equal(TimeSpan.Zero, clock.NextFrameTimestamp(Start));
        Assert.Equal(TimeSpan.FromSeconds(2),
                     clock.NextFrameTimestamp(Start + TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(4),
                     clock.NextFrameTimestamp(Start + TimeSpan.FromSeconds(4)));

        // Vier Sekunden plus die Standzeit des letzten Bildes.
        Assert.Equal(TimeSpan.FromSeconds(4.25), clock.Duration);
    }

    /// <summary>
    /// Die Pause fehlt in der Zeitachse. Fünf Minuten minimiert erzeugen keine fünf Minuten
    /// Datei — genau das ist der Sinn dieser Uhr.
    /// </summary>
    [Fact]
    public void Die_Pause_fehlt_in_den_Zeitstempeln()
    {
        RecordingClock clock = new(framesPerSecond: 4);

        clock.Start(Start);
        _ = clock.NextFrameTimestamp(Start + TimeSpan.FromSeconds(10));

        clock.Pause(Start + TimeSpan.FromSeconds(10));
        clock.Resume(Start + TimeSpan.FromMinutes(5));

        // Zehn Sekunden aufgezeichnet, danach eine Pause, danach eine weitere Sekunde.
        Assert.Equal(TimeSpan.FromSeconds(11),
                     clock.NextFrameTimestamp(Start + TimeSpan.FromMinutes(5)
                                              + TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// Zwei Bilder im selben Augenblick — der Takt hat einmal nachgeholt. Der zweite
    /// Zeitstempel rückt weiter, weil Media Foundation gleiche Zeitstempel abweist.
    /// </summary>
    [Fact]
    public void Zwei_Bilder_im_selben_Augenblick_bekommen_verschiedene_Zeitstempel()
    {
        RecordingClock clock = new(framesPerSecond: 4);
        clock.Start(Start);

        TimeSpan first = clock.NextFrameTimestamp(Start + TimeSpan.FromSeconds(3));
        TimeSpan second = clock.NextFrameTimestamp(Start + TimeSpan.FromSeconds(3));

        Assert.True(second > first,
            $"Der zweite Zeitstempel ({second}) ist nicht grösser als der erste ({first}).");
    }

    [Fact]
    public void Waehrend_der_Pause_wird_kein_Bild_geschrieben()
    {
        RecordingClock clock = new(framesPerSecond: 4);
        clock.Start(Start);
        _ = clock.NextFrameTimestamp(Start);
        clock.Pause(Start + TimeSpan.FromSeconds(1));

        // Kaeme hier ein Bild durch, entstuende in der Datei Zeit, die es nicht gab.
        _ = Assert.Throws<InvalidOperationException>(
            () => clock.NextFrameTimestamp(Start + TimeSpan.FromSeconds(2)));
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
