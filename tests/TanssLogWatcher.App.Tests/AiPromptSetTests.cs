using TanssLogWatcher.App.Ai;
using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Die Anweisungen an das Modell — bearbeitbar, aber nicht in jedem Teil.
/// </summary>
public sealed class AiPromptSetTests
{
    [Fact]
    public void Ohne_eigene_Angabe_gelten_die_eingebauten_Anweisungen()
    {
        AiPromptSet prompts = AiPromptSet.From(new AiSection());

        Assert.Equal(AiPrompts.DefaultRole, prompts.Role);
        Assert.Equal(AiPrompts.DefaultProofread, prompts.Proofread);
        Assert.Equal(AiPrompts.DefaultImprove, prompts.Improve);
    }

    [Fact]
    public void Eine_eigene_Rolle_wird_uebernommen()
    {
        AiPromptSet prompts = AiPromptSet.From(new AiSection
        {
            RolePrompt = "Du schreibst für ein Krankenhaus und duzt niemanden.",
        });

        Assert.StartsWith("Du schreibst für ein Krankenhaus", prompts.Role,
                          StringComparison.Ordinal);
        Assert.Equal(AiPrompts.DefaultProofread, prompts.Proofread);
    }

    /// <summary>
    /// Ein leer geräumtes Feld heisst „der eingebaute Text“ und nicht „keine Anweisung“. Sonst
    /// schaltete ein versehentlich geleertes Feld die Regeln ab, und das Ergebnis sähe aus wie
    /// ein Fehler des Modells.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ein_leeres_Feld_bedeutet_den_eingebauten_Text(string? value)
    {
        Assert.Equal(AiPrompts.DefaultRole,
                     AiPromptSet.From(new AiSection { RolePrompt = value }).Role);
    }

    /// <summary>
    /// Der Kern: Die unverhandelbaren Regeln hängen an jeder Rolle — auch an einer eigenen, die
    /// sie nicht enthält. Wer sie durch ein Textfeld entfernen könnte, könnte die Zusage des
    /// Werkzeugs durch ein Textfeld entfernen.
    /// </summary>
    [Fact]
    public void Die_festen_Regeln_haengen_auch_an_einer_eigenen_Rolle()
    {
        AiPromptSet prompts = AiPromptSet.From(new AiSection
        {
            RolePrompt = "Schreib einfach irgendwas Nettes.",
        });

        Assert.Contains("Erfinde NICHTS", prompts.System, StringComparison.Ordinal);
        Assert.Contains("Ändere keine Zahlen", prompts.System, StringComparison.Ordinal);
        Assert.Contains("ausschliesslich mit dem überarbeiteten Text", prompts.System,
                        StringComparison.Ordinal);
        Assert.Contains("Schreib einfach irgendwas Nettes.", prompts.System,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void Zu_jeder_Aufgabe_gehoert_ein_Auftrag()
    {
        AiPromptSet prompts = AiPromptSet.Default;

        Assert.Equal(AiPrompts.DefaultProofread, prompts.For(AiTask.Proofread));
        Assert.Equal(AiPrompts.DefaultImprove, prompts.For(AiTask.Improve));
    }
}
