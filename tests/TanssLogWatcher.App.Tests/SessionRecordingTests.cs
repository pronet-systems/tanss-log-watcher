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
        SegmentMinutes = 10,
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
                                                TimeProvider.System))
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
                                                TimeProvider.System))
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
                                                TimeProvider.System))
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
