using Xunit;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Die Leinwand: ein Video aus mehreren Fenstern, ohne zu skalieren.
/// </summary>
public sealed class CanvasLayoutTests
{
    /// <summary>
    /// Der Fall, den die Messung erzwungen hat: Ein Unterfenster liegt über dem Hauptfenster
    /// und wird von der Fensteraufnahme NICHT mitgeliefert. Beide müssen deshalb einzeln
    /// aufgenommen und in ein gemeinsames Bild kopiert werden.
    /// </summary>
    [Fact]
    public void Haupt_und_Unterfenster_landen_auf_einer_Leinwand()
    {
        WindowBox main = new(Handle: 1, Left: 100, Top: 100, Width: 800, Height: 600);
        WindowBox dialog = new(Handle: 2, Left: 250, Top: 250, Width: 300, Height: 200);

        CanvasLayout layout = CanvasLayout.For([main, dialog])!;

        Assert.Equal(800, layout.Width);
        Assert.Equal(600, layout.Height);
        Assert.Equal(100, layout.OriginLeft);
        Assert.Equal(100, layout.OriginTop);

        Assert.Collection(layout.PlaceAll([main, dialog]),
            first =>
            {
                Assert.Equal(1, first.Handle);
                Assert.Equal(0, first.X);
                Assert.Equal(0, first.Y);
            },
            second =>
            {
                Assert.Equal(2, second.Handle);
                Assert.Equal(150, second.X);
                Assert.Equal(150, second.Y);
            });
    }

    /// <summary>
    /// Ragt ein Unterfenster über das Hauptfenster hinaus, wächst die Leinwand — sonst wäre der
    /// überstehende Teil abgeschnitten, und genau dort steht gern die Schaltfläche, um die es
    /// hinterher geht.
    /// </summary>
    [Fact]
    public void Ein_ueberstehendes_Unterfenster_vergroessert_die_Leinwand()
    {
        WindowBox main = new(1, 100, 100, 800, 600);
        WindowBox dialog = new(2, 800, 650, 400, 300);

        CanvasLayout layout = CanvasLayout.For([main, dialog])!;

        Assert.Equal(1100, layout.Width);
        Assert.Equal(850, layout.Height);
    }

    /// <summary>
    /// H.264 mit 4:2:0 legt die Farbanteile auf halbe Auflösung; eine ungerade Kante hat dort
    /// keinen ganzen Bildpunkt mehr und wird vom Encoder abgelehnt.
    /// </summary>
    [Theory]
    [InlineData(801, 601, 802, 602)]
    [InlineData(800, 600, 800, 600)]
    [InlineData(1, 1, 2, 2)]
    public void Die_Leinwand_hat_immer_gerade_Kanten(int width, int height,
                                                     int expectedWidth, int expectedHeight)
    {
        CanvasLayout layout = CanvasLayout.For([new WindowBox(1, 0, 0, width, height)])!;

        Assert.Equal(expectedWidth, layout.Width);
        Assert.Equal(expectedHeight, layout.Height);
    }

    /// <summary>
    /// Bildschirme links vom Hauptbildschirm haben negative Koordinaten. Die Leinwand muss
    /// damit umgehen, sonst läge das Fenster ausserhalb des Bildes.
    /// </summary>
    [Fact]
    public void Ein_Fenster_auf_einem_Bildschirm_links_vom_Hauptbildschirm_liegt_richtig()
    {
        WindowBox left = new(1, -1920, 0, 1920, 1080);
        WindowBox right = new(2, 0, 0, 1920, 1080);

        CanvasLayout layout = CanvasLayout.For([left, right])!;

        Assert.Equal(3840, layout.Width);
        Assert.Equal(-1920, layout.OriginLeft);
        Assert.Equal(0, layout.PlaceAll([left, right])[0].X);
        Assert.Equal(1920, layout.PlaceAll([left, right])[1].X);
    }

    [Fact]
    public void Ohne_Fenster_mit_Flaeche_gibt_es_keine_Leinwand()
    {
        Assert.Null(CanvasLayout.For([]));
        Assert.Null(CanvasLayout.For([new WindowBox(1, 0, 0, 0, 0)]));
        Assert.Null(CanvasLayout.For([new WindowBox(1, 0, 0, 800, 0)]));
    }

    /// <summary>
    /// Verschiebt sich ein Fenster innerhalb der Leinwand, kostet das keine neue Datei —
    /// es ändert sich nur der Platz.
    /// </summary>
    [Fact]
    public void Verschieben_innerhalb_der_Leinwand_passt_weiterhin()
    {
        CanvasLayout layout = CanvasLayout.For(
            [new WindowBox(1, 0, 0, 1000, 800), new WindowBox(2, 900, 700, 100, 100)])!;

        Assert.True(layout.Fits([new WindowBox(1, 0, 0, 1000, 800),
                                 new WindowBox(2, 500, 400, 100, 100)]));
    }

    /// <summary>
    /// Wandert ein Fenster über die Leinwand hinaus, passt es nicht mehr. Die Grösse liegt in
    /// der Datei fest; es bleibt nur, eine neue zu beginnen — skaliert wird nicht.
    /// </summary>
    [Fact]
    public void Wandern_ueber_die_Leinwand_hinaus_passt_nicht_mehr()
    {
        CanvasLayout layout = CanvasLayout.For([new WindowBox(1, 0, 0, 800, 600)])!;

        Assert.False(layout.Fits([new WindowBox(1, 100, 0, 800, 600)]));
        Assert.False(layout.Fits([new WindowBox(1, -10, 0, 800, 600)]));
        Assert.False(layout.Fits([new WindowBox(1, 0, 0, 900, 600)]));
    }

    /// <summary>
    /// Ein minimiertes Fenster hat keine Fläche. Es hinterlässt seinen Platz schwarz, statt das
    /// letzte Bild stehenzulassen — ein stehengebliebenes Bild behauptete, dort sei noch etwas
    /// zu sehen.
    /// </summary>
    [Fact]
    public void Ein_Fenster_ohne_Flaeche_faellt_aus_der_Anordnung()
    {
        CanvasLayout layout = CanvasLayout.For(
            [new WindowBox(1, 0, 0, 800, 600), new WindowBox(2, 100, 100, 200, 200)])!;

        IReadOnlyList<WindowPlacement> placed = layout.PlaceAll(
            [new WindowBox(1, 0, 0, 800, 600), new WindowBox(2, 100, 100, 0, 0)]);

        Assert.Equal(1, Assert.Single(placed).Handle);
        Assert.True(layout.Fits([new WindowBox(2, 100, 100, 0, 0)]));
    }

    /// <summary>
    /// Die Leinwand ist der Bildschirm, nicht die Hüllfläche — der Kern der Änderung. Ein
    /// Fenster von 1400×900 auf einem Bildschirm von 2880×1800 ergibt eine Leinwand von
    /// 2880×1800, und das Fenster steht an seiner echten Stelle darin.
    /// </summary>
    [Fact]
    public void Die_Leinwand_ist_der_Bildschirm()
    {
        ScreenBox screen = new(0, 0, 2880, 1800);
        WindowBox window = new(1, 500, 300, 1400, 900);

        CanvasLayout layout = CanvasLayout.ForScreens([window], [screen])!;

        Assert.Equal(2880, layout.Width);
        Assert.Equal(1800, layout.Height);
        Assert.Equal(0, layout.OriginLeft);
        Assert.Equal(0, layout.OriginTop);

        WindowPlacement placed = Assert.Single(layout.PlaceAll([window]));

        Assert.Equal(500, placed.X);
        Assert.Equal(300, placed.Y);
    }

    /// <summary>
    /// Der Fall, der früher eine neue Datei kostete: Das Fenster wird verschoben und
    /// vergrössert. Die Leinwand bleibt dieselbe, nur der Platz ändert sich.
    /// </summary>
    [Fact]
    public void Verschieben_und_Vergroessern_kostet_keine_neue_Leinwand()
    {
        ScreenBox screen = new(0, 0, 2880, 1800);

        CanvasLayout layout = CanvasLayout.ForScreens(
            [new WindowBox(1, 500, 300, 1400, 900)], [screen])!;

        WindowPlacement placed = Assert.Single(
            layout.PlaceAll([new WindowBox(1, 40, 20, 2600, 1600)]));

        Assert.Equal(40, placed.X);
        Assert.Equal(20, placed.Y);
        Assert.Equal(2880, layout.Width);
    }

    /// <summary>
    /// Ein maximiertes Fenster ragt gemessen um dreizehn Bildpunkte über jeden Bildschirmrand
    /// hinaus — das ist sein unsichtbarer Anfassrahmen. Die Leinwand wächst deshalb NICHT:
    /// Was ausserhalb des Bildschirms liegt, ist auch auf dem Bildschirm nicht zu sehen.
    /// </summary>
    [Fact]
    public void Ein_maximiertes_Fenster_vergroessert_die_Leinwand_nicht()
    {
        ScreenBox screen = new(0, 0, 2880, 1800);
        WindowBox maximised = new(1, -13, -13, 2906, 1730);

        CanvasLayout layout = CanvasLayout.ForScreens([maximised], [screen])!;

        Assert.Equal(2880, layout.Width);
        Assert.Equal(1800, layout.Height);

        // Der Platz ist negativ, und das ist richtig so - beschnitten wird beim Zeichnen.
        WindowPlacement placed = Assert.Single(layout.PlaceAll([maximised]));

        Assert.Equal(-13, placed.X);
        Assert.Equal(-13, placed.Y);
        Assert.False(layout.Fits([maximised]));
    }

    /// <summary>
    /// Der Monitorwechsel: gleiche Bildgrösse, anderer Ursprung. Genau deshalb kostet er keine
    /// neue Datei.
    /// </summary>
    [Fact]
    public void Ein_Bildschirmwechsel_verschiebt_nur_den_Ursprung()
    {
        CanvasLayout first = CanvasLayout.ForScreens(
            [new WindowBox(1, 100, 100, 800, 600)], [new ScreenBox(0, 0, 1920, 1080)])!;

        CanvasLayout moved = first.MovedTo(1920, 0);

        Assert.Equal(first.Width, moved.Width);
        Assert.Equal(first.Height, moved.Height);
        Assert.Equal(1920, moved.OriginLeft);

        // Dasselbe Fenster, jetzt auf dem zweiten Bildschirm: derselbe Platz im Bild.
        Assert.Equal(100, Assert.Single(moved.PlaceAll(
            [new WindowBox(1, 2020, 100, 800, 600)])).X);
    }

    /// <summary>
    /// Zwei Bildschirme, und die Sitzung liegt auf beiden: Die Leinwand umfasst beide, damit
    /// kein Fenster verlorengeht.
    /// </summary>
    [Fact]
    public void Liegt_die_Sitzung_auf_zwei_Bildschirmen_umfasst_die_Leinwand_beide()
    {
        CanvasLayout layout = CanvasLayout.ForScreens(
            [new WindowBox(1, 100, 100, 800, 600), new WindowBox(2, 2000, 100, 400, 300)],
            [new ScreenBox(0, 0, 1920, 1080), new ScreenBox(1920, 0, 1920, 1080)])!;

        Assert.Equal(3840, layout.Width);
        Assert.Equal(1080, layout.Height);
    }

    /// <summary>
    /// Ein Bildschirm, den kein Fenster berührt, zählt nicht mit. Sonst wäre das Bild bei zwei
    /// Bildschirmen immer doppelt so breit wie nötig und zur Hälfte dauerhaft schwarz.
    /// </summary>
    [Fact]
    public void Ein_unbenutzter_Bildschirm_vergroessert_die_Leinwand_nicht()
    {
        CanvasLayout layout = CanvasLayout.ForScreens(
            [new WindowBox(1, 100, 100, 800, 600)],
            [new ScreenBox(0, 0, 1920, 1080), new ScreenBox(1920, 0, 3840, 2160)])!;

        Assert.Equal(1920, layout.Width);
        Assert.Equal(1080, layout.Height);
    }

    /// <summary>
    /// Ohne bekannte Bildschirme bleibt die Hüllfläche der Fenster — eine Aufzeichnung, die
    /// klein beginnt, ist besser als keine.
    /// </summary>
    [Fact]
    public void Ohne_Bildschirme_gilt_die_Huellflaeche()
    {
        CanvasLayout layout = CanvasLayout.ForScreens(
            [new WindowBox(1, 100, 100, 800, 600)], [])!;

        Assert.Equal(800, layout.Width);
        Assert.Equal(100, layout.OriginLeft);
    }

    /// <summary>
    /// Drei 4K-Bildschirme nebeneinander lehnt der Encoder gemessen ab (0xC00D36B4). Dann gilt
    /// der Bildschirm, auf dem am meisten von der Sitzung liegt.
    /// </summary>
    [Fact]
    public void Was_der_Encoder_nicht_annimmt_faellt_auf_einen_Bildschirm_zurueck()
    {
        CanvasLayout layout = CanvasLayout.ForScreens(
            [new WindowBox(1, 100, 100, 3000, 2000), new WindowBox(2, 8000, 100, 400, 300)],
            [new ScreenBox(0, 0, 3840, 2160),
             new ScreenBox(3840, 0, 3840, 2160),
             new ScreenBox(7680, 0, 3840, 2160)])!;

        Assert.Equal(3840, layout.Width);
        Assert.Equal(2160, layout.Height);
        Assert.Equal(0, layout.OriginLeft);
    }

    [Fact]
    public void Die_Anordnung_ist_stabil_sortiert()
    {
        CanvasLayout layout = CanvasLayout.For(
            [new WindowBox(3, 500, 500, 100, 100),
             new WindowBox(1, 0, 0, 100, 100),
             new WindowBox(2, 200, 0, 100, 100)])!;

        // Von oben nach unten, bei gleicher Hoehe von links nach rechts: Die Reihenfolge
        // bestimmt, was bei Ueberschneidung obenauf liegt, und sie darf nicht vom Zufall der
        // Fensteraufzaehlung abhaengen.
        Assert.Equal([1, 2, 3], layout.PlaceAll(
            [new WindowBox(3, 500, 500, 100, 100),
             new WindowBox(1, 0, 0, 100, 100),
             new WindowBox(2, 200, 0, 100, 100)]).Select(p => (int)p.Handle));
    }
}
