using Xunit;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Der Direktor: jede Entscheidung der Aufzeichnung, ohne einen einzigen Windows-Aufruf.
/// </summary>
public sealed class RecordingDirectorTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly WindowBox Haupt = new(1, 100, 100, 800, 600);
    private static readonly WindowBox Dialog = new(2, 250, 250, 300, 200);

    private static RecordingDirector Neu(RecordingOptions? options = null) =>
        new(options ?? new RecordingOptions
        {
            SegmentLength = TimeSpan.FromMinutes(10),
            PauseGrace = TimeSpan.FromSeconds(2),
            MinimumFreeMegabytes = 2048,
        });

    private static RecordingInput Takt(DateTimeOffset now, params WindowBox[] windows) =>
        new(now, windows, SessionEnded: false, FreeMegabytes: 100_000);

    /// <summary>
    /// Führt eine echte Pause herbei und gibt zurück, wann sie begann.
    /// </summary>
    /// <remarks>
    /// Zwei Takte, und das ist keine Umständlichkeit: Der erste stellt fest, dass die Fenster
    /// fort sind, und startet die Schonfrist; erst der zweite, nach deren Ablauf, hält an.
    /// Genau daran unterscheidet sich eine Pause von einem Fenster im Umzug.
    /// </remarks>
    private static DateTimeOffset Pausieren(RecordingDirector director, DateTimeOffset from)
    {
        Assert.Equal(RecordingAction.None, director.Decide(Takt(from)).Action);

        RecordingDecision paused = director.Decide(Takt(from + TimeSpan.FromSeconds(3)));

        Assert.Equal(RecordingAction.Pause, paused.Action);
        return from + TimeSpan.FromSeconds(3);
    }

    [Fact]
    public void Mit_dem_ersten_sichtbaren_Fenster_beginnt_die_Aufzeichnung()
    {
        RecordingDirector director = Neu();

        RecordingDecision decision = director.Decide(Takt(Start, Haupt, Dialog));

        Assert.Equal(RecordingAction.Start, decision.Action);
        Assert.Equal(2, decision.Windows.Count);
        Assert.True(director.IsRecording);
        Assert.Contains("800×600", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ohne Fenster wird nichts angelegt. Eine leere Datei wäre eine Aufzeichnung, die es nicht
    /// gibt — und die Begleitdatei behauptete, es sei etwas aufgezeichnet worden.
    /// </summary>
    [Fact]
    public void Ohne_sichtbares_Fenster_beginnt_nichts()
    {
        RecordingDirector director = Neu();

        Assert.Equal(RecordingAction.None, director.Decide(Takt(Start)).Action);
        Assert.False(director.IsRecording);
    }

    /// <summary>
    /// Ein Fenster ist beim Verschieben zwischen zwei Bildschirmen kurz ohne Geometrie. Daraus
    /// darf keine Pause werden, sonst stünde im Bericht eine Unterbrechung, die es nie gab.
    /// </summary>
    [Fact]
    public void Ein_kurzes_Verschwinden_erzeugt_noch_keine_Pause()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));

        Assert.Equal(RecordingAction.None,
            director.Decide(Takt(Start + TimeSpan.FromSeconds(1))).Action);
        Assert.True(director.IsRecording);
    }

    /// <summary>
    /// Der erste Takt ohne Fenster startet nur die Schonfrist — angehalten wird frühestens im
    /// nächsten. Sonst würde jeder Fensterumzug zu einer Pause im Bericht.
    /// </summary>
    [Fact]
    public void Der_erste_Takt_ohne_Fenster_haelt_noch_nicht_an()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));

        Assert.Equal(RecordingAction.None,
            director.Decide(Takt(Start + TimeSpan.FromMinutes(1))).Action);
        Assert.True(director.IsRecording);
    }

    [Fact]
    public void Nach_der_Schonfrist_wird_pausiert()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));
        _ = director.Decide(Takt(Start + TimeSpan.FromSeconds(1)));

        RecordingDecision decision = director.Decide(Takt(Start + TimeSpan.FromSeconds(3)));

        Assert.Equal(RecordingAction.Pause, decision.Action);
        Assert.True(director.IsPaused);
        Assert.Contains("minimiert", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Kommt_das_Fenster_unveraendert_zurueck_wird_fortgesetzt()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));
        _ = Pausieren(director, Start + TimeSpan.FromSeconds(5));

        RecordingDecision decision =
            director.Decide(Takt(Start + TimeSpan.FromMinutes(5), Haupt));

        Assert.Equal(RecordingAction.Resume, decision.Action);
        Assert.True(director.IsRecording);
    }

    /// <summary>
    /// Liegt das Fenster nach der Pause anders, passt die alte Leinwand nicht mehr. Die Grösse
    /// steht in der Datei fest; es bleibt nur eine neue.
    /// </summary>
    [Fact]
    public void Liegt_das_Fenster_nach_der_Pause_anders_beginnt_eine_neue_Datei()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));
        _ = Pausieren(director, Start + TimeSpan.FromSeconds(5));

        RecordingDecision decision = director.Decide(
            Takt(Start + TimeSpan.FromMinutes(5), new WindowBox(1, 900, 900, 800, 600)));

        Assert.Equal(RecordingAction.RollSegment, decision.Action);
        Assert.True(director.IsRecording);
        Assert.Contains("neue Datei", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Wandert_das_Fenster_ueber_die_Leinwand_hinaus_beginnt_eine_neue_Datei()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));

        RecordingDecision decision = director.Decide(
            Takt(Start + TimeSpan.FromSeconds(10), new WindowBox(1, 2000, 100, 800, 600)));

        Assert.Equal(RecordingAction.RollSegment, decision.Action);
        Assert.Contains("skaliert wird nicht", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verschieben innerhalb der Leinwand kostet keine neue Datei — es ändert sich nur der Platz.
    /// </summary>
    [Fact]
    public void Verschieben_innerhalb_der_Leinwand_laesst_die_Datei_stehen()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt, new WindowBox(2, 700, 600, 200, 100)));

        RecordingDecision decision = director.Decide(
            Takt(Start + TimeSpan.FromSeconds(10), Haupt, new WindowBox(2, 200, 200, 200, 100)));

        Assert.Equal(RecordingAction.None, decision.Action);
    }

    [Fact]
    public void Nach_der_Abschnittslaenge_beginnt_eine_neue_Datei()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));

        Assert.Equal(RecordingAction.None,
            director.Decide(Takt(Start + TimeSpan.FromMinutes(9), Haupt)).Action);

        RecordingDecision decision =
            director.Decide(Takt(Start + TimeSpan.FromMinutes(10), Haupt));

        Assert.Equal(RecordingAction.RollSegment, decision.Action);
        Assert.Contains("Abschnitt", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Das_Ende_der_Sitzung_beendet_die_Aufzeichnung()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));

        RecordingDecision decision = director.Decide(
            new RecordingInput(Start + TimeSpan.FromMinutes(1), [Haupt],
                               SessionEnded: true, FreeMegabytes: 100_000));

        Assert.Equal(RecordingAction.Stop, decision.Action);
        Assert.True(director.IsFinished);
    }

    /// <summary>
    /// Eine volle Platte nimmt auch der Warteschlange den Platz, und die trägt die Arbeitszeit.
    /// Die Aufzeichnung ist das, was nachgeben muss — und sie sagt es.
    /// </summary>
    [Fact]
    public void Bei_Platzmangel_tritt_die_Aufzeichnung_zurueck()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));

        RecordingDecision decision = director.Decide(
            new RecordingInput(Start + TimeSpan.FromMinutes(1), [Haupt],
                               SessionEnded: false, FreeMegabytes: 500));

        Assert.Equal(RecordingAction.Stop, decision.Action);
        Assert.Contains("500 MB frei", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("Warteschlange", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Bei_Platzmangel_beginnt_gar_nicht_erst_etwas()
    {
        RecordingDirector director = Neu();

        RecordingDecision decision = director.Decide(
            new RecordingInput(Start, [Haupt], SessionEnded: false, FreeMegabytes: 100));

        Assert.Equal(RecordingAction.Stop, decision.Action);
        Assert.False(director.IsRecording);
    }

    [Fact]
    public void Nach_dem_Ende_geschieht_nichts_mehr()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));
        _ = director.Decide(new RecordingInput(Start + TimeSpan.FromMinutes(1), [],
                                               SessionEnded: true, FreeMegabytes: 100_000));

        Assert.Equal(RecordingAction.None,
            director.Decide(Takt(Start + TimeSpan.FromMinutes(2), Haupt)).Action);
        Assert.True(director.IsFinished);
    }

    /// <summary>
    /// Ein Unterfenster, das später aufgeht, gehört dazu — es ist der Fall, wegen dem überhaupt
    /// mehrere Fenster aufgenommen werden.
    /// </summary>
    [Fact]
    public void Ein_spaeter_geoeffnetes_Unterfenster_kommt_hinzu()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));

        // Der Dialog liegt innerhalb des Hauptfensters: dieselbe Leinwand genuegt.
        RecordingDecision inside =
            director.Decide(Takt(Start + TimeSpan.FromSeconds(5), Haupt, Dialog));
        Assert.Equal(RecordingAction.None, inside.Action);

        // Einer, der darueber hinausragt, verlangt eine groessere Leinwand.
        RecordingDecision outside = director.Decide(
            Takt(Start + TimeSpan.FromSeconds(10), Haupt, new WindowBox(3, 850, 650, 400, 300)));
        Assert.Equal(RecordingAction.RollSegment, outside.Action);
    }

    [Fact]
    public void Unbrauchbare_Stellschrauben_werden_beim_Bauen_abgelehnt()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new RecordingDirector(new RecordingOptions { FramesPerSecond = 0 }));

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new RecordingDirector(new RecordingOptions { SegmentLength = TimeSpan.Zero }));
    }
}
