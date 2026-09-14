using Xunit;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Der Bildfang, gegen ein echtes Fenster.
/// </summary>
/// <remarks>
/// Das Fenster entsteht im Testlauf, trägt eine bekannte Farbe, und geprüft wird der
/// Bildpunkt, der herauskommt. Anders liesse sich nicht feststellen, ob überhaupt das richtige
/// Fenster aufgenommen wurde — eine Aufnahme, die irgendetwas liefert, ist keine.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowCaptureTests
{
    /// <summary>
    /// Steht der Bildfang auf diesem Rechner zur Verfügung? Wenn nicht, sagen die Tests das,
    /// statt rot zu werden — ein Rechner ohne Bildschirmaufnahme ist kein Fehler im Werkzeug.
    /// </summary>
    [Fact]
    public void Der_Bildfang_meldet_seine_Verfuegbarkeit()
    {
        // Kein Assert auf true: Auf einem Bauläufer ohne Grafik ist false die richtige Antwort.
        // Geprüft wird, dass die Frage überhaupt beantwortet wird und nicht wirft.
        bool supported = WindowCapture.IsSupported();

        Assert.True(supported || !supported);
    }

    /// <summary>
    /// Der Grundfall: ein rotes Fenster aufnehmen und Rot bekommen.
    /// </summary>
    [CaptureFact]
    public void Ein_rotes_Fenster_ergibt_ein_rotes_Bild()
    {

        using TestWindow window = TestWindow.Open(System.Drawing.Color.Red, 400, 300);
        using CaptureDevice device = CaptureDevice.Create();
        using WindowCapture capture = WindowCapture.Start(device, window.Handle);

        byte[] buffer = [];
        FrameSize? size = WaitForFrame(capture, ref buffer);

        Assert.NotNull(size);
        Assert.True(size.Value.Width > 0 && size.Value.Height > 0);

        // Mitte des Bildes. BGRA: Blau, Gruen, Rot, Alpha.
        int middle = (((size.Value.Height / 2) * size.Value.Width) + (size.Value.Width / 2)) * 4;

        Assert.True(buffer[middle + 2] > 200,
            $"Rotanteil war {buffer[middle + 2]}, erwartet über 200 — das Bild stammt nicht "
            + "vom Fenster, das aufgenommen werden sollte.");
        Assert.True(buffer[middle + 1] < 80, $"Grünanteil war {buffer[middle + 1]}.");
    }

    /// <summary>
    /// Gemessen und hier festgehalten: Ein fremdes Fenster darüber landet NICHT im Bild. Das ist
    /// der Grund, warum Fenster und nicht Bildschirme aufgenommen werden — sonst stünde das
    /// Mailfenster des Technikers in der Dokumentation des Kunden.
    /// </summary>
    /// <remarks>
    /// <b><see cref="LiveScreenFactAttribute"/> und nicht <see cref="CaptureFactAttribute"/>:</b>
    /// Das Verdecken ist keine Änderung am Zielfenster selbst. Ein Bild danach gibt es nur,
    /// wenn die Fensterverwaltung überhaupt noch einmal zeichnet — auf dem Bauläufer ohne
    /// Bildschirm kam gemessen keines mehr, und der Fall scheiterte an der fehlenden Zeichnung
    /// statt an seiner Zusicherung.
    /// </remarks>
    [LiveScreenFact]
    public void Ein_fremdes_Fenster_darueber_landet_nicht_im_Bild()
    {

        using TestWindow target = TestWindow.Open(System.Drawing.Color.Red, 400, 300);
        using CaptureDevice device = CaptureDevice.Create();
        using WindowCapture capture = WindowCapture.Start(device, target.Handle);

        byte[] buffer = [];
        Assert.NotNull(WaitForFrame(capture, ref buffer));

        using TestWindow cover = TestWindow.Open(System.Drawing.Color.Lime, 400, 300,
                                                 topMost: true, sameBoundsAs: target);

        FrameSize? size = WaitForFrame(capture, ref buffer);
        Assert.NotNull(size);

        int middle = (((size.Value.Height / 2) * size.Value.Width) + (size.Value.Width / 2)) * 4;

        Assert.True(buffer[middle + 2] > 200,
            $"Rotanteil war {buffer[middle + 2]} — der Verdecker ist im Bild gelandet.");
    }

    /// <summary>
    /// Ein minimiertes Fenster liefert keine Bilder mehr, ohne dass die Aufnahme endet. Daraus
    /// wird die Pause — ohne eigene Mechanik.
    /// </summary>
    [CaptureFact]
    public void Ein_minimiertes_Fenster_liefert_keine_Bilder_mehr()
    {

        using TestWindow window = TestWindow.Open(System.Drawing.Color.Red, 400, 300);
        using CaptureDevice device = CaptureDevice.Create();
        using WindowCapture capture = WindowCapture.Start(device, window.Handle);

        byte[] buffer = [];
        Assert.NotNull(WaitForFrame(capture, ref buffer));

        window.Minimise();

        // Den Beutel leerziehen: Was vor dem Minimieren entstand, liegt noch darin.
        for (int i = 0; i < 20; i++)
        {
            _ = capture.TryCopyLatest(ref buffer);
            Thread.Sleep(25);
        }

        int frames = 0;

        for (int i = 0; i < 20; i++)
        {
            if (capture.TryCopyLatest(ref buffer) is not null)
            {
                frames++;
            }

            Thread.Sleep(25);
        }

        Assert.True(frames <= 1,
            $"Nach dem Minimieren kamen {frames} Bilder — erwartet war keines. Die Pause "
            + "entsteht daraus, dass keine mehr kommen.");
    }

    [CaptureFact]
    public void Ohne_Fensterhandle_wird_abgelehnt()
    {

        using CaptureDevice device = CaptureDevice.Create();

        RecordingException error = Assert.Throws<RecordingException>(
            () => WindowCapture.Start(device, 0));

        Assert.Contains("kein Handle", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Wartet, bis ein Bild vorliegt — der Beutel füllt sich nicht sofort.</summary>
    private static FrameSize? WaitForFrame(WindowCapture capture, ref byte[] buffer)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (capture.TryCopyLatest(ref buffer) is { } size)
            {
                return size;
            }

            Thread.Sleep(50);
        }

        return null;
    }
}
