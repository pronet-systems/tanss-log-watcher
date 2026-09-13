using System.IO;
using TanssLogWatcher.App.Services;
using TanssLogWatcher.Recording;
using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Die Torwache: Darf aufgezeichnet werden — und wenn nicht, warum nicht?
/// </summary>
public sealed class RecordingGateTests
{
    private static RecordingSection Vollstaendig() => new()
    {
        Enabled = true,
        AcknowledgedAt = "2026-09-13T10:00:00+02:00",
        AcknowledgedBy = "S. Michel",
        LegalBasis = "Betriebsvereinbarung",
        LegalReference = "BV 2026-03 Fernwartung",
    };

    [Fact]
    public void Abgeschaltet_heisst_abgeschaltet()
    {
        Assert.Contains("abgeschaltet",
            RecordingGate.WhyNotRecording(new RecordingSection()), StringComparison.Ordinal);
    }

    /// <summary>
    /// Der Grund kommt aus dem Konfigurationsabschnitt und wird hier nicht zum zweiten Mal
    /// formuliert. Liefe er auseinander, nennte das Werkzeug an zwei Stellen verschiedene
    /// Gründe für dieselbe Sache.
    /// </summary>
    [Fact]
    public void Der_Grund_aus_der_Konfiguration_wird_durchgereicht()
    {
        RecordingSection section = Vollstaendig() with { AcknowledgedAt = null };

        Assert.Equal(section.UnusableReason, RecordingGate.WhyNotRecording(section));
    }

    /// <summary>
    /// Steht die Konfiguration, entscheidet allein der Rechner — und die Antwort ist dieselbe,
    /// die <see cref="CaptureSupport"/> gibt. Deshalb wird sie hier gefragt und nicht behauptet.
    /// </summary>
    [Fact]
    public void Steht_die_Konfiguration_entscheidet_der_Rechner()
    {
        string? why = RecordingGate.WhyNotRecording(Vollstaendig());

        if (CaptureSupport.IsAvailable())
        {
            Assert.Null(why);
        }
        else
        {
            Assert.Contains("Bildschirmaufnahme", why, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Der Ordner liegt auf einem echten Datenträger, also gibt es eine echte Zahl. Geprüft
    /// wird die Grössenordnung, nicht der Wert: Der ändert sich zwischen zwei Aufrufen.
    /// </summary>
    [Fact]
    public void Der_freie_Platz_wird_gemessen_und_nicht_geraten()
    {
        long free = RecordingGate.FreeMegabytes(Path.GetTempPath());

        Assert.True(free > 0, "Auf dem temporären Datenträger sind angeblich 0 MB frei.");
        Assert.True(free < long.MaxValue,
            "Der Platz liess sich nicht feststellen — dann stünde hier die Notbremse.");
    }

    /// <summary>
    /// Ein Pfad, den es nicht gibt, ist kein Grund, die Aufzeichnung zu verweigern: Der
    /// Laufwerksbuchstabe steht trotzdem, und der Ordner wird beim ersten Schreiben angelegt.
    /// </summary>
    [Fact]
    public void Ein_noch_nicht_angelegter_Ordner_ist_kein_Hindernis()
    {
        string missing = Path.Combine(Path.GetTempPath(), "gibt-es-nicht-" + Guid.NewGuid());

        Assert.True(RecordingGate.FreeMegabytes(missing) > 0);
    }
}
