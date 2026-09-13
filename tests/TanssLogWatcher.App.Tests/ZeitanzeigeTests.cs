using System.Runtime.Versioning;
using TanssLogWatcher.App.ViewModels;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Zeitangaben werden in Ortszeit gezeigt — gleich, aus welcher Zone sie kommen.
/// </summary>
/// <remarks>
/// <para><b>Der Befund, aus dem diese Datei entstanden ist.</b> Am 13.09.2026 gegen die echte
/// Instanz gemessen, an drei gebuchten Fernwartungen. So stand der Satz in TANSS, und so ging
/// er an den Kunden:</para>
/// <code>Zeitraum:     13.09.2026 15:52 – 13:52 (unter 1 Minute)</code>
/// <para>Zwei Stunden Differenz — und die Dauer daneben trotzdem richtig. Das war der Hinweis:
/// Die Zeitpunkte stimmten beide, sie trugen nur verschiedene Zonen. Der Beginn ist die
/// Startzeit des Prozesses und kommt in Ortszeit herein, das Ende setzt die Zustandsmaschine
/// in UTC. Nur die Anzeige verriet es.</para>
/// <para>Deshalb prüft dieser Test nicht eine Uhrzeit, sondern eine <b>Gleichheit</b>: Derselbe
/// Augenblick, in zwei Zonen ausgedrückt, muss denselben Text ergeben. Ein Test gegen „15:52“
/// wäre in einer anderen Zeitzone falsch und liefe im Sommer anders als im Winter.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ZeitanzeigeTests
{
    /// <summary>
    /// Derselbe Augenblick ergibt dieselbe Uhrzeit, gleich in welcher Zone er ankommt.
    /// </summary>
    [Fact]
    public void Derselbe_Augenblick_ergibt_dieselbe_Uhrzeit()
    {
        DateTimeOffset ortszeit = new(2026, 9, 13, 15, 52, 0, TimeSpan.FromHours(2));
        DateTimeOffset gleicherAugenblickInUtc = ortszeit.ToUniversalTime();

        // Gegenprobe, damit der Test nicht versehentlich zwei gleiche Werte vergleicht.
        Assert.NotEqual(ortszeit.Hour, gleicherAugenblickInUtc.Hour);

        Assert.Equal(Texts.Clock(ortszeit), Texts.Clock(gleicherAugenblickInUtc));
        Assert.Equal(Texts.Moment(ortszeit), Texts.Moment(gleicherAugenblickInUtc));
        Assert.Equal(Texts.Day(ortszeit), Texts.Day(gleicherAugenblickInUtc));
    }

    /// <summary>
    /// Der gemessene Fall selbst: Beginn in Ortszeit, Ende in UTC.
    /// </summary>
    /// <remarks>
    /// Nachgebaut, wie es im Betrieb entsteht — <c>DateTimeOffset.FromFileTime</c> für den
    /// Beginn, <c>GetUtcNow</c> für das Ende. Ohne die Umrechnung stünde hier 15:52 – 13:52.
    /// </remarks>
    [Fact]
    public void Beginn_in_Ortszeit_und_Ende_in_Utc_ergeben_eine_lesbare_Spanne()
    {
        DateTimeOffset beginn = new(2026, 9, 13, 15, 52, 0, TimeSpan.FromHours(2));
        DateTimeOffset ende = beginn.AddMinutes(24).ToUniversalTime();

        string gezeigt = Texts.Clock(beginn) + " – " + Texts.Clock(ende);

        // Die Spanne muss vorwaerts laufen. Das ist die eigentliche Zusage: Ein Ende vor dem
        // Beginn ist fuer jeden, der den Bericht liest, schlicht ein Fehler des Werkzeugs.
        Assert.Equal(Texts.Clock(beginn.ToLocalTime()) + " – " + Texts.Clock(ende.ToLocalTime()),
                     gezeigt);

        Assert.True(string.CompareOrdinal(Texts.Clock(ende), Texts.Clock(beginn)) > 0,
                    "Das Ende wird vor dem Beginn gezeigt: " + gezeigt);
    }

    /// <summary>
    /// Über Mitternacht kostet die fehlende Umrechnung einen ganzen Tag.
    /// </summary>
    [Fact]
    public void Kurz_nach_Mitternacht_stimmt_der_Tag()
    {
        DateTimeOffset kurzNachMitternacht = new(2026, 9, 13, 0, 30, 0, TimeSpan.FromHours(2));

        // In UTC waere das noch der 12. - in einem Kundenbericht ist das kein Schoenheitsfehler.
        Assert.Equal(Texts.Day(kurzNachMitternacht),
                     Texts.Day(kurzNachMitternacht.ToUniversalTime()));
    }
}
