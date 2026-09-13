using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Was eine frische Installation vorfindet.
/// </summary>
/// <remarks>
/// <para><b>Die Zusage lautet: Auf einem frisch aufgesetzten Rechner wird nicht geschwärzt.</b>
/// Sie gilt für jeden Weg, auf dem eine erste <c>config.json</c> entsteht — die Vorgabe im
/// Quelltext, die mitgelieferte Vorlage und den Einrichtungsassistenten. Drei Wege heissen drei
/// Gelegenheiten, an denen die Zusage still auseinanderläuft; genau deshalb stehen sie hier
/// nebeneinander und nicht verstreut.</para>
///
/// <para>Der Verlauf hängt mit daran: Seine eigene Einstellung ist voreingestellt
/// <see langword="null"/> und folgt damit dem Protokollabschnitt. Eine frische Installation, die
/// im Protokoll nicht schwärzt, aber im Verlauf doch, wäre für niemanden erklärbar.</para>
/// </remarks>
public sealed class FrischeInstallationTests
{
    [Fact]
    public void Die_Vorgabe_im_Quelltext_schwaerzt_nicht()
    {
        Assert.False(new LoggingSection().RedactWindowTitles);
    }

    /// <summary>
    /// Der Weg des Einrichtungsassistenten.
    /// </summary>
    /// <remarks>
    /// Er baut bei einer frischen Einrichtung genau dieses Stück — nur der TANSS-Abschnitt wird
    /// gesetzt, alles andere kommt aus den Vorgaben (<c>SetupViewModel.Save</c>). Ein Tag, an dem
    /// jemand dort einen Protokollabschnitt mit eigenen Werten einsetzt, lässt diesen Fall
    /// durchfallen — und das ist der Zweck.
    /// </remarks>
    [Fact]
    public void Der_Einrichtungsassistent_schwaerzt_nicht()
    {
        AppConfig frisch = new()
        {
            Tanss = new TanssSection { BaseUrl = "https://tanss.kunde.de/backend", EmployeeId = 1 },
        };

        Assert.False(frisch.Logging.RedactWindowTitles);
    }

    /// <summary>
    /// Der Verlauf folgt dem Protokoll, solange niemand etwas anderes einträgt.
    /// </summary>
    /// <remarks>
    /// Die Gegenstelle im Verlauf ist gemessen derselbe Text, den
    /// <see cref="LoggingSection.RedactWindowTitles"/> verbirgt. Eine eigene Vorgabe hier wäre
    /// eine zweite Entscheidung über dieselbe Sache — und die stille Umkehr einer Entscheidung,
    /// die der Benutzer schon getroffen hat.
    /// </remarks>
    [Fact]
    public void Der_Verlauf_folgt_dem_Protokoll()
    {
        HistorySection frisch = new();
        Assert.Null(frisch.RedactDestination);

        Assert.False(frisch.ShouldRedactDestination(new LoggingSection()));

        // Und in die andere Richtung: Wer im Protokoll schwaerzt, schwaerzt auch im Verlauf.
        Assert.True(frisch.ShouldRedactDestination(
            new LoggingSection { RedactWindowTitles = true }));
    }

    /// <summary>
    /// Eine bestehende Konfiguration, die den Schalter ausdrücklich setzt, bleibt unangetastet.
    /// </summary>
    /// <remarks>
    /// Der Fall, der bei einem Vorgabewechsel am leichtesten übersehen wird: Wer die Schwärzung
    /// eingeschaltet hat, muss sie behalten. Eine Vorgabe, die eine ausdrückliche Entscheidung
    /// überstimmt, ist die unangenehmste Art von Rückschritt — sie fällt erst auf, wenn die
    /// Daten schon im Klartext liegen.
    /// </remarks>
    [Fact]
    public void Eine_ausdrueckliche_Entscheidung_ueberlebt_den_Vorgabewechsel()
    {
        AppConfig gesetzt = new()
        {
            Tanss = new TanssSection { BaseUrl = "https://tanss.kunde.de/backend", EmployeeId = 1 },
            Logging = new LoggingSection { RedactWindowTitles = true },
        };

        Assert.True(gesetzt.Logging.RedactWindowTitles);
        Assert.True(gesetzt.History.ShouldRedactDestination(gesetzt.Logging));
    }
}
