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

        Assert.Collection(layout.Placements,
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
        Assert.Equal(0, layout.Placements[0].X);
        Assert.Equal(1920, layout.Placements[1].X);
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
        Assert.Equal([1, 2, 3], layout.Placements.Select(p => (int)p.Handle));
    }
}
