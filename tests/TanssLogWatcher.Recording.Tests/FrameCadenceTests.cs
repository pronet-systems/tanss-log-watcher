using Xunit;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Die Bildrate, von Hand getaktet — weil die Drossel von Windows auf Build 22631 wirft.
/// </summary>
public sealed class FrameCadenceTests
{
    private static FrameCadence Vier() => new(framesPerSecond: 4, heartbeat: TimeSpan.FromSeconds(2));

    [Fact]
    public void Das_erste_Bild_geht_immer_durch()
    {
        Assert.Equal(FrameReason.First, Vier().Decide(TimeSpan.Zero, changed: false));
    }

    /// <summary>
    /// Gemessen wurden sechzig Bilder je Sekunde an einem Fenster, das sich alle sechzehn
    /// Millisekunden neu zeichnete. Ohne Drossel landete das alles in der Datei.
    /// </summary>
    [Fact]
    public void Aenderungen_unterhalb_des_Mindestabstands_werden_verworfen()
    {
        FrameCadence cadence = Vier();
        cadence.Accepted(TimeSpan.Zero);

        Assert.Equal(FrameReason.Skip,
            cadence.Decide(TimeSpan.FromMilliseconds(16), changed: true));
        Assert.Equal(FrameReason.Skip,
            cadence.Decide(TimeSpan.FromMilliseconds(249), changed: true));
        Assert.Equal(FrameReason.Changed,
            cadence.Decide(TimeSpan.FromMilliseconds(250), changed: true));
    }

    /// <summary>
    /// Ein stehender Bildschirm liefert keine Bilder. Ohne Herzschlag hätte die Datei dort eine
    /// Lücke, und der Abspieler spränge über eine Viertelstunde Lesezeit hinweg, als wäre sie
    /// nicht gewesen.
    /// </summary>
    [Fact]
    public void Ohne_Aenderung_kommt_nach_dem_Herzschlag_trotzdem_ein_Bild()
    {
        FrameCadence cadence = Vier();
        cadence.Accepted(TimeSpan.Zero);

        Assert.Equal(FrameReason.Skip,
            cadence.Decide(TimeSpan.FromSeconds(1.9), changed: false));
        Assert.Equal(FrameReason.Heartbeat,
            cadence.Decide(TimeSpan.FromSeconds(2), changed: false));
    }

    /// <summary>
    /// Der Herzschlag richtet sich nach der Aufzeichnungsuhr und nicht nach der Wanduhr. Steht
    /// sie während einer Pause, wird auch kein Herzschlag fällig — sonst hinterliesse die Pause
    /// Bilder und wäre in der Datei keine.
    /// </summary>
    [Fact]
    public void Waehrend_der_Pause_wird_kein_Herzschlag_faellig()
    {
        FrameCadence cadence = Vier();
        cadence.Accepted(TimeSpan.FromSeconds(10));

        // Die Aufzeichnungsuhr steht bei 10 Sekunden, egal wie lange die Pause dauert.
        Assert.Equal(FrameReason.Skip, cadence.Decide(TimeSpan.FromSeconds(10), changed: false));
    }

    [Fact]
    public void Nach_einer_Pause_geht_das_naechste_Bild_sofort_durch()
    {
        FrameCadence cadence = Vier();
        cadence.Accepted(TimeSpan.FromSeconds(10));
        cadence.Reset();

        Assert.Equal(FrameReason.First,
            cadence.Decide(TimeSpan.FromSeconds(10), changed: false));
    }

    [Fact]
    public void Der_Mindestabstand_folgt_der_Bildrate()
    {
        Assert.Equal(TimeSpan.FromSeconds(0.25),
            new FrameCadence(4, TimeSpan.FromSeconds(2)).MinimumGap);
        Assert.Equal(TimeSpan.FromSeconds(1),
            new FrameCadence(1, TimeSpan.FromSeconds(2)).MinimumGap);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Eine_unbrauchbare_Bildrate_wird_abgelehnt(int framesPerSecond)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new FrameCadence(framesPerSecond, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Ein_Herzschlag_von_null_wird_abgelehnt()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new FrameCadence(4, TimeSpan.Zero));
    }
}
