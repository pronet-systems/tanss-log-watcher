using Xunit;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Der Kodierer, gegen echte Dateien.
/// </summary>
/// <remarks>
/// Hier wird nichts nachgestellt: Media Foundation schreibt eine richtige mp4-Datei, und
/// geprüft wird, was danach auf der Platte liegt. Eine Attrappe könnte genau den Fehler nicht
/// finden, um den es geht — dass ohne Abschluss keine abspielbare Datei entsteht.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows6.1")]
public sealed class VideoFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "tanss-aufzeichnung-" + Guid.NewGuid().ToString("N"));

    public VideoFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Der Testlauf raeumt auf, nicht die Pruefung. Ein gesperrter Rest ist kein Fehler.
        }
    }

    /// <summary>
    /// Der Grundfall: Bilder hinein, abspielbare Datei heraus.
    /// </summary>
    [Fact]
    public void Aus_Bildern_wird_eine_abspielbare_Datei()
    {
        string path = Path.Combine(_folder, "grundfall.mp4");

        using (VideoFile file = VideoFile.Create(path, 320, 240, framesPerSecond: 4))
        {
            Schreibe(file, frames: 20, framesPerSecond: 4);
            file.Complete();

            Assert.Equal(20, file.WrittenFrames);
        }

        FileInfo written = new(path);

        Assert.True(written.Exists, "Die Datei wurde nicht angelegt.");
        Assert.True(written.Length > 1024,
            $"Die Datei ist mit {written.Length} Byte zu klein, um zwanzig Bilder zu enthalten.");

        // Die Kopfdaten einer mp4: Das zweite Viererfeld traegt "ftyp". Ohne den Abschluss
        // stuende hier zwar auch etwas, aber die Datei haette keinen Index - siehe den Test
        // darunter, der genau das misst.
        byte[] head = new byte[12];
        using (FileStream stream = File.OpenRead(path))
        {
            Assert.Equal(12, stream.Read(head, 0, 12));
        }

        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(head, 4, 4));
    }

    /// <summary>
    /// Ohne Abschluss schreibt Media Foundation den Index nicht — heraus kommt eine Datei, die
    /// kein Abspieler öffnet. Genau deshalb ist <c>Complete</c> nicht <c>Dispose</c>.
    /// </summary>
    [Fact]
    public void Ohne_Abschluss_bleibt_die_Datei_unbrauchbar()
    {
        string mitAbschluss = Path.Combine(_folder, "mit.mp4");
        string ohneAbschluss = Path.Combine(_folder, "ohne.mp4");

        using (VideoFile file = VideoFile.Create(mitAbschluss, 320, 240, 4))
        {
            Schreibe(file, 20, 4);
            file.Complete();
        }

        using (VideoFile file = VideoFile.Create(ohneAbschluss, 320, 240, 4))
        {
            Schreibe(file, 20, 4);

            // Kein Complete - nur Dispose durch das using.
        }

        long mit = new FileInfo(mitAbschluss).Length;
        long ohne = new FileInfo(ohneAbschluss).Length;

        Assert.True(mit > ohne,
            $"Die abgeschlossene Datei ({mit} Byte) müsste grösser sein als die nicht "
            + $"abgeschlossene ({ohne} Byte) — der Index fehlt dort.");
    }

    /// <summary>
    /// Zweimal abschliessen ist erlaubt und tut beim zweiten Mal nichts. Das geordnete Ende
    /// läuft über mehrere Wege — Sitzungsende, Herunterfahren, Abschnittswechsel —, und die
    /// dürfen sich nicht gegenseitig ins Gehege kommen.
    /// </summary>
    [Fact]
    public void Zweimal_Abschliessen_ist_kein_Fehler()
    {
        string path = Path.Combine(_folder, "zweimal.mp4");

        using VideoFile file = VideoFile.Create(path, 320, 240, 4);
        Schreibe(file, 8, 4);

        file.Complete();
        file.Complete();

        Assert.True(new FileInfo(path).Length > 0);
    }

    /// <summary>
    /// Die Zeitstempel kommen von aussen. Eine Aufzeichnung mit Pause schreibt weniger Bilder,
    /// und die Datei ist entsprechend kürzer — sie behauptet keine Zeit, die es nicht gab.
    /// </summary>
    [Fact]
    public void Eine_Aufzeichnung_mit_Pause_ergibt_eine_kuerzere_Datei()
    {
        string ohnePause = Path.Combine(_folder, "durchgehend.mp4");
        string mitPause = Path.Combine(_folder, "mit-pause.mp4");

        using (VideoFile file = VideoFile.Create(ohnePause, 320, 240, 4))
        {
            Schreibe(file, 40, 4);
            file.Complete();
        }

        using (VideoFile file = VideoFile.Create(mitPause, 320, 240, 4))
        {
            // Dieselbe Wanduhrspanne, aber die Haelfte war Pause: halb so viele Bilder.
            Schreibe(file, 20, 4);
            file.Complete();
        }

        Assert.True(new FileInfo(mitPause).Length < new FileInfo(ohnePause).Length);
    }

    /// <summary>
    /// H.264 mit 4:2:0 verlangt gerade Kantenlängen. Die Leinwand rundet auf — ein ungerader
    /// Wert kommt also nicht von dort, und wer trotzdem einen übergibt, bekommt es gesagt.
    /// </summary>
    [Theory]
    [InlineData(321, 240)]
    [InlineData(320, 241)]
    [InlineData(0, 240)]
    public void Eine_ungerade_Leinwand_wird_abgelehnt(int width, int height)
    {
        string path = Path.Combine(_folder, "ungerade.mp4");

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => VideoFile.Create(path, width, height, 4));

        Assert.Contains("gerade Kantenlängen", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_Puffer_falscher_Groesse_wird_abgelehnt()
    {
        string path = Path.Combine(_folder, "puffer.mp4");

        using VideoFile file = VideoFile.Create(path, 320, 240, 4);

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => file.Write(new byte[100], TimeSpan.Zero, TimeSpan.FromMilliseconds(250)));

        Assert.Contains("halbes Bild", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Media Foundation läuft mit Zählwerk: Zwei gleichzeitige Dateien sind der Normalfall beim
    /// Abschnittswechsel, und das Schliessen der einen darf der anderen nicht den Boden
    /// wegziehen.
    /// </summary>
    [Fact]
    public void Zwei_Dateien_gleichzeitig_stoeren_einander_nicht()
    {
        string erste = Path.Combine(_folder, "erste.mp4");
        string zweite = Path.Combine(_folder, "zweite.mp4");

        using VideoFile a = VideoFile.Create(erste, 320, 240, 4);
        Schreibe(a, 8, 4);

        using (VideoFile b = VideoFile.Create(zweite, 320, 240, 4))
        {
            Schreibe(b, 8, 4);
            b.Complete();
        }

        // Die erste muss nach dem Schliessen der zweiten weiterschreiben koennen.
        Schreibe(a, 8, 4, startIndex: 8);
        a.Complete();

        Assert.Equal(16, a.WrittenFrames);
        Assert.True(new FileInfo(erste).Length > 1024);
    }

    /// <summary>
    /// Der Test, auf den es ankommt: Die Dauer wird aus der DATEI gelesen, nicht aus unserer
    /// Buchführung.
    /// </summary>
    /// <remarks>
    /// Vierzig Bilder bei vier je Sekunde sind zehn Sekunden. Stünde die Dauer in der Datei
    /// anders, wäre die ganze Zusage hinfällig — dass eine Aufzeichnung mit Pausen genau die
    /// Zeit zeigt, in der etwas geschah, und keine Sekunde mehr.
    /// </remarks>
    [Fact]
    public void Die_Dauer_in_der_Datei_stimmt_mit_den_geschriebenen_Bildern_ueberein()
    {
        string path = Path.Combine(_folder, "dauer.mp4");

        using (VideoFile file = VideoFile.Create(path, 320, 240, framesPerSecond: 4))
        {
            Schreibe(file, frames: 40, framesPerSecond: 4);
            file.Complete();
        }

        TimeSpan? duration = Mp4Duration.Of(path);

        Assert.NotNull(duration);

        // Zehn Sekunden, mit einem Bild Spielraum: Der Behaelter rundet auf sein Zeitmass.
        Assert.InRange(duration.Value.TotalSeconds, 9.7, 10.3);
    }

    /// <summary>
    /// Dieselbe Wanduhrspanne, die Hälfte davon Pause — und die Datei ist halb so lang. Das ist
    /// der Unterschied zwischen einer ehrlichen Aufzeichnung und einer, die Standbilder als
    /// Arbeitszeit ausgibt.
    /// </summary>
    [Fact]
    public void Eine_Pause_verkuerzt_die_Dauer_in_der_Datei()
    {
        string durchgehend = Path.Combine(_folder, "dauer-voll.mp4");
        string mitPause = Path.Combine(_folder, "dauer-pause.mp4");

        using (VideoFile file = VideoFile.Create(durchgehend, 320, 240, 4))
        {
            Schreibe(file, 40, 4);
            file.Complete();
        }

        using (VideoFile file = VideoFile.Create(mitPause, 320, 240, 4))
        {
            Schreibe(file, 20, 4);
            file.Complete();
        }

        TimeSpan voll = Mp4Duration.Of(durchgehend)!.Value;
        TimeSpan pause = Mp4Duration.Of(mitPause)!.Value;

        Assert.InRange(voll.TotalSeconds, 9.7, 10.3);
        Assert.InRange(pause.TotalSeconds, 4.7, 5.3);
    }

    /// <summary>Schreibt Bilder mit wechselnder Farbe, damit der Kodierer etwas zu tun hat.</summary>
    private static void Schreibe(VideoFile file, int frames, int framesPerSecond,
                                 int startIndex = 0)
    {
        long ticksPerFrame = TimeSpan.TicksPerSecond / framesPerSecond;
        byte[] canvas = new byte[file.Width * file.Height * 4];

        for (int i = 0; i < frames; i++)
        {
            int index = startIndex + i;

            // Ein wanderndes Band: Ein vollstaendig gleichfoermiges Bild komprimierte auf
            // nahezu nichts und liesse den Test auch dann bestehen, wenn gar nichts kodiert
            // wuerde.
            byte level = (byte)(index * 11 % 256);

            for (int p = 0; p < canvas.Length; p += 4)
            {
                canvas[p] = level;
                canvas[p + 1] = (byte)(255 - level);
                canvas[p + 2] = (byte)(p / 4 % 256);
                canvas[p + 3] = 255;
            }

            file.Write(canvas,
                       TimeSpan.FromTicks(index * ticksPerFrame),
                       TimeSpan.FromTicks(ticksPerFrame));
        }
    }
}
