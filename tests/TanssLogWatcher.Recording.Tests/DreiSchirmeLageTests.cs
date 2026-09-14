using Xunit;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Die unbequeme Bildschirmlage, gegen erfundene Bildschirme durchgerechnet.
/// </summary>
/// <remarks>
/// <para><b>Genau die Lage eines echten Arbeitsplatzes</b>, an dem die Fehler dieser Runde
/// gemessen wurden:</para>
/// <list type="bullet">
///   <item><description>links ein Bildschirm bei <b>−1920</b> — negatives X,</description></item>
///   <item><description>er ist mit 1200 Bildpunkten <b>höher</b> als die anderen beiden mit
///   1080,</description></item>
///   <item><description>und der <b>Hauptbildschirm liegt in der Mitte</b>, nicht links.
///   </description></item>
/// </list>
/// <para><b>Warum das hier steht und nicht nur im Mehrschirmtest.</b> Der Test am echten Aufbau
/// misst die ganze Kette bis zum Bildpunkt, läuft aber nur auf einem Rechner mit mehreren
/// Bildschirmen. Die Rechnung dahinter braucht keinen einzigen: Wird sie hier mitgeführt, fällt
/// ein Rückfall auch auf einem Einschirmrechner auf — und dort wird entwickelt.</para>
/// </remarks>
public sealed class DreiSchirmeLageTests
{
    /// <summary>Links, bei negativem X, und 120 Bildpunkte höher als die anderen.</summary>
    private static readonly ScreenInfo Links =
        new(Handle: 3, new ScreenBox(-1920, 0, 1920, 1200), IsPrimary: false);

    /// <summary>Der Hauptbildschirm — in der Mitte, nicht links.</summary>
    private static readonly ScreenInfo Mitte =
        new(Handle: 2, new ScreenBox(0, 0, 1920, 1080), IsPrimary: true);

    /// <summary>Rechts aussen.</summary>
    private static readonly ScreenInfo Rechts =
        new(Handle: 1, new ScreenBox(1920, 0, 1920, 1080), IsPrimary: false);

    /// <summary>
    /// Die Bildschirme in der Reihenfolge, in der Windows sie gemessen aufzählt.
    /// </summary>
    /// <remarks>
    /// Ausdrücklich nicht von links nach rechts sortiert: Am echten Aufbau gemessen kam der
    /// rechte zuerst und der linke zuletzt. Wer sich auf die Reihenfolge verliesse, bekäme
    /// hier den falschen Bildschirm.
    /// </remarks>
    private static readonly ScreenInfo[] Alle = [Rechts, Mitte, Links];

    private static readonly ScreenBox[] Boxen = [.. Alle.Select(s => s.Box)];

    private static readonly DateTimeOffset Start =
        new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Ein Fenster auf dem linken Bildschirm: Die Leinwand liegt bei negativem X und ist so
    /// hoch wie DIESER Bildschirm — 1200, nicht die 1080 des Hauptbildschirms.
    /// </summary>
    [Fact]
    public void Auf_dem_linken_Bildschirm_gilt_dessen_Lage_und_dessen_Hoehe()
    {
        WindowBox window = new(1, -1500, 100, 1200, 900);

        CanvasLayout layout = CanvasLayout.ForScreens([window], Boxen)!;

        Assert.Equal(-1920, layout.OriginLeft);
        Assert.Equal(0, layout.OriginTop);
        Assert.Equal(1920, layout.Width);
        Assert.Equal(1200, layout.Height);

        // Das Fenster steht 420 Bildpunkte vom linken Rand der Leinwand entfernt.
        WindowPlacement placed = Assert.Single(layout.PlaceAll([window]));

        Assert.Equal(420, placed.X);
        Assert.Equal(100, placed.Y);
    }

    /// <summary>
    /// Der untere Streifen des höheren Bildschirms geht nicht verloren. Wer die Leinwand aus
    /// dem Hauptbildschirm ableitete, schnitte hier 120 Bildpunkte ab.
    /// </summary>
    [Fact]
    public void Der_untere_Streifen_des_hoeheren_Bildschirms_bleibt_erhalten()
    {
        WindowBox window = new(1, -1620, 500, 1000, 650);

        CanvasLayout layout = CanvasLayout.ForScreens([window], Boxen)!;

        Assert.Equal(1200, layout.Height);

        WindowPlacement placed = Assert.Single(layout.PlaceAll([window]));

        // Unterkante bei 1150 - das liegt unterhalb der 1080 des Hauptbildschirms.
        Assert.Equal(1150, placed.Y + placed.Height);
        Assert.True(placed.Y + placed.Height <= layout.Height);
    }

    /// <summary>Ein Fenster rechts aussen liegt ebenso richtig.</summary>
    [Fact]
    public void Auf_dem_rechten_Bildschirm_gilt_dessen_Lage()
    {
        WindowBox window = new(1, 2500, 100, 1200, 900);

        CanvasLayout layout = CanvasLayout.ForScreens([window], Boxen)!;

        Assert.Equal(1920, layout.OriginLeft);
        Assert.Equal(1920, layout.Width);
        Assert.Equal(1080, layout.Height);
        Assert.Equal(580, Assert.Single(layout.PlaceAll([window])).X);
    }

    /// <summary>
    /// Ein Fenster über der Grenze zwischen dem linken und dem mittleren Bildschirm: Die
    /// Leinwand umfasst beide und ist so hoch wie der höhere.
    /// </summary>
    [Fact]
    public void Ueber_der_Grenze_umfasst_die_Leinwand_beide_Bildschirme()
    {
        WindowBox window = new(1, -400, 100, 800, 600);

        CanvasLayout layout = CanvasLayout.ForScreens([window], Boxen)!;

        Assert.Equal(-1920, layout.OriginLeft);
        Assert.Equal(3840, layout.Width);
        Assert.Equal(1200, layout.Height);

        // Beide Hälften liegen im Bild: die linke ab 1520, die rechte bis 2320.
        WindowPlacement placed = Assert.Single(layout.PlaceAll([window]));

        Assert.Equal(1520, placed.X);
        Assert.Equal(2320, placed.X + placed.Width);
    }

    /// <summary>
    /// Der Fehler, der am echten Aufbau gemessen wurde: Das Bild rutschte mitten in der
    /// Aufzeichnung um 1920 Bildpunkte zur Seite, obwohl sich kein Fenster bewegt hatte.
    /// </summary>
    /// <remarks>
    /// <para>Die Ursache war die Frage, mit der über einen Bildschirmwechsel entschieden wurde:
    /// „Sitzt die Leinwand auf dem Ursprung dieses Bildschirms?“ Eine Leinwand über zwei
    /// Bildschirme sitzt auf dem Ursprung des <b>linkesten</b> — hier also bei −1920. Der
    /// Bildschirm mit der grössten Überdeckung war aber der Hauptbildschirm bei 0/0, und weil
    /// die Ursprünge nicht übereinstimmten, wanderte die Leinwand dorthin.</para>
    /// <para>Gemessen, was das anrichtete: Das Fenster auf dem linken Bildschirm fiel aus dem
    /// Bild, und ein Drittel der Leinwand wurde dauerhaft schwarz. Zu sehen war das erst im
    /// Video, nie an einem Rückgabewert.</para>
    /// </remarks>
    [Fact]
    public void Die_Leinwand_rutscht_nicht_wenn_sie_den_Bildschirm_schon_zeigt()
    {
        RecordingDirector director = new(new RecordingOptions
        {
            MinimumFreeMegabytes = 0,
            PauseGrace = TimeSpan.FromSeconds(2),
        });

        // Das grosse Fenster auf dem Hauptbildschirm, ein kleines auf dem linken.
        WindowBox gross = new(1, 400, 200, 1000, 700);
        WindowBox klein = new(2, -1620, 300, 200, 200);

        RecordingDecision start = director.Decide(Takt(Start, gross, klein));

        Assert.Equal(RecordingAction.Start, start.Action);
        Assert.Equal(-1920, director.Canvas!.OriginLeft);
        Assert.Equal(3840, director.Canvas.Width);

        // Zwoelf Sekunden lang aendert sich nichts an der Fensterlage. Dann darf sich auch an
        // der Leinwand nichts aendern.
        for (int second = 2; second <= 12; second += 2)
        {
            Assert.Equal(RecordingAction.None,
                director.Decide(Takt(Start + TimeSpan.FromSeconds(second), gross, klein))
                    .Action);
        }

        Assert.Equal(0, director.CanvasMoves);
        Assert.Equal(-1920, director.Canvas!.OriginLeft);

        // Und beide Fenster stehen weiterhin im Bild.
        Assert.All(director.Canvas.PlaceAll([gross, klein]),
            p => Assert.True(p.X >= 0 && p.X + p.Width <= director.Canvas!.Width,
                $"Fenster {p.Handle} liegt bei {p.X}..{p.X + p.Width} und damit ausserhalb "
                + $"der Leinwand von {director.Canvas!.Width} Bildpunkten."));
    }

    /// <summary>
    /// Wandert die Sitzung auf einen Bildschirm, den die Leinwand NICHT zeigt, folgt sie ihm
    /// weiterhin.
    /// </summary>
    /// <remarks>
    /// Die Gegenprobe zum Test darüber: Die neue Frage darf den Wechsel nicht überhaupt
    /// abschaffen. Ein Test, der nur noch „es bewegt sich nichts“ zusichert, wäre schlimmer als
    /// keiner.
    /// </remarks>
    [Fact]
    public void Auf_einen_nicht_gezeigten_Bildschirm_wandert_die_Leinwand_weiterhin()
    {
        RecordingDirector director = new(new RecordingOptions { MinimumFreeMegabytes = 0 });

        WindowBox hier = new(1, 400, 200, 1000, 700);

        _ = director.Decide(Takt(Start, hier));

        Assert.Equal(0, director.Canvas!.OriginLeft);
        Assert.Equal(1920, director.Canvas.Width);

        // Dasselbe Fenster, jetzt ganz auf dem linken Bildschirm.
        WindowBox drueben = new(1, -1520, 200, 1000, 700);

        Assert.Equal(RecordingAction.None,
            director.Decide(Takt(Start + TimeSpan.FromSeconds(4), drueben)).Action);

        RecordingDecision moved =
            director.Decide(Takt(Start + TimeSpan.FromSeconds(6), drueben));

        Assert.Equal(RecordingAction.MoveCanvas, moved.Action);
        Assert.Equal(-1920, director.Canvas!.OriginLeft);
        Assert.Equal(3, director.Screen!.Value.Handle);
    }

    /// <summary>
    /// Der Bildschirmbetrieb bekommt GENAU EINEN Bildschirm — den, auf dem die Sitzung liegt.
    /// </summary>
    /// <remarks>
    /// <para>Der Rekorder nimmt im Bildschirmbetrieb einen einzigen Bildschirm auf und setzt
    /// dessen Bild bündig bei 0/0 auf die Leinwand. Eine Leinwand über zwei Bildschirme hätte
    /// für die zweite Hälfte keine Quelle.</para>
    /// <para>Am echten Aufbau gemessen, bevor das hier zugesichert war: eine Datei von
    /// 3840×1200, deren Mitte des grossen Fensters sich als 0/0/0 las — zwei Drittel des Bildes
    /// waren dauerhaft schwarz.</para>
    /// </remarks>
    [Fact]
    public void Im_Bildschirmbetrieb_ist_die_Leinwand_genau_ein_Bildschirm()
    {
        RecordingDirector director = new(new RecordingOptions
        {
            Scope = CaptureScope.Screen,
            MinimumFreeMegabytes = 0,
        });

        WindowBox gross = new(1, 400, 200, 1000, 700);
        WindowBox klein = new(2, -1620, 300, 200, 200);

        Assert.Equal(RecordingAction.Start, director.Decide(Takt(Start, gross, klein)).Action);

        Assert.Equal(1920, director.Canvas!.Width);
        Assert.Equal(1080, director.Canvas.Height);
        Assert.Equal(0, director.Canvas.OriginLeft);

        // Und aufgenommen wird der Bildschirm, auf dem die Sitzung liegt - der mittlere.
        Assert.Equal(2, director.Screen!.Value.Handle);
    }

    /// <summary>
    /// Der Bildschirm, auf dem die Sitzung liegt, wird über die FLÄCHE bestimmt — nicht über
    /// den Ursprung der Leinwand und nicht über die Reihenfolge der Aufzählung.
    /// </summary>
    /// <remarks>
    /// Hier fallen beide falschen Antworten auseinander: Der Ursprung der Leinwand gehört dem
    /// linken Bildschirm, der erste der Aufzählung ist der rechte — und die Sitzung liegt auf
    /// dem mittleren. Nur die Fläche gibt die richtige Antwort.
    /// </remarks>
    [Fact]
    public void Der_Bildschirm_der_Sitzung_wird_ueber_die_Flaeche_bestimmt()
    {
        RecordingDirector director = new(new RecordingOptions { MinimumFreeMegabytes = 0 });

        WindowBox gross = new(1, 400, 200, 1000, 700);
        WindowBox klein = new(2, -1620, 300, 200, 200);

        _ = director.Decide(Takt(Start, gross, klein));

        Assert.Equal(2, director.Screen!.Value.Handle);
        Assert.True(director.Screen!.Value.IsPrimary);
    }

    /// <summary>
    /// Die Leinwand über beide linken Bildschirme deckt den mittleren vollständig ab — die
    /// Frage, an der der Wechsel hängt.
    /// </summary>
    [Fact]
    public void Eine_Leinwand_ueber_zwei_Bildschirme_deckt_beide_ab()
    {
        CanvasLayout layout = CanvasLayout.ForScreens(
            [new WindowBox(1, 400, 200, 1000, 700), new WindowBox(2, -1620, 300, 200, 200)],
            Boxen)!;

        Assert.True(layout.Covers(Mitte.Box));
        Assert.True(layout.Covers(Links.Box));

        // Der rechte gehoert nicht dazu: Ihn beruehrt kein Fenster.
        Assert.False(layout.Covers(Rechts.Box));
    }

    /// <summary>
    /// Auch der Einzelbildschirm-Fall bleibt, wie er war: Die Leinwand deckt ihren eigenen
    /// Bildschirm ab, einen anderen nicht.
    /// </summary>
    [Fact]
    public void Eine_Leinwand_auf_einem_Bildschirm_deckt_nur_diesen_ab()
    {
        CanvasLayout layout = CanvasLayout.ForScreens(
            [new WindowBox(1, 400, 200, 1000, 700)], Boxen)!;

        Assert.True(layout.Covers(Mitte.Box));
        Assert.False(layout.Covers(Links.Box));
        Assert.False(layout.Covers(Rechts.Box));
    }

    private static RecordingInput Takt(DateTimeOffset now, params WindowBox[] windows) =>
        new(now, windows, SessionEnded: false, FreeMegabytes: 100_000, Screens: Alle);
}
