using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.ViewModels;
using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Die Fußzeile: Zustandsplakette, Warnungen und der Hinweis auf eine neue Fassung.
/// </summary>
/// <remarks>
/// <para><b>Der Befund, aus dem diese Datei entstanden ist.</b> Die Plakette sagte „verbunden,
/// mit Warnung“, und es gab in der ganzen Oberfläche keinen Ort, an dem stand, welche. Die
/// Warnungen wurden erhoben, in den Zustand gelegt und nie gezeigt.</para>
///
/// <para>Derselbe Mangel beim Aktualisieren: Geprüft wurde eine Minute nach dem Start und
/// danach täglich — gesagt wurde es nur auf der Seite „Verbindung“. Die öffnet im Betrieb
/// niemand, das Werkzeug läuft mit geschlossenem Fenster.</para>
///
/// <para><b>Was hier NICHT geprüft wird:</b> ob GitHub tatsächlich antwortet. Das täte ein
/// Netzaufruf in einem Prüfstand, und er wäre an dem Tag rot, an dem GitHub hustet. Geprüft
/// wird, was dieses Haus verantwortet — dass aus einem Befund die richtige Zeile wird.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class FusszeileTests
{
    /// <summary>
    /// Ohne neue Fassung steht in der Fußzeile nichts dazu.
    /// </summary>
    /// <remarks>
    /// Der wichtigere der beiden Fälle: Eine Dauerzeile, die immer da ist, liest nach drei
    /// Tagen niemand mehr — und dann fällt auch die echte Meldung nicht mehr auf.
    /// </remarks>
    [Fact]
    public void Ohne_neue_Fassung_steht_nichts_da()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp);
        using ShellViewModel model = new(host);

        Assert.False(model.UpdateAvailable);
    }

    /// <summary>
    /// Liegt eine neue Fassung vor, nennt die Zeile ihre Nummer.
    /// </summary>
    [Fact]
    public void Mit_neuer_Fassung_nennt_die_Zeile_die_Nummer()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp);
        using ShellViewModel model = new(host);

        model.UpdateAvailable = true;
        model.UpdateVersion = "0.3.1";

        Assert.Contains("0.3.1", model.UpdateText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ohne bekannte Nummer bleibt die Zeile trotzdem wahr.
    /// </summary>
    /// <remarks>
    /// Eine erfundene Nummer wäre hier das Naheliegende und das Falsche. Steht keine fest,
    /// sagt die Zeile nur, dass es etwas gibt — und der Klick führt dorthin, wo es steht.
    /// </remarks>
    [Fact]
    public void Ohne_Nummer_wird_keine_erfunden()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp);
        using ShellViewModel model = new(host);

        model.UpdateAvailable = true;

        Assert.Equal("Aktualisierung verfügbar", model.UpdateText);
    }

    /// <summary>
    /// Die Auskunft zur Plakette lässt sich auf- und wieder zuklappen.
    /// </summary>
    /// <remarks>
    /// Ein Umschalter und nicht bloss ein Aufklappen: Wer sie selbst geöffnet hat, soll sie an
    /// derselben Stelle wieder loswerden und nicht danebentreffen müssen.
    /// </remarks>
    [Fact]
    public void Die_Auskunft_klappt_auf_und_wieder_zu()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp);
        using ShellViewModel model = new(host);

        Assert.False(model.IsWarningsOpen);

        model.ToggleWarningsCommand.Execute(null);
        Assert.True(model.IsWarningsOpen);

        model.ToggleWarningsCommand.Execute(null);
        Assert.False(model.IsWarningsOpen);
    }

    /// <summary>
    /// Ohne Warnung geht der Klick nicht ins Leere — die Auskunft sagt dann genau das.
    /// </summary>
    /// <remarks>
    /// Ein Klick, der nichts tut, sieht aus wie ein Fehler des Werkzeugs.
    /// </remarks>
    [Fact]
    public void Ohne_Warnung_sagt_die_Auskunft_genau_das()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp);
        using ShellViewModel model = new(host);

        Assert.False(model.HasWarnings);
        Assert.Empty(model.Warnings);
        Assert.NotEmpty(model.NoWarningText);
    }

    /// <summary>
    /// Der stillgelegte Zeittakt erzeugt keine Warnung mehr.
    /// </summary>
    /// <remarks>
    /// <para>Er tat es einmal, und das war der Fehler: Die Plakette stand dauerhaft auf
    /// „verbunden, mit Warnung“ für eine Einstellung, zu der es nichts zu tun gab. Eine
    /// Warnung, auf die keine Handlung folgt, stumpft alle übrigen ab.</para>
    /// <para>Der Schlüssel selbst ist aus dem Schema verschwunden und wird beim Laden
    /// stillschweigend geräumt — das prüft <c>StillgelegteSchluesselTests</c>. Hier steht nur,
    /// dass die Plakette darüber schweigt.</para>
    /// </remarks>
    [Fact]
    public void Der_alte_Zeittakt_erzeugt_keine_Warnung()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp);
        using ShellViewModel model = new(host);

        Assert.DoesNotContain(model.Warnings,
            w => w.Title.Contains("Abschnittslänge", StringComparison.Ordinal));
    }

    private static AppHost Laufzeit(TempDirectory temp)
    {
        ConfigStore store = new(temp.File("config.json"));
        store.Save(Sample.Config());

        AppHost host = new(store, NullLoggerFactory.Instance, new RuntimeNotifier(null),
                           TimeProvider.System, temp.File("state.db"));

        Assert.True(host.Reload(), "Die Konfiguration liess sich nicht laden.");
        return host;
    }
}
