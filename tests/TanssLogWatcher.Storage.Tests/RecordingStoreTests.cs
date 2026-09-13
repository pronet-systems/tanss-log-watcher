using TanssLogWatcher.Storage.Recordings;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Die Buchführung über Aufzeichnungen — und die Löschfrist, die ein Versprechen ist.
/// </summary>
public sealed class RecordingStoreTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Eine_beginnende_Aufzeichnung_steht_sofort_in_der_Buchfuehrung()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        long id = store.Begin("sitzung-1", @"2026\2026-09-13\teil-1.mp4", 1,
                              TimeSpan.FromDays(30));

        Assert.True(id > 0);

        RecordingEntry entry = Assert.Single(store.For("sitzung-1"));

        Assert.Equal("recording", entry.State);
        Assert.Equal(Start, entry.StartedAt);
        Assert.Equal(Start + TimeSpan.FromDays(30), entry.DeleteAfter);
        Assert.Null(entry.EndedAt);
    }

    /// <summary>
    /// Die aufgezeichnete Zeit ist nicht die Differenz aus Beginn und Ende. Wer beides
    /// vergleicht, sieht, wie viel der Sitzung aus Pausen bestand.
    /// </summary>
    [Fact]
    public void Die_aufgezeichnete_Zeit_ist_nicht_die_Dauer_der_Sitzung()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        long id = store.Begin("sitzung-1", "teil-1.mp4", 1, TimeSpan.FromDays(30));

        clock.Advance(TimeSpan.FromMinutes(45));
        Assert.True(store.Finish(id, TimeSpan.FromMinutes(20), bytes: 12_345_678));

        RecordingEntry entry = Assert.Single(store.For("sitzung-1"));

        Assert.Equal("kept", entry.State);
        Assert.Equal(TimeSpan.FromMinutes(45), entry.EndedAt!.Value - entry.StartedAt);
        Assert.Equal(TimeSpan.FromMinutes(20), entry.Recorded);
        Assert.Equal(12_345_678, entry.Bytes);
    }

    [Fact]
    public void Vor_Ablauf_der_Frist_ist_nichts_faellig()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        long id = store.Begin("sitzung-1", "teil-1.mp4", 1, TimeSpan.FromDays(30));
        _ = store.Finish(id, TimeSpan.FromMinutes(5), 1000);

        clock.Advance(TimeSpan.FromDays(29));

        Assert.Empty(store.Due(TimeSpan.FromDays(30)));
    }

    [Fact]
    public void Nach_Ablauf_der_Frist_wird_sie_faellig()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        long id = store.Begin("sitzung-1", "teil-1.mp4", 1, TimeSpan.FromDays(30));
        _ = store.Finish(id, TimeSpan.FromMinutes(5), 1000);

        clock.Advance(TimeSpan.FromDays(31));

        Assert.Equal(id, Assert.Single(store.Due(TimeSpan.FromDays(30))).Id);
    }

    /// <summary>
    /// Eine verkürzte Frist wirkt sofort auf bestehende Aufzeichnungen. Das darf sie: Ein
    /// Versprechen enger zu fassen ist jederzeit zulässig.
    /// </summary>
    [Fact]
    public void Eine_verkuerzte_Frist_wirkt_auf_bestehende_Aufzeichnungen()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        long id = store.Begin("sitzung-1", "teil-1.mp4", 1, TimeSpan.FromDays(30));
        _ = store.Finish(id, TimeSpan.FromMinutes(5), 1000);

        clock.Advance(TimeSpan.FromDays(10));

        // Jemand stellt die Frist auf sieben Tage herunter: Die zehn Tage alte Aufzeichnung
        // ist damit sofort faellig.
        Assert.Equal(id, Assert.Single(store.Due(TimeSpan.FromDays(7))).Id);
    }

    /// <summary>
    /// Eine verlängerte Frist wirkt <b>nicht</b> auf bestehende Aufzeichnungen. Ein gegebenes
    /// Versprechen wird nicht nachträglich gedehnt.
    /// </summary>
    [Fact]
    public void Eine_verlaengerte_Frist_dehnt_ein_gegebenes_Versprechen_nicht()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        long id = store.Begin("sitzung-1", "teil-1.mp4", 1, TimeSpan.FromDays(7));
        _ = store.Finish(id, TimeSpan.FromMinutes(5), 1000);

        clock.Advance(TimeSpan.FromDays(8));

        // Die Einstellung steht jetzt auf ein Jahr - die Zusage von sieben Tagen gilt trotzdem.
        Assert.Equal(id, Assert.Single(store.Due(TimeSpan.FromDays(365))).Id);
    }

    /// <summary>
    /// Die Zeile bleibt als Grabstein stehen. Für eine Auskunft nach Art. 15 DSGVO ist zu
    /// sagen, was es gab und wann es verschwand — eine gelöschte Zeile könnte das nicht.
    /// </summary>
    [Fact]
    public void Eine_geloeschte_Aufzeichnung_hinterlaesst_einen_Grabstein()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        long id = store.Begin("sitzung-1", "teil-1.mp4", 1, TimeSpan.FromDays(30));
        _ = store.Finish(id, TimeSpan.FromMinutes(5), 1000);

        clock.Advance(TimeSpan.FromDays(31));
        Assert.True(store.MarkPurged(id, "Aufbewahrungsfrist von 30 Tagen abgelaufen."));

        RecordingEntry entry = Assert.Single(store.For("sitzung-1"));

        Assert.Equal("purged", entry.State);
        Assert.False(entry.Exists);
        Assert.Empty(store.Due(TimeSpan.FromDays(30)));
        Assert.Equal(1, store.Count("purged"));
    }

    /// <summary>
    /// Eine Datei, die jemand von Hand entfernt hat, ist kein Fehler — aber sie gehört
    /// festgehalten, sonst meldete der Aufräumer sie bei jedem Durchlauf erneut.
    /// </summary>
    [Fact]
    public void Eine_verschwundene_Datei_wird_als_solche_vermerkt()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        long id = store.Begin("sitzung-1", "teil-1.mp4", 1, TimeSpan.FromDays(30));
        _ = store.Finish(id, TimeSpan.FromMinutes(5), 1000);

        clock.Advance(TimeSpan.FromDays(31));
        Assert.True(store.MarkMissing(id, "Die Datei lag beim Aufräumen nicht mehr dort."));

        Assert.Equal("missing", Assert.Single(store.For("sitzung-1")).State);
        Assert.Empty(store.Due(TimeSpan.FromDays(30)));
    }

    [Fact]
    public void Eine_Sitzung_kann_mehrere_Abschnitte_haben()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        _ = store.Begin("sitzung-1", "teil-1.mp4", 1, TimeSpan.FromDays(30));
        _ = store.Begin("sitzung-1", "teil-2.mp4", 2, TimeSpan.FromDays(30));
        _ = store.Begin("sitzung-2", "andere-1.mp4", 1, TimeSpan.FromDays(30));

        Assert.Equal([1, 2], store.For("sitzung-1").Select(e => e.Segment));
        Assert.Single(store.For("sitzung-2"));
    }

    [Fact]
    public void Ein_zweites_Abschliessen_tut_nichts()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        long id = store.Begin("sitzung-1", "teil-1.mp4", 1, TimeSpan.FromDays(30));

        Assert.True(store.Finish(id, TimeSpan.FromMinutes(5), 1000));
        Assert.False(store.Finish(id, TimeSpan.FromMinutes(99), 9999));

        Assert.Equal(TimeSpan.FromMinutes(5), Assert.Single(store.For("sitzung-1")).Recorded);
    }

    /// <summary>
    /// Eine Aufzeichnung, die nie abgeschlossen wurde — Absturz mitten im Schreiben —, wird
    /// trotzdem fällig. Sonst bliebe sie für immer liegen.
    /// </summary>
    [Fact]
    public void Eine_nie_abgeschlossene_Aufzeichnung_wird_trotzdem_faellig()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using RecordingStore store = new(temp.File("state.db"), clock);

        long id = store.Begin("sitzung-1", "teil-1.mp4", 1, TimeSpan.FromDays(30));

        clock.Advance(TimeSpan.FromDays(31));

        Assert.Equal(id, Assert.Single(store.Due(TimeSpan.FromDays(30))).Id);
    }
}
