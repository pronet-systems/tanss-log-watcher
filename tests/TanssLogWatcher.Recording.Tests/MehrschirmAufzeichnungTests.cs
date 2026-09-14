using Xunit;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Ein Test, der mehr als einen angeschlossenen Bildschirm braucht.
/// </summary>
/// <remarks>
/// Übersprungen ist nicht bestanden — dasselbe Muster wie bei <see cref="CaptureFactAttribute"/>.
/// Auf einem Einschirmrechner lässt sich am echten Aufbau nichts über zwei Bildschirme
/// beweisen; die erfundenen Lagen in <see cref="CanvasLayoutTests"/> und
/// <see cref="RecordingDirectorTests"/> prüfen dieselbe Rechnung dort weiter.
/// </remarks>
public sealed class MultiScreenFactAttribute : FactAttribute
{
    /// <summary>Baut das Kennzeichen und überspringt, wo der Aufbau fehlt.</summary>
    public MultiScreenFactAttribute()
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                || !WindowCapture.IsSupported())
            {
                Skip = "Dieser Rechner hat keine Bildschirmaufnahme.";
                return;
            }

            if (new Win32ScreenSource().Screens().Count < 2)
            {
                Skip = "Dieser Rechner hat nur einen Bildschirm; der Mehrschirmfall lässt sich "
                    + "hier nicht messen.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Skip = "Die Bildschirme liessen sich nicht ermitteln: " + ex.Message;
        }
    }
}

/// <summary>
/// Die Aufzeichnung am echten Mehrschirmaufbau — gemessen am Bild, nicht am Rückgabewert.
/// </summary>
/// <remarks>
/// <para><b>Warum das nicht mit erfundenen Lagen geht.</b> Die Rechnung des Direktors lässt
/// sich mit erfundenen Bildschirmen prüfen, und das geschieht nebenan. Was sich nur hier prüfen
/// lässt, ist die Kette bis zur Datei: ob die Aufnahme eines Fensters bei negativem X
/// überhaupt etwas liefert, ob die Leinwand die Höhe des Bildschirms bekommt, auf dem das
/// Fenster steht, und ob der Bildpunkt im fertigen Video an der erwarteten Stelle liegt.</para>
/// <para><b>Geprüft wird der Bildpunkt.</b> Eine Datei mit richtiger Dauer und richtiger Grösse
/// kann durchgehend schwarz sein — das ist gerade der Fehler, den ein Bildschirm links vom
/// Ursprung hervorruft. Deshalb wird das Video mit dem Decoder von Windows aufgemacht
/// (<see cref="Mp4Frame"/>) und nachgesehen.</para>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
public sealed class MehrschirmAufzeichnungTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "tanss-mehrschirm-" + Guid.NewGuid().ToString("N"));

    /// <summary>Legt den Ablageort der Probeaufzeichnungen an.</summary>
    public MehrschirmAufzeichnungTests() => Directory.CreateDirectory(_folder);

    /// <summary>Räumt die Probeaufzeichnungen weg.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Aufraeumen ist Nebensache; ein gesperrter Rest ist kein Fehler.
        }
    }

    /// <summary>
    /// Ein Fenster auf dem Bildschirm links vom Hauptbildschirm — also bei negativem X.
    /// </summary>
    [MultiScreenFact]
    public void Ein_Fenster_bei_negativem_X_landet_im_Bild()
    {
        IReadOnlyList<ScreenInfo> screens = new Win32ScreenSource().Screens();
        ScreenBox left = screens.MinBy(s => s.Box.Left).Box;

        Assert.True(left.Left < 0,
            $"Dieser Aufbau hat keinen Bildschirm links vom Ursprung (linkester: {left.Left}). "
            + "Der Fall, um den es geht, ist hier nicht zu messen.");

        Record("negativ.mp4", left, screens,
               at: new System.Drawing.Point(left.Left + 420, 100),
               size: new System.Drawing.Size(1200, 900));
    }

    /// <summary>
    /// Ein Fenster auf dem Bildschirm rechts vom Hauptbildschirm — also weit jenseits von 1920.
    /// </summary>
    [MultiScreenFact]
    public void Ein_Fenster_rechts_aussen_landet_im_Bild()
    {
        IReadOnlyList<ScreenInfo> screens = new Win32ScreenSource().Screens();
        ScreenBox right = screens.MaxBy(s => s.Box.Left).Box;

        Assert.True(right.Left > 0, "Dieser Aufbau hat keinen Bildschirm rechts vom Ursprung.");

        Record("rechts.mp4", right, screens,
               at: new System.Drawing.Point(right.Left + 580, 100),
               size: new System.Drawing.Size(1200, 900));
    }

    /// <summary>
    /// Der höhere Bildschirm: Die Leinwand bekommt SEINE Höhe und nicht die des
    /// Hauptbildschirms.
    /// </summary>
    /// <remarks>
    /// Der Aufbau, an dem das auffällt: 1080, 1080 und 1200 nebeneinander. Wer die Leinwand aus
    /// dem Hauptbildschirm ableitete, schnitte auf dem hohen Bildschirm 120 Bildpunkte ab — und
    /// zwar unten, wo die Schaltflächen der Dialoge liegen.
    /// </remarks>
    [MultiScreenFact]
    public void Der_hoehere_Bildschirm_bekommt_seine_eigene_Leinwandhoehe()
    {
        IReadOnlyList<ScreenInfo> screens = new Win32ScreenSource().Screens();
        ScreenBox tallest = screens.MaxBy(s => s.Box.Height).Box;
        ScreenBox primary = screens.Single(s => s.IsPrimary).Box;

        Assert.True(tallest.Height != primary.Height,
            $"Alle Bildschirme sind gleich hoch ({tallest.Height}); der Fall der "
            + "unterschiedlichen Höhen ist hier nicht zu messen.");

        // Das Fenster reicht bis in die unteren 120 Bildpunkte hinein - genau in den Streifen,
        // den eine aus dem Hauptbildschirm abgeleitete Leinwand verloere.
        Record("hoch.mp4", tallest, screens,
               at: new System.Drawing.Point(tallest.Left + 300, tallest.Bottom - 700),
               size: new System.Drawing.Size(1000, 650));
    }

    /// <summary>
    /// Ein Fenster über der Grenze zweier Bildschirme: Die Leinwand umfasst beide, und das
    /// Fenster ist auf beiden Hälften zu sehen.
    /// </summary>
    [MultiScreenFact]
    public void Ein_Fenster_ueber_der_Bildschirmgrenze_ist_auf_beiden_Haelften_zu_sehen()
    {
        IReadOnlyList<ScreenInfo> screens = new Win32ScreenSource().Screens();

        List<ScreenInfo> ordered = [.. screens.OrderBy(s => s.Box.Left)];
        ScreenBox a = ordered[0].Box;
        ScreenBox b = ordered[1].Box;

        Assert.Equal(a.Right, b.Left);

        // 400 Bildpunkte links der Grenze, 400 rechts davon.
        using TestWindow window = TestWindow.Open(System.Drawing.Color.Red, 800, 600,
            at: new System.Drawing.Point(a.Right - 400, 100));

        using SessionRecorder recorder = new(Options(),
            () => Path.Combine(_folder, "grenze.mp4"));

        string path = Run(recorder, window, screens);

        (int Width, int Height)? size = Mp4Duration.SizeOf(path);

        Assert.NotNull(size);

        int top = Math.Min(a.Top, b.Top);
        int expectedWidth = Even(b.Right - a.Left);
        int expectedHeight = Even(Math.Max(a.Bottom, b.Bottom) - top);

        Assert.Equal(expectedWidth, size.Value.Width);
        Assert.Equal(expectedHeight, size.Value.Height);

        byte[] pixels = Frame(path, size.Value);

        // Links der Grenze und rechts der Grenze, jeweils mitten im Fenster.
        int y = 100 - top + 300;
        int leftSample = a.Right - 400 - a.Left + 200;
        int rightSample = a.Right - 400 - a.Left + 600;

        Assert.True(Mp4Frame.IsRed(pixels, size.Value.Width, leftSample, y),
            $"Links der Bildschirmgrenze ({leftSample}/{y}) stand "
            + $"{Describe(pixels, size.Value.Width, leftSample, y)} statt Rot.");

        Assert.True(Mp4Frame.IsRed(pixels, size.Value.Width, rightSample, y),
            $"Rechts der Bildschirmgrenze ({rightSample}/{y}) stand "
            + $"{Describe(pixels, size.Value.Width, rightSample, y)} statt Rot — der Teil "
            + "jenseits der Grenze fehlt im Bild.");
    }

    /// <summary>
    /// Zeichnet ein rotes Fenster auf einem bestimmten Bildschirm auf und sieht im Bild nach.
    /// </summary>
    /// <param name="name">Der Dateiname der Probeaufzeichnung.</param>
    /// <param name="screen">Der Bildschirm, auf dem das Fenster steht.</param>
    /// <param name="screens">Alle Bildschirme, so wie der Dienst sie hereinreicht.</param>
    /// <param name="at">Die linke obere Ecke des Fensters, in Bildschirmkoordinaten.</param>
    /// <param name="size">Die Grösse des Fensters.</param>
    private void Record(string name, ScreenBox screen, IReadOnlyList<ScreenInfo> screens,
                        System.Drawing.Point at, System.Drawing.Size size)
    {
        using TestWindow window = TestWindow.Open(System.Drawing.Color.Red,
            size.Width, size.Height, at: at);

        using SessionRecorder recorder = new(Options(), () => Path.Combine(_folder, name));

        string path = Run(recorder, window, screens);

        (int Width, int Height)? actual = Mp4Duration.SizeOf(path);

        Assert.NotNull(actual);

        Assert.True(actual.Value.Width == Even(screen.Width)
                    && actual.Value.Height == Even(screen.Height),
            $"Die Datei misst {actual.Value.Width}×{actual.Value.Height}; der Bildschirm, auf "
            + $"dem das Fenster steht, misst {screen.Width}×{screen.Height}.");

        byte[] pixels = Frame(path, actual.Value);

        // Die Mitte des Fensters, umgerechnet auf die Leinwand: Bildschirmkoordinate minus
        // Bildschirmursprung.
        int x = at.X - screen.Left + (size.Width / 2);
        int y = at.Y - screen.Top + (size.Height / 2);

        Assert.True(Mp4Frame.IsRed(pixels, actual.Value.Width, x, y),
            $"In der Mitte des Fensters ({x}/{y} auf einer Leinwand von {actual.Value.Width}"
            + $"×{actual.Value.Height}) stand {Describe(pixels, actual.Value.Width, x, y)} "
            + "statt Rot. Das Video ist an dieser Stelle schwarz oder verschoben.");

        // Und daneben, wo nichts zur Sitzung gehoert, ist Schwarz richtig.
        int outside = at.X - screen.Left > 40 ? 10 : actual.Value.Width - 10;

        Assert.True(Mp4Frame.IsBlack(pixels, actual.Value.Width, outside, y),
            $"Neben dem Fenster ({outside}/{y}) stand "
            + $"{Describe(pixels, actual.Value.Width, outside, y)} statt Schwarz.");
    }

    private static string Run(SessionRecorder recorder, TestWindow window,
                              IReadOnlyList<ScreenInfo> screens)
    {
        System.Drawing.Rectangle bounds = window.Bounds;
        WindowBox box = new(window.Handle, bounds.X, bounds.Y, bounds.Width, bounds.Height);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Drei Sekunden in Vierteltakten - lang genug, dass der Kodierer ein vollstaendiges
        // Bild geschrieben hat, an dem sich der Bildpunkt ablesen laesst.
        for (int i = 0; i < 24; i++)
        {
            Assert.True(recorder.Tick(
                new RecordingInput(now, [box], SessionEnded: false, FreeMegabytes: 100_000,
                                   Screens: screens)));

            Thread.Sleep(125);
            now = now.AddMilliseconds(125);
        }

        recorder.Stop();

        string path = Assert.Single(recorder.Files);

        Assert.True(File.Exists(path), "Es ist keine Datei entstanden.");

        return path;
    }

    private static byte[] Frame(string path, (int Width, int Height) size)
    {
        byte[]? pixels = Mp4Frame.Of(path, TimeSpan.FromSeconds(1.5), size.Width, size.Height);

        Assert.True(pixels is not null,
            "Aus der Datei liess sich kein Bild decodieren — ein Abspieler bekäme sie auch "
            + "nicht auf.");

        return pixels!;
    }

    private static RecordingOptions Options() => new()
    {
        FramesPerSecond = 4,
        Heartbeat = TimeSpan.FromMilliseconds(500),
        MinimumFreeMegabytes = 0,
        PauseGrace = TimeSpan.FromSeconds(5),
    };

    private static string Describe(byte[] pixels, int width, int x, int y)
    {
        (int b, int g, int r) = Mp4Frame.At(pixels, width, x, y);
        return $"R{r}/G{g}/B{b}";
    }

    private static int Even(int value) => value % 2 == 0 ? value : value + 1;
}
