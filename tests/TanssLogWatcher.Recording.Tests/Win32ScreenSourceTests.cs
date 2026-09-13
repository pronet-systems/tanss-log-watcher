using Xunit;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Die echte Bildschirmaufzählung — gegen die echten Bildschirme dieses Rechners.
/// </summary>
/// <remarks>
/// Eine Attrappe könnte hier nichts prüfen: Die Frage ist gerade, ob Windows die Lage so
/// meldet, wie die Leinwand sie braucht. Geprüft werden deshalb Eigenschaften, die auf jedem
/// Rechner gelten müssen, und nicht die Zahlen dieses einen.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows5.0")]
public sealed class Win32ScreenSourceTests
{
    [Fact]
    public void Es_gibt_mindestens_einen_Bildschirm_mit_Flaeche()
    {
        IReadOnlyList<ScreenInfo> screens = new Win32ScreenSource().Screens();

        Assert.NotEmpty(screens);
        Assert.All(screens, screen => Assert.True(screen.Box.HasArea));
        Assert.All(screens, screen => Assert.NotEqual(0, screen.Handle));
    }

    /// <summary>
    /// Genau ein Hauptbildschirm. Ohne ihn gäbe es keinen Ursprung des Koordinatensystems, und
    /// die Lage aller anderen wäre nicht zu deuten.
    /// </summary>
    [Fact]
    public void Genau_ein_Bildschirm_ist_der_Hauptbildschirm()
    {
        _ = Assert.Single(new Win32ScreenSource().Screens(), s => s.IsPrimary);
    }

    /// <summary>
    /// Der Hauptbildschirm liegt bei 0/0 — darauf beruht, dass ein Fenster links davon
    /// negative Werte meldet.
    /// </summary>
    [Fact]
    public void Der_Hauptbildschirm_liegt_im_Ursprung()
    {
        ScreenInfo primary = new Win32ScreenSource().Screens().Single(s => s.IsPrimary);

        Assert.Equal(0, primary.Box.Left);
        Assert.Equal(0, primary.Box.Top);
    }

    /// <summary>
    /// Zwei Bildschirme dürfen sich nicht überlappen — sonst wäre die Frage, auf welchem eine
    /// Sitzung liegt, nicht zu beantworten.
    /// </summary>
    [Fact]
    public void Die_Bildschirme_ueberlappen_sich_nicht()
    {
        IReadOnlyList<ScreenInfo> screens = new Win32ScreenSource().Screens();

        for (int i = 0; i < screens.Count; i++)
        {
            for (int j = i + 1; j < screens.Count; j++)
            {
                ScreenBox a = screens[i].Box;
                ScreenBox b = screens[j].Box;

                Assert.Equal(0, a.Overlap(new WindowBox(0, b.Left, b.Top, b.Width, b.Height)));
            }
        }
    }

    /// <summary>
    /// Die Leinwand, die daraus entsteht, ist so gross wie der Bildschirm — und der Encoder
    /// nimmt sie an. Das ist die Kette, an der die ganze Änderung hängt.
    /// </summary>
    [Fact]
    public void Aus_dem_echten_Bildschirm_wird_eine_brauchbare_Leinwand()
    {
        ScreenInfo primary = new Win32ScreenSource().Screens().Single(s => s.IsPrimary);

        // Ein Fenster mittendrin, wie eine Fernwartung es oeffnet.
        WindowBox window = new(1, 100, 100,
            Math.Min(1400, primary.Box.Width - 200),
            Math.Min(900, primary.Box.Height - 200));

        CanvasLayout layout = CanvasLayout.ForScreens([window], [primary.Box])!;

        Assert.Equal(primary.Box.Width, layout.Width);
        Assert.Equal(primary.Box.Height, layout.Height);
        Assert.True((long)layout.Width * layout.Height <= CanvasLayout.MaximumArea,
            $"Die Leinwand {layout.Width}×{layout.Height} überschreitet, was der Encoder "
            + "gemessen annimmt.");
    }
}
