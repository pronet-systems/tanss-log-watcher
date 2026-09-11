using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Die Einwilligungssperre der Sprachmodell-Unterstützung.
/// </summary>
/// <remarks>
/// <para>Diese Tests bewachen eine Zusage und nicht eine Rechenregel: Ohne erteilte Einwilligung
/// darf kein Text das Haus verlassen. Ein Fehler hier fiele niemandem auf, weil er nur dazu
/// führt, dass etwas <b>funktioniert</b> — und genau deshalb steht er fest.</para>
/// <para>Geprüft wird <see cref="AiSection.IsUsable"/>, weil <c>AiGateway</c> allein daran
/// entscheidet. Wer die Bedingung dort lockert, muss hier vorbeikommen.</para>
/// </remarks>
public sealed class AiSectionTests
{
    private const string Consent = "2026-09-11 20:00:00 +02:00";

    [Fact]
    public void Voreinstellung_IstAbgeschaltetUndOhneEinwilligung()
    {
        AiSection section = new();

        Assert.False(section.Enabled);
        Assert.False(section.HasConsent);
        Assert.False(section.IsUsable);
    }

    [Fact]
    public void Voreinstellung_SchwaerztNamen()
    {
        // Die Schwaerzung ist an, solange niemand sie abschaltet - nicht umgekehrt.
        Assert.True(new AiSection().RedactIdentifiers);
    }

    [Fact]
    public void Eingeschaltet_OhneEinwilligung_BleibtGesperrt()
    {
        AiSection section = new() { Enabled = true, Model = "claude-opus-5" };

        Assert.False(section.IsUsable);
    }

    [Fact]
    public void Einwilligung_OhneEinschalten_BleibtGesperrt()
    {
        AiSection section = new() { ConsentGivenAt = Consent, Model = "claude-opus-5" };

        Assert.True(section.HasConsent);
        Assert.False(section.IsUsable);
    }

    [Fact]
    public void EingeschaltetUndEingewilligt_OhneModell_BleibtGesperrt()
    {
        AiSection section = new() { Enabled = true, ConsentGivenAt = Consent };

        Assert.False(section.IsUsable);
    }

    [Fact]
    public void AlleDreiBedingungen_GebenFrei()
    {
        AiSection section = new()
        {
            Enabled = true,
            ConsentGivenAt = Consent,
            Model = "claude-opus-5",
        };

        Assert.True(section.IsUsable);
    }

    /// <summary>
    /// Leerraum ist keine Einwilligung.
    /// </summary>
    /// <remarks>
    /// Eine Zeichenkette aus Leerzeichen entsteht leicht beim Bearbeiten von Hand. Zählte sie
    /// als erteilt, hinge die Sperre an einem Tippfehler.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void LeereEinwilligung_ZaehltNicht(string? given)
    {
        AiSection section = new() { Enabled = true, ConsentGivenAt = given, Model = "x" };

        Assert.False(section.HasConsent);
        Assert.False(section.IsUsable);
    }

    [Fact]
    public void LeeresModell_ZaehltNicht()
    {
        AiSection section = new() { Enabled = true, ConsentGivenAt = Consent, Model = "   " };

        Assert.False(section.IsUsable);
    }
}
