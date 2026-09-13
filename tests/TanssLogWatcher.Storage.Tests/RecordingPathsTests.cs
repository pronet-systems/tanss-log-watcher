using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Recordings;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Wo eine Aufzeichnung liegt, wie sie heisst — und der Riegel vor dem Löschen.
/// </summary>
public sealed class RecordingPathsTests
{
    // Mit Kind=Local gerechnet, damit der Fall auf jeder Zeitzone dasselbe erwartet: Die
    // Benennung folgt der Ortszeit, weil sie ein Mensch neben seine Kalenderwoche haelt.
    private static readonly DateTimeOffset Start =
        new(new DateTime(2026, 9, 13, 14, 30, 0, DateTimeKind.Local));

    [Fact]
    public void Der_Ordner_traegt_Tag_und_Uhrzeit()
    {
        Assert.Equal(Path.Combine("2026-09-13", "1430-a1b2c3d4"),
                     RecordingPaths.FolderFor(Start, "a1b2c3d4-9999"));
    }

    /// <summary>
    /// Eine Kennung, die nach Rechnernamen aussieht, wird gekürzt und von Sonderzeichen
    /// befreit. Wer wissen will, zu wem die Aufzeichnung gehört, sieht in der Begleitdatei
    /// nach — die wird mit der Aufzeichnung zusammen gelöscht, ein Dateipfad nicht.
    /// </summary>
    [Fact]
    public void Im_Pfad_landet_nur_ein_kurzes_Stueck_der_Kennung()
    {
        string folder = RecordingPaths.FolderFor(Start, "srv-dc01.kunde-gmbh.de");

        Assert.Equal(Path.Combine("2026-09-13", "1430-srvdc01k"), folder);
        Assert.DoesNotContain("kunde", folder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Eine_kurze_Kennung_bleibt_wie_sie_ist()
    {
        Assert.EndsWith("1430-ab12", RecordingPaths.FolderFor(Start, "ab12"),
                        StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "teil-01.mp4")]
    [InlineData(9, "teil-09.mp4")]
    [InlineData(10, "teil-10.mp4")]
    [InlineData(100, "teil-100.mp4")]
    public void Die_Abschnitte_sind_durchnummeriert(int segment, string expected)
    {
        Assert.Equal(Path.Combine("2026-09-13", "1430-a1b2c3d4", expected),
                     RecordingPaths.SegmentFor(Start, "a1b2c3d4", segment));
    }

    [Fact]
    public void Es_gibt_keinen_nullten_Abschnitt()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => RecordingPaths.SegmentFor(Start, "a1b2c3d4", 0));
    }

    [Fact]
    public void Eine_Datei_unterhalb_der_Wurzel_liegt_darin()
    {
        Assert.True(RecordingPaths.IsInside(@"C:\Aufzeichnungen",
                                            @"C:\Aufzeichnungen\2026-09-13\teil-01.mp4"));
    }

    [Fact]
    public void Ein_Pfad_mit_zwei_Punkten_fuehrt_heraus()
    {
        Assert.False(RecordingPaths.IsInside(@"C:\Aufzeichnungen",
                                             @"C:\Aufzeichnungen\..\Dokumente\wichtig.docx"));
    }

    /// <summary>
    /// Die Falle beim Vergleich mit Zeichenketten: <c>Aufzeichnungen-alt</c> beginnt mit
    /// <c>Aufzeichnungen</c>, liegt aber nicht darin. Ein Werkzeug, das Dateien löscht, darf
    /// diesen Unterschied nicht übersehen.
    /// </summary>
    [Fact]
    public void Ein_Nachbarordner_mit_gleichem_Anfang_liegt_nicht_darin()
    {
        Assert.False(RecordingPaths.IsInside(@"C:\Aufzeichnungen",
                                             @"C:\Aufzeichnungen-alt\teil-01.mp4"));
    }

    [Fact]
    public void Die_Wurzel_selbst_ist_nicht_ihr_eigener_Inhalt()
    {
        Assert.False(RecordingPaths.IsInside(@"C:\Aufzeichnungen", @"C:\Aufzeichnungen"));
    }

    [Fact]
    public void Ein_abschliessender_Trenner_aendert_nichts()
    {
        Assert.True(RecordingPaths.IsInside(@"C:\Aufzeichnungen\",
                                            @"C:\Aufzeichnungen\teil-01.mp4"));
    }

    [Fact]
    public void Ohne_eigene_Angabe_liegt_die_Wurzel_beim_uebrigen_Zustand()
    {
        string root = RecordingPaths.Root(new RecordingSection());

        Assert.StartsWith(StoragePaths.StateDirectory, root, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Aufzeichnungen", root, StringComparison.Ordinal);
    }

    [Fact]
    public void Eine_eigene_Angabe_gilt()
    {
        Assert.Equal(@"D:\Fernwartung",
                     RecordingPaths.Root(new RecordingSection { Directory = @"D:\Fernwartung" }));
    }
}
