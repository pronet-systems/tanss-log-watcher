using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Der Abschnitt zur Bildschirmaufzeichnung — und vor allem die eine Frage, die er beantwortet.
/// </summary>
public sealed class RecordingSectionTests
{
    private static RecordingSection Vollstaendig() => new()
    {
        Enabled = true,
        AcknowledgedAt = "2026-09-13 10:00:00 +02:00",
        AcknowledgedBy = "Sebastian Michel",
        LegalBasis = "Betriebsvereinbarung",
        LegalReference = "BV 2026-03 Fernwartung",
    };

    [Fact]
    public void Ohne_Einschalten_wird_nicht_aufgezeichnet()
    {
        Assert.False((Vollstaendig() with { Enabled = false }).IsUsable);
    }

    [Fact]
    public void Ohne_Kenntnisnahme_wird_nicht_aufgezeichnet()
    {
        Assert.False((Vollstaendig() with { AcknowledgedAt = null }).IsUsable);
    }

    /// <summary>
    /// Eine Kenntnisnahme ohne benannte Grundlage ist ein Haken und kein Nachweis. Sie reicht
    /// deshalb nicht.
    /// </summary>
    [Theory]
    [InlineData(null, "BV 2026-03")]
    [InlineData("Betriebsvereinbarung", null)]
    [InlineData("", "BV 2026-03")]
    [InlineData("   ", "BV 2026-03")]
    public void Ohne_benannte_Grundlage_wird_nicht_aufgezeichnet(string? basis, string? reference)
    {
        RecordingSection section = Vollstaendig() with
        {
            LegalBasis = basis,
            LegalReference = reference,
        };

        Assert.False(section.IsUsable);
    }

    [Fact]
    public void Mit_allen_vier_Angaben_darf_aufgezeichnet_werden()
    {
        Assert.True(Vollstaendig().IsUsable);
        Assert.True(Vollstaendig().HasAcknowledgement);
    }

    /// <summary>
    /// Der Grund steht neben der Entscheidung. Ein Werkzeug, das an einer Stelle entscheidet
    /// und an einer anderen begründet, begründet irgendwann etwas anderes, als es entschieden
    /// hat.
    /// </summary>
    [Theory]
    [InlineData(false, true, true, "abgeschaltet")]
    [InlineData(true, false, true, "Kenntnisnahme")]
    [InlineData(true, true, false, "Rechtsgrundlage")]
    public void Zu_jedem_Nein_gehoert_ein_Satz(bool enabled, bool acknowledged, bool legal,
                                               string expected)
    {
        RecordingSection section = Vollstaendig() with
        {
            Enabled = enabled,
            AcknowledgedAt = acknowledged ? "2026-09-13 10:00:00 +02:00" : null,
            LegalBasis = legal ? "Betriebsvereinbarung" : null,
        };

        Assert.False(section.IsUsable);
        Assert.Contains(expected, section.UnusableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Zu_einem_Ja_gehoert_kein_Satz()
    {
        Assert.Null(Vollstaendig().UnusableReason);
    }

    /// <summary>
    /// Entscheidung und Begründung dürfen nicht auseinanderlaufen: Genau dann, wenn es einen
    /// Grund gibt, wird nicht aufgezeichnet.
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Grund_und_Entscheidung_sagen_dasselbe(bool enabled, bool acknowledged, bool legal)
    {
        RecordingSection section = Vollstaendig() with
        {
            Enabled = enabled,
            AcknowledgedAt = acknowledged ? "2026-09-13 10:00:00 +02:00" : null,
            LegalBasis = legal ? "Betriebsvereinbarung" : null,
            LegalReference = legal ? "BV 2026-03 Fernwartung" : null,
        };

        Assert.Equal(section.IsUsable, section.UnusableReason is null);
    }

    [Fact]
    public void Die_Voreinstellung_zeichnet_nicht_auf()
    {
        RecordingSection section = new();

        Assert.False(section.Enabled);
        Assert.False(section.IsUsable);
        Assert.False(section.HasAcknowledgement);
        Assert.Equal(RecordingSection.DefaultRetentionDays, section.RetentionDays);
    }

    [Fact]
    public void Eine_halb_ausgefuellte_Kenntnisnahme_wird_beanstandet()
    {
        AppConfig config = Sample.Config() with
        {
            Recording = new RecordingSection { AcknowledgedAt = "2026-09-13 10:00:00 +02:00" },
        };

        Assert.Contains(ConfigValidator.Collect(config),
            problem => problem.Contains("Kenntnisnahme ist unvollständig",
                                        StringComparison.Ordinal));
    }

    /// <summary>
    /// Geprüft wird auch, was abgeschaltet ist: Ein unsinniger Wert in einem ruhenden Abschnitt
    /// fiele sonst erst an dem Tag auf, an dem jemand die Aufzeichnung einschaltet.
    /// </summary>
    [Theory]
    [InlineData(0, "recording.retention_days")]
    [InlineData(4000, "recording.retention_days")]
    public void Eine_unsinnige_Loeschfrist_wird_auch_im_abgeschalteten_Zustand_beanstandet(
        int days, string field)
    {
        AppConfig config = Sample.Config() with
        {
            Recording = new RecordingSection { Enabled = false, RetentionDays = days },
        };

        Assert.Contains(ConfigValidator.Collect(config),
            problem => problem.StartsWith(field, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void Eine_unsinnige_Bildrate_wird_beanstandet(int fps)
    {
        AppConfig config = Sample.Config() with
        {
            Recording = new RecordingSection { FramesPerSecond = fps },
        };

        Assert.Contains(ConfigValidator.Collect(config),
            problem => problem.StartsWith("recording.frames_per_second", StringComparison.Ordinal));
    }

    /// <summary>
    /// Ein relativer Pfad zeigte je nach Startverzeichnis woandershin — und gelöscht wird
    /// später ausschliesslich unterhalb dieses Ordners.
    /// </summary>
    [Fact]
    public void Ein_relativer_Zielordner_wird_beanstandet()
    {
        AppConfig config = Sample.Config() with
        {
            Recording = new RecordingSection { Directory = "aufzeichnungen" },
        };

        Assert.Contains(ConfigValidator.Collect(config),
            problem => problem.StartsWith("recording.directory", StringComparison.Ordinal));
    }

    [Fact]
    public void Ein_vollstaendiger_Zielordner_geht_durch()
    {
        AppConfig config = Sample.Config() with
        {
            Recording = new RecordingSection { Directory = @"D:\Fernwartung\Aufzeichnungen" },
        };

        Assert.DoesNotContain(ConfigValidator.Collect(config),
            problem => problem.StartsWith("recording.", StringComparison.Ordinal));
    }

    [Fact]
    public void Die_Voreinstellung_besteht_die_Pruefung()
    {
        AppConfig config = Sample.Config() with { Recording = new RecordingSection() };

        Assert.DoesNotContain(ConfigValidator.Collect(config),
            problem => problem.StartsWith("recording.", StringComparison.Ordinal));
    }
}
