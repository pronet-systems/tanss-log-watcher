using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.ViewModels;
using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Die Anbieterauswahl im Fenster zur Sprachmodell-Unterstützung.
/// </summary>
/// <remarks>
/// <para><b>Der Befund.</b> Beim Öffnen des Fensters war der eingestellte Anbieter nicht
/// ausgewählt.</para>
///
/// <para><b>Die Ursache steckte in der Bindung.</b> Beide Auswahlknöpfe derselben Gruppe hingen
/// an <c>IsOpenAi</c>, einer davon über einen invertierenden Umsetzer. Wählt WPF einen Knopf
/// einer Gruppe ab, setzt es dessen <c>IsChecked</c> selbst auf <see langword="false"/> — und
/// weil <c>IsChecked</c> in beide Richtungen bindet, läuft dieser Wert durch den Umsetzer
/// zurück in die Quelle. Welcher Knopf zuerst gebunden wird, entscheidet dann über das
/// Ergebnis.</para>
///
/// <para><b>Was hier geprüft wird — und was nicht.</b> Geprüft wird das Ansichtsmodell: dass
/// der Anbieter aus der Konfiguration ankommt und dass ein Abwählen den Zustand nicht umwirft.
/// Genau daran hing der Fehler. <b>Nicht</b> geprüft wird das Fenster selbst; dafür bräuchte es
/// eine laufende Oberfläche, und ein Prüfstand, der ein Fenster öffnet, prüft am Ende WPF.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AnbieterauswahlTests
{
    /// <summary>
    /// Steht Anthropic in der Konfiguration, ist Anthropic gewählt.
    /// </summary>
    [Fact]
    public void Anthropic_aus_der_Konfiguration_kommt_an()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, "anthropic");
        AiSettingsViewModel model = new(host);

        Assert.True(model.IsAnthropic);
        Assert.False(model.IsOpenAi);
        Assert.Equal("Anthropic (Claude)", model.ProviderName);
    }

    /// <summary>
    /// Steht OpenAI in der Konfiguration, ist OpenAI gewählt.
    /// </summary>
    [Fact]
    public void OpenAi_aus_der_Konfiguration_kommt_an()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, "openai");
        AiSettingsViewModel model = new(host);

        Assert.True(model.IsOpenAi);
        Assert.False(model.IsAnthropic);
        Assert.Equal("OpenAI", model.ProviderName);
    }

    /// <summary>
    /// Die Schreibweise entscheidet nicht mit.
    /// </summary>
    /// <remarks>
    /// In der Konfigurationsdatei steht, was jemand hineingeschrieben hat. „OpenAI“ und
    /// „openai“ meinen dasselbe.
    /// </remarks>
    [Theory]
    [InlineData("OpenAI")]
    [InlineData("openai")]
    [InlineData("OPENAI")]
    public void Die_Schreibweise_des_Anbieters_ist_gleichgueltig(string geschrieben)
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, geschrieben);
        AiSettingsViewModel model = new(host);

        Assert.True(model.IsOpenAi);
    }

    /// <summary>
    /// <b>Der Fall, an dem es hing:</b> Ein Abwählen wirft den Zustand nicht um.
    /// </summary>
    /// <remarks>
    /// <para>Genau das tut die Gruppenlogik von WPF, sobald der andere Knopf angewählt wird:
    /// Sie schreibt <see langword="false"/> in den abgewählten. Käme das in der Quelle an,
    /// stünde dort am Ende der Anbieter, der gerade NICHT gewählt wurde — oder gar keiner.</para>
    /// <para>Der Setzer handelt deshalb nur auf <see langword="true"/>. Gesetzt wird allein
    /// durch das Anwählen.</para>
    /// </remarks>
    [Fact]
    public void Ein_Abwaehlen_wirft_den_Zustand_nicht_um()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, "openai");
        AiSettingsViewModel model = new(host);

        Assert.True(model.IsOpenAi);

        // So schreibt die Gruppenlogik, wenn sie den Anthropic-Knopf abwaehlt.
        model.IsAnthropic = false;

        Assert.True(model.IsOpenAi);
        Assert.False(model.IsAnthropic);
    }

    /// <summary>
    /// Anwählen wechselt den Anbieter — in beide Richtungen.
    /// </summary>
    [Fact]
    public void Anwaehlen_wechselt_den_Anbieter()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, "anthropic");
        AiSettingsViewModel model = new(host);

        model.IsOpenAi = true;
        Assert.False(model.IsAnthropic);
        Assert.Equal("OpenAI", model.ProviderName);

        model.IsAnthropic = true;
        Assert.False(model.IsOpenAi);
        Assert.Equal("Anthropic (Claude)", model.ProviderName);
    }

    /// <summary>
    /// Ohne Angabe gilt Anthropic — und zwar sichtbar, nicht als leere Auswahl.
    /// </summary>
    [Fact]
    public void Ohne_Angabe_ist_Anthropic_gewaehlt()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, provider: null);
        AiSettingsViewModel model = new(host);

        Assert.True(model.IsAnthropic);
        Assert.False(model.IsOpenAi);
    }

    private static AppHost Laufzeit(TempDirectory temp, string? provider)
    {
        ConfigStore store = new(temp.File("config.json"));

        AppConfig basis = Sample.Config();
        store.Save(basis with
        {
            Ai = new AiSection
            {
                Provider = provider ?? string.Empty,
                Model = "ein-modell",
            },
        });

        AppHost host = new(store, NullLoggerFactory.Instance, new RuntimeNotifier(null),
                           TimeProvider.System, temp.File("state.db"));

        Assert.True(host.Reload(), "Die Konfiguration liess sich nicht laden.");
        return host;
    }
}
