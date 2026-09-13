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
            PauseGrace = TimeSpan.FromSeconds(2),
            MinimumFreeMegabytes = 2048,
        });

    /// <summary>Der Bildschirm, auf dem in diesen Fällen alles liegt.</summary>
    private static readonly ScreenInfo Schirm =
        new(Handle: 100, new ScreenBox(0, 0, 1920, 1080), IsPrimary: true);

    /// <summary>Ein zweiter Bildschirm rechts daneben.</summary>
    private static readonly ScreenInfo Rechts =
        new(Handle: 200, new ScreenBox(1920, 0, 1920, 1080), IsPrimary: false);

    private static RecordingInput Takt(DateTimeOffset now, params WindowBox[] windows) =>
        new(now, windows, SessionEnded: false, FreeMegabytes: 100_000, Screens: [Schirm]);

    /// <summary>Ein Takt mit beiden Bildschirmen.</summary>
    private static RecordingInput Takt2(DateTimeOffset now, params WindowBox[] windows) =>
        new(now, windows, SessionEnded: false, FreeMegabytes: 100_000,
            Screens: [Schirm, Rechts]);

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

        // Die Leinwand ist der Bildschirm und nicht die Huellflaeche der Fenster - genau das
        // ist der Grund, warum spaeteres Verschieben keine neue Datei mehr kostet.
        Assert.Contains("1920×1080", decision.Reason, StringComparison.Ordinal);
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
    /// Liegt das Fenster nach der Pause anders, wird trotzdem fortgesetzt — die Leinwand ist
    /// der Bildschirm, und der ist derselbe geblieben. Früher begann hier eine neue Datei.
    /// </summary>
    [Fact]
    public void Liegt_das_Fenster_nach_der_Pause_anders_wird_trotzdem_fortgesetzt()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));
        _ = Pausieren(director, Start + TimeSpan.FromSeconds(5));

        RecordingDecision decision = director.Decide(
            Takt(Start + TimeSpan.FromMinutes(5), new WindowBox(1, 900, 400, 800, 600)));

        Assert.Equal(RecordingAction.Resume, decision.Action);
        Assert.True(director.IsRecording);
    }

    /// <summary>
    /// Verschieben kostet keine neue Datei — es ändert sich nur der Platz im Bild. Das gilt
    /// jetzt auch für eine Lage, die früher über die Hüllfläche hinausragte.
    /// </summary>
    [Fact]
    public void Verschieben_laesst_die_Datei_stehen()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt, new WindowBox(2, 700, 600, 200, 100)));

        Assert.Equal(RecordingAction.None, director.Decide(
            Takt(Start + TimeSpan.FromSeconds(10), Haupt,
                 new WindowBox(2, 200, 200, 200, 100))).Action);

        Assert.Equal(RecordingAction.None, director.Decide(
            Takt(Start + TimeSpan.FromSeconds(20),
                 new WindowBox(1, 1000, 400, 800, 600))).Action);
    }

    /// <summary>
    /// Es gibt keine Abschnittslänge mehr. Eine Sitzung, die eine Stunde läuft, ergibt eine
    /// Datei — früher waren es sechs.
    /// </summary>
    [Fact]
    public void Auch_nach_einer_Stunde_beginnt_keine_neue_Datei()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));

        for (int minute = 5; minute <= 60; minute += 5)
        {
            Assert.Equal(RecordingAction.None,
                director.Decide(Takt(Start + TimeSpan.FromMinutes(minute), Haupt)).Action);
        }

        Assert.True(director.IsRecording);
    }

    [Fact]
    public void Das_Ende_der_Sitzung_beendet_die_Aufzeichnung()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));

        RecordingDecision decision = director.Decide(
            new RecordingInput(Start + TimeSpan.FromMinutes(1), [Haupt],
                               SessionEnded: true, FreeMegabytes: 100_000, Screens: [Schirm]));

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
                               SessionEnded: false, FreeMegabytes: 500, Screens: [Schirm]));

        Assert.Equal(RecordingAction.Stop, decision.Action);
        Assert.Contains("500 MB frei", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("Warteschlange", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Bei_Platzmangel_beginnt_gar_nicht_erst_etwas()
    {
        RecordingDirector director = Neu();

        RecordingDecision decision = director.Decide(
            new RecordingInput(Start, [Haupt], SessionEnded: false, FreeMegabytes: 100,
                               Screens: [Schirm]));

        Assert.Equal(RecordingAction.Stop, decision.Action);
        Assert.False(director.IsRecording);
    }

    [Fact]
    public void Nach_dem_Ende_geschieht_nichts_mehr()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));
        _ = director.Decide(new RecordingInput(Start + TimeSpan.FromMinutes(1), [],
                                               SessionEnded: true, FreeMegabytes: 100_000,
                                               Screens: [Schirm]));

        Assert.Equal(RecordingAction.None,
            director.Decide(Takt(Start + TimeSpan.FromMinutes(2), Haupt)).Action);
        Assert.True(director.IsFinished);
    }

    /// <summary>
    /// Ein Unterfenster, das später aufgeht, gehört dazu — auch eines, das über das
    /// Hauptfenster hinausragt. Früher begann dafür eine neue Datei; die Leinwand ist jetzt der
    /// Bildschirm, und der ändert sich nicht.
    /// </summary>
    [Fact]
    public void Ein_spaeter_geoeffnetes_Unterfenster_kostet_keine_neue_Datei()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt(Start, Haupt));

        Assert.Equal(RecordingAction.None,
            director.Decide(Takt(Start + TimeSpan.FromSeconds(5), Haupt, Dialog)).Action);

        Assert.Equal(RecordingAction.None, director.Decide(
            Takt(Start + TimeSpan.FromSeconds(10), Haupt,
                 new WindowBox(3, 850, 650, 400, 300))).Action);
    }

    /// <summary>
    /// Der gemessene Anlass für die ganze Änderung: Ein Remotedesktop-Fenster ändert beim
    /// Verbindungsaufbau zweimal seine Grösse. Das ergab drei Dateien in neunzehn Sekunden.
    /// Jetzt ergibt es eine.
    /// </summary>
    [Fact]
    public void Ein_Fenster_das_seine_Groesse_aendert_kostet_keine_neue_Datei()
    {
        RecordingDirector director = Neu();

        Assert.Equal(RecordingAction.Start,
            director.Decide(Takt(Start, new WindowBox(1, 500, 300, 836, 496))).Action);

        foreach ((int at, WindowBox box) in new[]
        {
            (4, new WindowBox(1, 500, 300, 860, 496)),
            (5, new WindowBox(1, 0, 0, 1920, 1080)),
            (9, new WindowBox(1, -13, -13, 1946, 1010)),
        })
        {
            Assert.Equal(RecordingAction.None,
                director.Decide(Takt(Start + TimeSpan.FromSeconds(at), box)).Action);
        }

        Assert.True(director.IsRecording);
    }

    /// <summary>
    /// Wandert die Sitzung auf den zweiten Bildschirm, folgt ihr das Bild — mit Beharrungszeit,
    /// damit ein Fenster im Umzug das Bild nicht springen lässt.
    /// </summary>
    [Fact]
    public void Die_Leinwand_folgt_der_Sitzung_auf_den_anderen_Bildschirm()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt2(Start, new WindowBox(1, 100, 100, 800, 600)));

        Assert.Equal(0, director.Canvas!.OriginLeft);

        WindowBox drueben = new(1, 2100, 100, 800, 600);

        // Der erste Takt drueben merkt es nur vor; erst nach der Beharrungszeit wandert es.
        Assert.Equal(RecordingAction.None,
            director.Decide(Takt2(Start + TimeSpan.FromSeconds(4), drueben)).Action);

        RecordingDecision moved =
            director.Decide(Takt2(Start + TimeSpan.FromSeconds(6), drueben));

        Assert.Equal(RecordingAction.MoveCanvas, moved.Action);
        Assert.Equal(1920, director.Canvas!.OriginLeft);
        Assert.Equal(1, director.CanvasMoves);

        // Dasselbe Fenster steht im Bild jetzt wieder an seinem Platz auf dem Bildschirm.
        Assert.Equal(180, director.Canvas!.PlaceAll([drueben])[0].X);
    }

    /// <summary>
    /// Ein Fenster genau auf der Grenze darf das Bild nicht hin- und herspringen lassen. Erst
    /// wenn der deutlich grössere Teil drüben liegt, wandert es mit.
    /// </summary>
    [Fact]
    public void Ein_Fenster_auf_der_Grenze_laesst_das_Bild_stehen()
    {
        RecordingDirector director = Neu();
        _ = director.Decide(Takt2(Start, new WindowBox(1, 100, 100, 800, 600)));

        // Haelftig auf beiden Bildschirmen: 400 von 800 Bildpunkten Breite drueben.
        WindowBox haelftig = new(1, 1520, 100, 800, 600);

        for (int at = 4; at <= 12; at += 2)
        {
            Assert.Equal(RecordingAction.None,
                director.Decide(Takt2(Start + TimeSpan.FromSeconds(at), haelftig)).Action);
        }

        Assert.Equal(0, director.Canvas!.OriginLeft);
        Assert.Equal(0, director.CanvasMoves);
    }

    /// <summary>
    /// Ohne bekannte Bildschirme wandert nichts — und es bricht auch nichts. Das ist der
    /// Zustand auf einer Dienstsitzung ohne Bildschirm.
    /// </summary>
    [Fact]
    public void Ohne_Bildschirme_wandert_die_Leinwand_nicht()
    {
        RecordingDirector director = Neu();

        RecordingDecision start = director.Decide(new RecordingInput(
            Start, [Haupt], SessionEnded: false, FreeMegabytes: 100_000, Screens: []));

        Assert.Equal(RecordingAction.Start, start.Action);
        Assert.Equal(800, director.Canvas!.Width);

        Assert.Equal(RecordingAction.None, director.Decide(new RecordingInput(
            Start + TimeSpan.FromSeconds(10), [Haupt], false, 100_000, [])).Action);
    }

    [Fact]
    public void Unbrauchbare_Stellschrauben_werden_beim_Bauen_abgelehnt()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new RecordingDirector(new RecordingOptions { FramesPerSecond = 0 }));

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new RecordingDirector(new RecordingOptions { ScreenSwitchShare = 0 }));
    }
}
