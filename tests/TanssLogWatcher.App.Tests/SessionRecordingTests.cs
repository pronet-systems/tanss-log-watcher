using System.IO;
using System.Runtime.Versioning;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.Services;
using TanssLogWatcher.Recording;
using TanssLogWatcher.Recording.Tests;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Recordings;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Die Verbindung zwischen Aufnahme und Ablage: echtes Fenster, echte Datei, echte Buchführung.
/// </summary>
/// <remarks>
/// Das Aufnahmeprojekt prüft, dass aus einem Fenster ein abspielbares Video wird. Hier wird
/// geprüft, was danach kommt und was allein diese Schicht tut: dass jeder Abschnitt in der
/// Buchführung steht, dass seine Frist feststeht, und dass die Begleitdatei daneben liegt und
/// erklärt, was das Video nicht zeigt.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class SessionRecordingTests
{
    private static RecordingSection Settings(string root) => new()
    {
        Enabled = true,
        Directory = root,
        RetentionDays = 30,
        FramesPerSecond = 4,
        HeartbeatSeconds = 1,
        MinimumFreeMegabytes = 0,
        AcknowledgedAt = "2026-09-13T10:00:00+02:00",
        AcknowledgedBy = "S. Michel",
        LegalBasis = "Betriebsvereinbarung",
        LegalReference = "BV 2026-03 Fernwartung",
    };

    /// <summary>
    /// Der Fall, auf den es ankommt: Aus einer Sitzung wird eine Datei, ein Eintrag in der
    /// Buchführung und eine Begleitdatei — alle drei zusammengehörig.
    /// </summary>
    [CaptureFact]
    public async Task Aus_einer_Sitzung_wird_Datei_Eintrag_und_Begleitdatei()
    {
        using TempDirectory temp = new();
        using TestWindow window = TestWindow.Open(System.Drawing.Color.Red, 400, 300);

        string root = temp.File("Aufzeichnungen");
        using RecordingStore store = new(temp.File("state.db"));

        SessionSnapshot session = Sample.Session(window.Handle);
        RecordingSection settings = Settings(root);

        using (SessionRecording recording = new(session, settings, root, store,
                                                new FixedWindows(Box(window)),
                                                new FixedScreens(), TimeProvider.System))
        {
            recording.Start();

            // Zwei Sekunden Wanduhr: genug fuer mehrere Bilder und fuer eine Dauer, die sich
            // von null unterscheidet.
            await Task.Delay(TimeSpan.FromSeconds(2));

            recording.MarkEnded();
            await recording.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }

        RecordingEntry entry = Assert.Single(store.For(session.SessionId));

        Assert.Equal("kept", entry.State);
        Assert.Equal(1, entry.Segment);
        Assert.True(entry.Bytes > 1024,
            $"Die Datei ist mit {entry.Bytes} Byte zu klein, um Bilder zu enthalten.");
        Assert.True(entry.Recorded > TimeSpan.Zero, "Es ist nichts aufgezeichnet worden.");

        string file = Path.Combine(root, entry.RelativePath);

        Assert.True(File.Exists(file), $"Die eingetragene Datei fehlt: {entry.RelativePath}");
        Assert.Equal(new FileInfo(file).Length, entry.Bytes);

        string manifest = Path.Combine(
            root, RecordingPaths.FolderFor(session.StartedAt, session.SessionId),
            RecordingPaths.ManifestName);

        RecordingManifest sidecar =
            Assert.IsType<RecordingManifest>(RecordingManifest.Load(manifest));

        Assert.Equal(session.SessionId, sidecar.SessionId);
        Assert.Equal(session.Destination, sidecar.Destination);
        Assert.Equal(Path.GetFileName(file), Assert.Single(sidecar.Files));
        Assert.True(sidecar.RecordedSeconds >= 1,
            $"Die Begleitdatei nennt {sidecar.RecordedSeconds} aufgezeichnete Sekunden.");
    }

    /// <summary>
    /// Der gemeldete Vorfall, auf der obersten Ebene festgehalten: Ein Fenster, das während der
    /// Aufzeichnung zweimal seine Grösse ändert und am Ende maximiert ist, ergibt <b>eine</b>
    /// Datei und <b>einen</b> Eintrag in der Buchführung.
    /// </summary>
    /// <remarks>
    /// Im Betrieb waren es drei Dateien — 4 s, 1 s, 10 s — für eine Fernwartung von neunzehn
    /// Sekunden. Die Leinwand war die Hüllfläche der Fenster beim Start, und die Bildgrösse
    /// liegt im Encoder fest; jede neue Lage kostete deshalb eine Datei.
    /// </remarks>
    [CaptureFact]
    public async Task Ein_Fenster_das_seine_Groesse_aendert_ergibt_eine_einzige_Datei()
    {
        using TempDirectory temp = new();
        using TestWindow window = TestWindow.Open(System.Drawing.Color.Red, 400, 300);

        string root = temp.File("Aufzeichnungen");
        using RecordingStore store = new(temp.File("state.db"));

        FixedScreens screens = new();
        ScreenBox screen = screens.Screens()[0].Box;

        SessionSnapshot session = Sample.Session(window.Handle);

        using (SessionRecording recording = new(session, Settings(root), root, store,
                                                new GrowingWindow(window.Handle, screen),
                                                screens, TimeProvider.System))
        {
            recording.Start();
            await Task.Delay(TimeSpan.FromSeconds(3));
            recording.MarkEnded();
            await recording.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }

        RecordingEntry entry = Assert.Single(store.For(session.SessionId));

        Assert.Equal("kept", entry.State);
        Assert.Equal(1, entry.Segment);

        string folder = Path.Combine(
            root, RecordingPaths.FolderFor(session.StartedAt, session.SessionId));

        string[] videos = Directory.GetFiles(folder, "*.mp4");

        Assert.True(videos.Length == 1,
            $"Es sind {videos.Length} Videodateien entstanden: "
            + string.Join(", ", videos.Select(Path.GetFileName)));

        Assert.Equal(RecordingPaths.VideoName, Path.GetFileName(videos[0]));

        RecordingManifest sidecar = Assert.IsType<RecordingManifest>(RecordingManifest.Load(
            Path.Combine(folder, RecordingPaths.ManifestName)));

        Assert.Equal(RecordingPaths.VideoName, Assert.Single(sidecar.Files));

        // Die Leinwand ist der Bildschirm - deshalb passt jede der drei Lagen hinein.
        Assert.Equal($"{screen.Width}×{screen.Height}", sidecar.Canvas);
    }

    /// <summary>
    /// Ein Fenster, das über den linken und oberen Bildschirmrand hinausragt, ergibt kein
    /// schwarzes Video.
    /// </summary>
    /// <remarks>
    /// <para>Das ist der Regelfall einer Fernwartung und war beinahe der teuerste Fehler dieses
    /// Umbaus: Ein maximiertes Fenster meldet gemessen (−13,−13) bei 2906×1730 auf einem
    /// Bildschirm von 2880×1800 — sein unsichtbarer Anfassrahmen liegt ausserhalb. Das Zeichnen
    /// stieg bei negativem Platz vollständig aus, und mit der Bildschirmfläche als Leinwand wäre
    /// jede solche Aufzeichnung durchgehend schwarz gewesen, ohne eine einzige
    /// Fehlermeldung.</para>
    /// <para>Das Prüffenster ist randlos und meldet maximiert deshalb 0/0. Die Lage wird hier
    /// also unmittelbar hergestellt — ein Fenster, dessen Ecke wirklich vor dem Bildschirm
    /// liegt — statt sie über „maximieren“ zu erhoffen.</para>
    /// </remarks>
    [CaptureFact]
    public async Task Ein_Fenster_ueber_dem_Bildschirmrand_ergibt_kein_schwarzes_Video()
    {
        using TempDirectory temp = new();

        FixedScreens screens = new();
        ScreenBox screen = screens.Screens()[0].Box;

        using TestWindow window = TestWindow.Open(
            System.Drawing.Color.Red, 800, 600,
            at: new System.Drawing.Point(screen.Left - 13, screen.Top - 13));

        WindowBox box = Box(window);

        Assert.True(box.Left < screen.Left && box.Top < screen.Top,
            $"Das Fenster liegt bei {box.Left}/{box.Top}, der Bildschirm beginnt bei "
            + $"{screen.Left}/{screen.Top} — ohne Überstand prüft dieser Fall nichts.");

        string root = temp.File("Aufzeichnungen");
        using RecordingStore store = new(temp.File("state.db"));

        SessionSnapshot session = Sample.Session(window.Handle);

        using (SessionRecording recording = new(session, Settings(root), root, store,
                                                new FixedWindows(box), screens,
                                                TimeProvider.System))
        {
            recording.Start();
            await Task.Delay(TimeSpan.FromSeconds(2));
            recording.MarkEnded();
            await recording.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }

        RecordingEntry entry = Assert.Single(store.For(session.SessionId));

        // Eine Leinwand in Bildschirmgroesse, auf der nichts steht, packt sich auf wenige
        // hundert Byte je Bild zusammen. Ein echter Fensterinhalt ist deutlich mehr.
        Assert.True(entry.Bytes > 8192,
            $"Die Datei ist mit {entry.Bytes} Byte so klein, dass sie schwarz sein dürfte — "
            + "genau der Fehler, den das Beschneiden an allen vier Seiten verhindern soll.");
    }

    /// <summary>
    /// Die Löschfrist wird beim Beginn festgeschrieben und steht in beiden: in der Buchführung
    /// und in der Begleitdatei. Wer eine Aufzeichnung Monate später in der Hand hält, soll
    /// nicht in einer Konfigurationsdatei nachsehen müssen.
    /// </summary>
    [CaptureFact]
    public async Task Die_Loeschfrist_steht_in_Buchfuehrung_und_Begleitdatei()
    {
        using TempDirectory temp = new();
        using TestWindow window = TestWindow.Open(System.Drawing.Color.Blue, 320, 240);

        string root = temp.File("Aufzeichnungen");
        using RecordingStore store = new(temp.File("state.db"));

        SessionSnapshot session = Sample.Session(window.Handle);
        RecordingSection settings = Settings(root) with { RetentionDays = 7 };

        using (SessionRecording recording = new(session, settings, root, store,
                                                new FixedWindows(Box(window)),
                                                new FixedScreens(), TimeProvider.System))
        {
            recording.Start();
            await Task.Delay(TimeSpan.FromSeconds(1));
            recording.MarkEnded();
            await recording.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }

        RecordingEntry entry = Assert.Single(store.For(session.SessionId));

        // Die Frist laeuft ab dem Eintrag, nicht ab dem Ende: Das steht so in der Buchfuehrung.
        Assert.True(entry.DeleteAfter > DateTimeOffset.UtcNow.AddDays(6));
        Assert.True(entry.DeleteAfter < DateTimeOffset.UtcNow.AddDays(8));

        RecordingManifest sidecar = Assert.IsType<RecordingManifest>(RecordingManifest.Load(
            Path.Combine(root, RecordingPaths.FolderFor(session.StartedAt, session.SessionId),
                         RecordingPaths.ManifestName)));

        Assert.Equal(session.StartedAt + TimeSpan.FromDays(7), sidecar.DeleteAfter);
        Assert.Equal("Betriebsvereinbarung", sidecar.LegalBasis);
        Assert.Equal("BV 2026-03 Fernwartung", sidecar.LegalReference);
    }

    /// <summary>
    /// Der Aufräumer greift auf genau das, was hier entstanden ist: Ist die Frist um, geht die
    /// Datei — und mit ihr die Begleitdatei, die Gegenstelle und Techniker trägt.
    /// </summary>
    [CaptureFact]
    public async Task Nach_Ablauf_der_Frist_holt_der_Aufraeumer_alles_ab()
    {
        using TempDirectory temp = new();
        using TestWindow window = TestWindow.Open(System.Drawing.Color.Green, 320, 240);

        string root = temp.File("Aufzeichnungen");
        using RecordingStore store = new(temp.File("state.db"));

        SessionSnapshot session = Sample.Session(window.Handle);

        using (SessionRecording recording = new(session, Settings(root), root, store,
                                                new FixedWindows(Box(window)),
                                                new FixedScreens(), TimeProvider.System))
        {
            recording.Start();
            await Task.Delay(TimeSpan.FromSeconds(1));
            recording.MarkEnded();
            await recording.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }

        string folder = Path.Combine(
            root, RecordingPaths.FolderFor(session.StartedAt, session.SessionId));

        Assert.True(Directory.Exists(folder));

        // Eine Frist von einer Sekunde: Die eben entstandene Aufzeichnung ist damit sofort
        // faellig - verkuerzen wirkt auch auf bestehende.
        CleanupResult result = new RecordingCleaner(store, root).Run(TimeSpan.FromSeconds(1));

        Assert.Equal(1, result.Deleted);
        Assert.False(Directory.Exists(folder),
            "Der Ordner steht noch — samt Begleitdatei, die Gegenstelle und Techniker trägt.");
        Assert.Equal("purged", Assert.Single(store.For(session.SessionId)).State);
    }

    private static WindowBox Box(TestWindow window)
    {
        System.Drawing.Rectangle bounds = window.Bounds;
        return new WindowBox(window.Handle, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}
