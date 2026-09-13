using System.Text.Json;
using TanssLogWatcher.Storage.Recordings;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Die Begleitdatei — gegen echte Dateien, denn sie liegt im Zweifelsfall als echte Datei
/// neben dem Video und wird dann von einem Menschen gelesen.
/// </summary>
public sealed class RecordingManifestTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 13, 10, 0, 0, TimeSpan.FromHours(2));

    private static RecordingManifest Beispiel() => new()
    {
        SessionId = "a1b2c3d4-9999",
        Destination = "srv-dc01",
        Application = "Microsoft Remotedesktop",
        Workstation = "TECHNIK-07",
        Technician = "S. Michel",
        StartedAt = Start,
        EndedAt = Start + TimeSpan.FromMinutes(45),
        RecordedSeconds = 1200,
        Pauses = [new ManifestPause(Start + TimeSpan.FromMinutes(10),
                                    (long)TimeSpan.FromMinutes(25).TotalSeconds)],
        Files = ["teil-01.mp4", "teil-02.mp4"],
        Canvas = "1920x1080",
        FramesPerSecond = 4,
        DeleteAfter = Start + TimeSpan.FromDays(30),
        LegalBasis = "Betriebsvereinbarung",
        LegalReference = "BV Fernwartung 2026-03",
    };

    [Fact]
    public void Geschrieben_und_wieder_gelesen_steht_alles_noch_da()
    {
        using TempDirectory temp = new();
        string path = temp.File(RecordingPaths.ManifestName);

        RecordingManifest written = Beispiel();
        written.Save(path);

        RecordingManifest read = Assert.IsType<RecordingManifest>(RecordingManifest.Load(path));

        Assert.Equal(written.SessionId, read.SessionId);
        Assert.Equal(written.Destination, read.Destination);
        Assert.Equal(written.Application, read.Application);
        Assert.Equal(written.Workstation, read.Workstation);
        Assert.Equal(written.Technician, read.Technician);
        Assert.Equal(written.StartedAt, read.StartedAt);
        Assert.Equal(written.EndedAt, read.EndedAt);
        Assert.Equal(written.RecordedSeconds, read.RecordedSeconds);
        Assert.Equal(written.Pauses, read.Pauses);
        Assert.Equal(written.Files, read.Files);
        Assert.Equal(written.Canvas, read.Canvas);
        Assert.Equal(written.FramesPerSecond, read.FramesPerSecond);
        Assert.Equal(written.Codec, read.Codec);
        Assert.Equal(written.DeleteAfter, read.DeleteAfter);
        Assert.Equal(written.LegalBasis, read.LegalBasis);
        Assert.Equal(written.LegalReference, read.LegalReference);
    }

    /// <summary>
    /// Der eigentliche Zweck: Das Video ist zwanzig Minuten lang, die Sitzung dauerte
    /// fünfundvierzig. Ohne diese Datei sähe das nach einer abgebrochenen Aufzeichnung aus.
    /// </summary>
    [Fact]
    public void Sie_erklaert_die_Luecke_zwischen_Video_und_Sitzung()
    {
        using TempDirectory temp = new();
        string path = temp.File(RecordingPaths.ManifestName);

        Beispiel().Save(path);
        RecordingManifest read = Assert.IsType<RecordingManifest>(RecordingManifest.Load(path));

        TimeSpan session = read.EndedAt!.Value - read.StartedAt;
        TimeSpan video = TimeSpan.FromSeconds(read.RecordedSeconds);
        TimeSpan pauses = TimeSpan.FromSeconds(read.Pauses.Sum(p => p.Seconds));

        Assert.Equal(TimeSpan.FromMinutes(45), session);
        Assert.Equal(TimeSpan.FromMinutes(20), video);
        Assert.Equal(session, video + pauses);
    }

    /// <summary>
    /// Sie wird im Zweifel von einem Menschen gelesen: eingerückt, mit Unterstrichen, wie
    /// <c>config.json</c>.
    /// </summary>
    [Fact]
    public void Sie_ist_lesbar_geschrieben()
    {
        using TempDirectory temp = new();
        string path = temp.File(RecordingPaths.ManifestName);

        Beispiel().Save(path);
        string text = File.ReadAllText(path);

        Assert.Contains("\"recorded_seconds\": 1200", text, StringComparison.Ordinal);
        Assert.Contains("\"delete_after\"", text, StringComparison.Ordinal);
        Assert.Contains("\"legal_basis\": \"Betriebsvereinbarung\"", text,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// Die Rechtsgrundlage wandert mit. Wer die Aufzeichnung Monate später in der Hand hält,
    /// soll nicht in einer Konfigurationsdatei nachsehen müssen, worauf sie sich stützte.
    /// </summary>
    [Fact]
    public void Die_Rechtsgrundlage_liegt_bei_der_Aufzeichnung()
    {
        using TempDirectory temp = new();
        string path = temp.File(RecordingPaths.ManifestName);

        Beispiel().Save(path);
        RecordingManifest read = Assert.IsType<RecordingManifest>(RecordingManifest.Load(path));

        Assert.Equal("Betriebsvereinbarung", read.LegalBasis);
        Assert.Equal("BV Fernwartung 2026-03", read.LegalReference);
    }

    [Fact]
    public void Ein_fehlender_Zeitpunkt_des_Endes_ist_erlaubt()
    {
        using TempDirectory temp = new();
        string path = temp.File(RecordingPaths.ManifestName);

        new RecordingManifest
        {
            SessionId = "a1b2c3d4",
            StartedAt = Start,
            DeleteAfter = Start + TimeSpan.FromDays(30),
        }.Save(path);

        Assert.Null(Assert.IsType<RecordingManifest>(RecordingManifest.Load(path)).EndedAt);
    }

    [Fact]
    public void Eine_fehlende_Datei_gibt_null()
    {
        using TempDirectory temp = new();

        Assert.Null(RecordingManifest.Load(temp.File("gibt-es-nicht.json")));
    }

    /// <summary>
    /// Hausregel 5: Eine zerstörte Begleitdatei kostet die Begleitdatei, nicht die
    /// Aufzeichnung. Der Aufrufer zeigt dann weniger an — mehr nicht.
    /// </summary>
    [Fact]
    public void Eine_zerstoerte_Datei_gibt_null_und_wirft_nicht()
    {
        using TempDirectory temp = new();
        string path = temp.File(RecordingPaths.ManifestName);

        File.WriteAllText(path, "{ das ist kein JSON");

        Assert.Null(RecordingManifest.Load(path));
    }

    /// <summary>
    /// Eine Begleitdatei aus einem späteren Stand mit zusätzlichen Feldern bleibt lesbar. Sie
    /// liegt womöglich Monate neben dem Video und überlebt mehrere Versionen des Werkzeugs.
    /// </summary>
    [Fact]
    public void Ein_unbekanntes_Feld_macht_sie_nicht_unlesbar()
    {
        using TempDirectory temp = new();
        string path = temp.File(RecordingPaths.ManifestName);

        Beispiel().Save(path);

        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Dictionary<string, JsonElement> fields = document.RootElement
                .EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

            fields["kommt_erst_spaeter"] = JsonDocument.Parse("\"etwas\"").RootElement.Clone();

            File.WriteAllText(path, JsonSerializer.Serialize(fields));
        }

        Assert.Equal("a1b2c3d4-9999",
                     Assert.IsType<RecordingManifest>(RecordingManifest.Load(path)).SessionId);
    }

    /// <summary>
    /// Geschrieben wird unteilbar: Wer die Datei liest, während geschrieben wird, sieht
    /// entweder den alten oder den neuen Stand, nie einen halben.
    /// </summary>
    [Fact]
    public void Ein_zweites_Schreiben_ersetzt_den_Stand_vollstaendig()
    {
        using TempDirectory temp = new();
        string path = temp.File(RecordingPaths.ManifestName);

        Beispiel().Save(path);
        (Beispiel() with { RecordedSeconds = 60, Files = ["teil-01.mp4"] }).Save(path);

        RecordingManifest read = Assert.IsType<RecordingManifest>(RecordingManifest.Load(path));

        Assert.Equal(60, read.RecordedSeconds);
        Assert.Equal("teil-01.mp4", Assert.Single(read.Files));
    }
}
