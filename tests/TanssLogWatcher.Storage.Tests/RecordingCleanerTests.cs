using TanssLogWatcher.Storage.Recordings;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Der Aufräumer — gegen echte Dateien, denn er löscht echte Dateien.
/// </summary>
public sealed class RecordingCleanerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Was_faellig_ist_wird_geloescht()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        string root = temp.File("aufzeichnungen");
        string relative = Path.Combine("2026-09-13", "1000-abcd1234", "teil-01.mp4");
        string full = Path.Combine(root, relative);

        _ = Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[4096]);

        long id = store.Begin("sitzung-1", relative, 1, TimeSpan.FromDays(30));
        _ = store.Finish(id, TimeSpan.FromMinutes(5), 4096);

        clock.Advance(TimeSpan.FromDays(31));

        CleanupResult result = new RecordingCleaner(store, root).Run(TimeSpan.FromDays(30));

        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(full));
        Assert.Equal("purged", Assert.Single(store.For("sitzung-1")).State);
    }

    [Fact]
    public void Was_noch_nicht_faellig_ist_bleibt_liegen()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        string root = temp.File("aufzeichnungen");
        string relative = Path.Combine("2026-09-13", "1000-abcd1234", "teil-01.mp4");
        string full = Path.Combine(root, relative);

        _ = Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[4096]);

        long id = store.Begin("sitzung-1", relative, 1, TimeSpan.FromDays(30));
        _ = store.Finish(id, TimeSpan.FromMinutes(5), 4096);

        clock.Advance(TimeSpan.FromDays(10));

        Assert.Equal(0, new RecordingCleaner(store, root).Run(TimeSpan.FromDays(30)).Deleted);
        Assert.True(File.Exists(full));
    }

    /// <summary>
    /// Der Riegel: Ein Eintrag, dessen Pfad aus der Wurzel herausführt, wird nicht gelöscht —
    /// und zwar gar nichts, auch nicht die Datei, auf die er zeigt.
    /// </summary>
    [Fact]
    public void Ein_Pfad_ausserhalb_der_Wurzel_wird_nicht_geloescht()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        string root = temp.File("aufzeichnungen");
        _ = Directory.CreateDirectory(root);

        string outside = temp.File("wichtig.txt");
        File.WriteAllText(outside, "nicht anfassen");

        long id = store.Begin("sitzung-1", Path.Combine("..", "wichtig.txt"), 1,
                              TimeSpan.FromDays(30));
        _ = store.Finish(id, TimeSpan.FromMinutes(5), 10);

        clock.Advance(TimeSpan.FromDays(31));

        CleanupResult result = new RecordingCleaner(store, root).Run(TimeSpan.FromDays(30));

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Failed);
        Assert.True(File.Exists(outside), "Die Datei ausserhalb der Wurzel wurde angetastet.");
    }

    /// <summary>
    /// Eine Datei, die jemand von Hand entfernt hat, ist kein Fehler. Sie wird vermerkt, damit
    /// der Aufräumer sie nicht bei jedem Durchlauf erneut meldet.
    /// </summary>
    [Fact]
    public void Eine_fehlende_Datei_wird_vermerkt_und_nicht_wieder_gemeldet()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        string root = temp.File("aufzeichnungen");
        _ = Directory.CreateDirectory(root);

        long id = store.Begin("sitzung-1", Path.Combine("2026-09-13", "weg.mp4"), 1,
                              TimeSpan.FromDays(30));
        _ = store.Finish(id, TimeSpan.FromMinutes(5), 4096);

        clock.Advance(TimeSpan.FromDays(31));

        RecordingCleaner cleaner = new(store, root);

        Assert.Equal(1, cleaner.Run(TimeSpan.FromDays(30)).Missing);
        Assert.False(cleaner.Run(TimeSpan.FromDays(30)).DidAnything);
    }

    /// <summary>
    /// Die Begleitdatei überlebt die Videos nicht: Sie trägt Gegenstelle und Techniker, und die
    /// sollen nicht länger daliegen als das, was sie beschreiben.
    /// </summary>
    [Fact]
    public void Mit_dem_letzten_Video_geht_auch_die_Begleitdatei()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        string root = temp.File("aufzeichnungen");
        string folder = Path.Combine(root, "2026-09-13", "1000-abcd1234");
        _ = Directory.CreateDirectory(folder);

        File.WriteAllBytes(Path.Combine(folder, "teil-01.mp4"), new byte[2048]);
        File.WriteAllText(Path.Combine(folder, RecordingPaths.ManifestName), "{}");

        long id = store.Begin("sitzung-1",
            Path.Combine("2026-09-13", "1000-abcd1234", "teil-01.mp4"), 1, TimeSpan.FromDays(30));
        _ = store.Finish(id, TimeSpan.FromMinutes(5), 2048);

        clock.Advance(TimeSpan.FromDays(31));
        _ = new RecordingCleaner(store, root).Run(TimeSpan.FromDays(30));

        Assert.False(Directory.Exists(folder),
            "Der Ordner der Sitzung steht noch - samt Begleitdatei, die Gegenstelle und "
            + "Techniker trägt.");
    }

    /// <summary>
    /// Solange noch ein Abschnitt der Sitzung liegt, bleibt die Begleitdatei. Sie gehört zu
    /// allen Abschnitten und nicht zum letzten.
    /// </summary>
    [Fact]
    public void Solange_noch_ein_Abschnitt_liegt_bleibt_die_Begleitdatei()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        string root = temp.File("aufzeichnungen");
        string folder = Path.Combine(root, "2026-09-13", "1000-abcd1234");
        _ = Directory.CreateDirectory(folder);

        File.WriteAllBytes(Path.Combine(folder, "teil-01.mp4"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(folder, "teil-02.mp4"), new byte[2048]);
        File.WriteAllText(Path.Combine(folder, RecordingPaths.ManifestName), "{}");

        string relative = Path.Combine("2026-09-13", "1000-abcd1234");

        long first = store.Begin("sitzung-1", Path.Combine(relative, "teil-01.mp4"), 1,
                                 TimeSpan.FromDays(30));
        _ = store.Finish(first, TimeSpan.FromMinutes(5), 2048);

        // Der zweite Abschnitt bekommt eine laengere Frist: Er begann spaeter.
        clock.Advance(TimeSpan.FromDays(2));
        long second = store.Begin("sitzung-1", Path.Combine(relative, "teil-02.mp4"), 2,
                                  TimeSpan.FromDays(30));
        _ = store.Finish(second, TimeSpan.FromMinutes(5), 2048);

        clock.Advance(TimeSpan.FromDays(29));

        _ = new RecordingCleaner(store, root).Run(TimeSpan.FromDays(30));

        Assert.False(File.Exists(Path.Combine(folder, "teil-01.mp4")));
        Assert.True(File.Exists(Path.Combine(folder, "teil-02.mp4")));
        Assert.True(File.Exists(Path.Combine(folder, RecordingPaths.ManifestName)));
    }

    [Fact]
    public void Ein_Durchlauf_ohne_Faelliges_sagt_das()
    {
        using TempDirectory temp = new();
        using RecordingStore store = new(temp.File("state.db"), new ManualTimeProvider(Start));

        string root = temp.File("aufzeichnungen");
        _ = Directory.CreateDirectory(root);

        CleanupResult result = new RecordingCleaner(store, root).Run(TimeSpan.FromDays(30));

        Assert.False(result.DidAnything);
        Assert.Equal("Nichts fällig.", result.Summary);
    }
}
