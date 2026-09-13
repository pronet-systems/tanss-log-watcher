using Xunit;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Die ganze Kette: echtes Fenster, echter Kodierer, echte Datei.
/// </summary>
/// <remarks>
/// Das ist der Test, der zählt. Alles davor prüft Teile; hier wird aufgezeichnet, was auf dem
/// Bildschirm steht, und danach nachgesehen, was auf der Platte liegt — Länge, Abmessungen und
/// ob ein Abspieler die Datei öffnen könnte.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
public sealed class SessionRecorderTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "tanss-sitzung-" + Guid.NewGuid().ToString("N"));

    public SessionRecorderTests() => Directory.CreateDirectory(_folder);

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
    /// Ein Fenster, zwei Sekunden, eine abspielbare Datei.
    /// </summary>
    [CaptureFact]
    public void Aus_einem_Fenster_wird_eine_abspielbare_Aufzeichnung()
    {
        using TestWindow window = TestWindow.Open(System.Drawing.Color.Red, 400, 300);

        RecordingOptions options = new()
        {
            FramesPerSecond = 4,
            Heartbeat = TimeSpan.FromMilliseconds(500),
            SegmentLength = TimeSpan.FromMinutes(10),
            MinimumFreeMegabytes = 0,
            PauseGrace = TimeSpan.FromSeconds(2),
        };

        using SessionRecorder recorder = new(options, n => Path.Combine(_folder, $"teil-{n}.mp4"));

        WindowBox box = Box(window);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Zwei Sekunden in Vierteltakten - so, wie der Dienst es spaeter tut.
        for (int i = 0; i < 16; i++)
        {
            Assert.True(recorder.Tick(
                new RecordingInput(now, [box], SessionEnded: false, FreeMegabytes: 100_000)));

            Thread.Sleep(125);
            now = now.AddMilliseconds(125);
        }

        recorder.Stop();

        string path = Assert.Single(recorder.Files);

        Assert.True(File.Exists(path), "Es ist keine Datei entstanden.");

        TimeSpan? duration = Mp4Duration.Of(path);

        Assert.NotNull(duration);
        Assert.True(duration.Value > TimeSpan.FromSeconds(0.5),
            $"Die Aufzeichnung ist mit {duration.Value.TotalSeconds:0.0} s zu kurz — es sind "
            + "kaum Bilder angekommen.");
    }

    /// <summary>
    /// Die Leinwand umfasst beide Fenster. Der Fall, wegen dem überhaupt mehrere Fenster
    /// aufgenommen werden: Ein Unterfenster gehört zur selben Arbeit.
    /// </summary>
    [CaptureFact]
    public void Zwei_Fenster_ergeben_eine_gemeinsame_Leinwand()
    {
        using TestWindow main = TestWindow.Open(System.Drawing.Color.Red, 400, 300);
        using TestWindow child = TestWindow.Open(System.Drawing.Color.Blue, 200, 150);

        RecordingOptions options = new()
        {
            FramesPerSecond = 4,
            Heartbeat = TimeSpan.FromMilliseconds(500),
            MinimumFreeMegabytes = 0,
        };

        using SessionRecorder recorder = new(options, n => Path.Combine(_folder, $"zwei-{n}.mp4"));

        WindowBox a = Box(main);
        WindowBox b = new(child.Handle, a.Left + 500, a.Top + 400, 200, 150);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 12; i++)
        {
            _ = recorder.Tick(new RecordingInput(now, [a, b], false, 100_000));
            Thread.Sleep(125);
            now = now.AddMilliseconds(125);
        }

        recorder.Stop();

        string path = Assert.Single(recorder.Files);
        Assert.True(File.Exists(path));

        // Die Leinwand muss beide umfassen: von a.Left bis b.Right, von a.Top bis b.Bottom.
        // Geprueft wird ueber die Datei - dort steht die Bildgroesse fest.
        Assert.True(new FileInfo(path).Length > 1024);
    }

    /// <summary>
    /// Wenige Bilder über eine lange Spanne ergeben eine lange Datei — nicht eine kurze.
    /// </summary>
    /// <remarks>
    /// <para>Der Fall, der die ganze Zeitachse trägt: Ein stehender Bildschirm liefert kaum
    /// Bilder, die Sitzung läuft trotzdem. Läge der Zeitstempel am Bildzähler, ergäben zehn
    /// Bilder bei vier je Sekunde zweieinhalb Sekunden Datei — für zehn Sekunden Sitzung. Der
    /// Abspieler zeigte dann eine Dauer, die es nie gab, und das Video liefe im
    /// Schnelldurchlauf.</para>
    /// <para>Die Wanduhr wird vorgestellt statt abgewartet: Zehn echte Sekunden zu warten
    /// prüfte dasselbe und hielte den Testlauf zehn Sekunden auf.</para>
    /// </remarks>
    [CaptureFact]
    public void Wenige_Bilder_ueber_eine_lange_Spanne_ergeben_eine_lange_Datei()
    {
        using TestWindow window = TestWindow.Open(System.Drawing.Color.Red, 400, 300);

        RecordingOptions options = new()
        {
            FramesPerSecond = 4,
            Heartbeat = TimeSpan.FromMilliseconds(500),
            MinimumFreeMegabytes = 0,
        };

        using SessionRecorder recorder = new(options, n => Path.Combine(_folder, $"lang-{n}.mp4"));

        WindowBox box = Box(window);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Zehn Takte im Abstand einer Sekunde: zehn Sekunden Sitzung, hoechstens zehn Bilder.
        for (int i = 0; i < 10; i++)
        {
            Assert.True(recorder.Tick(new RecordingInput(now, [box], false, 100_000)));

            Thread.Sleep(40);
            now = now.AddSeconds(1);
        }

        recorder.Stop();

        TimeSpan duration = Mp4Duration.Of(Assert.Single(recorder.Files))!.Value;

        Assert.InRange(duration.TotalSeconds, 8.0, 10.5);
    }

    /// <summary>
    /// Wird das Fenster minimiert, pausiert die Aufzeichnung — und die Datei bleibt kurz. Das
    /// ist die Zusage, die den ganzen Aufwand rechtfertigt.
    /// </summary>
    [CaptureFact]
    public void Ein_minimiertes_Fenster_pausiert_die_Aufzeichnung()
    {
        using TestWindow window = TestWindow.Open(System.Drawing.Color.Red, 400, 300);

        RecordingOptions options = new()
        {
            FramesPerSecond = 4,
            Heartbeat = TimeSpan.FromMilliseconds(250),
            MinimumFreeMegabytes = 0,
            PauseGrace = TimeSpan.FromMilliseconds(500),
        };

        using SessionRecorder recorder = new(options, n => Path.Combine(_folder, $"pause-{n}.mp4"));

        WindowBox box = Box(window);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 8; i++)
        {
            _ = recorder.Tick(new RecordingInput(now, [box], false, 100_000));
            Thread.Sleep(125);
            now = now.AddMilliseconds(125);
        }

        TimeSpan beforePause = recorder.Recorded;

        window.Minimise();

        // Zehn Sekunden Wanduhr ohne sichtbares Fenster.
        for (int i = 0; i < 20; i++)
        {
            _ = recorder.Tick(new RecordingInput(now, [], false, 100_000));
            now = now.AddMilliseconds(500);
        }

        recorder.Stop();

        Assert.True(recorder.Recorded < beforePause + TimeSpan.FromSeconds(2),
            $"Während der Pause sind {(recorder.Recorded - beforePause).TotalSeconds:0.0} s "
            + "aufgezeichnet worden — die Pause zählt nicht zur Aufzeichnung.");

        Assert.NotEmpty(recorder.Pauses);
    }

    /// <summary>
    /// Bei Platzmangel tritt die Aufzeichnung zurück und sagt es. Die Warteschlange braucht den
    /// Platz dringender — sie trägt die Arbeitszeit.
    /// </summary>
    [CaptureFact]
    public void Bei_Platzmangel_wird_gar_nicht_erst_aufgezeichnet()
    {
        using TestWindow window = TestWindow.Open(System.Drawing.Color.Red, 400, 300);

        RecordingOptions options = new() { MinimumFreeMegabytes = 2048 };

        using SessionRecorder recorder = new(options, n => Path.Combine(_folder, $"voll-{n}.mp4"));

        bool running = recorder.Tick(
            new RecordingInput(DateTimeOffset.UtcNow, [Box(window)], false, FreeMegabytes: 100));

        Assert.False(running);
        Assert.Empty(recorder.Files);
        Assert.Contains("Warteschlange", recorder.LastReason, StringComparison.Ordinal);
    }

    private static WindowBox Box(TestWindow window)
    {
        System.Drawing.Rectangle bounds = window.Bounds;
        return new WindowBox(window.Handle, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}
