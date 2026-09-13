using TanssLogWatcher.Monitoring.Model;
using Xunit;

namespace TanssLogWatcher.Monitoring.Tests;

/// <summary>
/// Die Kennung, unter der TANSS das Gerät wiedererkennt.
/// </summary>
/// <remarks>
/// <para>Gemessen am 13.09.2026 gegen eine echte Instanz der Fassung 10.10.0: Mit hinterlegter
/// Zuordnung kam die Firma an der angelegten Fernwartung zurück, ohne Zuordnung eine 0. Der
/// Nutzen dieser Kennung ist damit belegt — und ebenso, dass eine falsche teuer ist: Sie
/// bucht ab dann stillschweigend auf einen Kunden.</para>
///
/// <para>Diese Fälle bewachen den Riegel, nicht die Bildung. Die Bildung ist trivial; der
/// Riegel ist das Eigentliche.</para>
/// </remarks>
public sealed class DeviceIdentityTests
{
    [Theory]
    [InlineData("mstsc", "srv-kunde01")]
    [InlineData("AnyDesk", "123 456 789")]
    [InlineData("TeamViewer", "987654321")]
    [InlineData("putty", "10.0.0.5")]
    [InlineData("WinSCP", "backup.kunde.local")]
    public void WoDerBezeichnerEinGeraetBenenntEntstehtEineKennung(string profil,
                                                                           string bezeichner)
    {
        Assert.Equal(bezeichner, DeviceIdentity.From(profil, bezeichner));
    }

    /// <summary>
    /// Profile, deren Ziel kein Gerät ist.
    /// </summary>
    /// <remarks>
    /// Bei <c>cmd</c>, <c>powershell</c> und <c>RoyalTS</c> ist das Ziel der <b>ganze</b>
    /// Fenstertitel; bei den Outlook-Profilen die Nachrichtenbetreffzeile; bei <c>devenv</c>
    /// und <c>Code</c> eine Projektmappe oder Datei; bei <c>Zoom</c> eine Besprechung. Wer
    /// eines davon einem Kunden zuordnete, buchte fortan jedes Fenster dieser Art auf ihn.
    /// </remarks>
    [Theory]
    [InlineData("cmd", "C:\\Windows\\system32\\cmd.exe")]
    [InlineData("powershell", "Administrator: Windows PowerShell")]
    [InlineData("RoyalTS", "Royal TS - Kunden")]
    [InlineData("OUTLOOK|Message", "Angebot Serverumzug")]
    [InlineData("Mail|Message", "Rückfrage Rechnung")]
    [InlineData("devenv", "TanssLogWatcher")]
    [InlineData("Code", "Program.cs")]
    [InlineData("Zoom", "Zoom Meeting")]
    [InlineData("WindowsTerminal", "Windows PowerShell")]
    [InlineData("host", "irgendein Fenstertitel")]
    public void WoDasZielKeinGeraetIstEntstehtKeine(string profil, string bezeichner)
    {
        Assert.Null(DeviceIdentity.From(profil, bezeichner));
    }

    /// <summary>
    /// Der teuerste denkbare Fehlgriff.
    /// </summary>
    /// <remarks>
    /// Der Platzhalter heisst „hier wurde nichts aufgelöst“. Als Kennung wäre er die eine
    /// Zeile, die auf <b>jede</b> unaufgelöste Sitzung passt — quer über alle Kunden. Gemessen
    /// an der Zustandsdatenbank dieses Arbeitsplatzes trugen vier von fünfzehn Sitzungen
    /// genau diesen Platzhalter.
    /// </remarks>
    [Fact]
    public void DerPlatzhalterErgibtNiemalsEineKennung()
    {
        Assert.Null(DeviceIdentity.From("mstsc", SessionConstants.NoDestinationTitle));
        Assert.Null(DeviceIdentity.From("TeamViewer", SessionConstants.NoDestinationTitle));
        Assert.Null(DeviceIdentity.From("mstsc", "  " + SessionConstants.NoDestinationTitle + "  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OhneBezeichnerEntstehtKeine(string? bezeichner)
    {
        Assert.Null(DeviceIdentity.From("mstsc", bezeichner));
    }

    [Fact]
    public void EinUnbekanntesProfilErgibtKeine()
    {
        Assert.Null(DeviceIdentity.From("gibt-es-nicht", "srv-kunde01"));
        Assert.Null(DeviceIdentity.From(null, "srv-kunde01"));
        Assert.Null(DeviceIdentity.From("  ", "srv-kunde01"));
    }

    /// <summary>
    /// Zu lang heisst gar nicht — nicht gekürzt.
    /// </summary>
    /// <remarks>
    /// Eine abgeschnittene Kennung sähe aus wie eine Kennung und passte auf nichts. Lieber
    /// keine Übersetzung als eine, die auf die falsche Zeile zeigt.
    /// </remarks>
    [Fact]
    public void ZuLangErgibtKeineStattEinerAbgeschnittenen()
    {
        string gerade = new('x', DeviceIdentity.MaximumLength);
        string zuLang = new('x', DeviceIdentity.MaximumLength + 1);

        Assert.Equal(gerade, DeviceIdentity.From("mstsc", gerade));
        Assert.Null(DeviceIdentity.From("mstsc", zuLang));
    }

    /// <summary>
    /// Unverändert weitergereicht — bis auf Leerzeichen am Rand.
    /// </summary>
    /// <remarks>
    /// Wie TANSS vergleicht, ist <b>nicht</b> gemessen. Ein selbst erdachtes Kleinschreiben
    /// oder Abschneiden von Domänensuffixen träfe dann womöglich eine andere Zeile als die,
    /// die jemand in TANSS angelegt hat.
    /// </remarks>
    [Fact]
    public void DieSchreibweiseBleibtWieSieIst()
    {
        Assert.Equal("SRV-Kunde01", DeviceIdentity.From("mstsc", "SRV-Kunde01"));
        Assert.Equal("srv.kunde.local", DeviceIdentity.From("mstsc", " srv.kunde.local "));
    }

    /// <summary>
    /// Der Katalog ist die eine Quelle — nicht eine Namensliste daneben.
    /// </summary>
    /// <remarks>
    /// Kein Profil, dessen Ziel der ganze Fenstertitel ist, darf eine Kennung liefern. Diese
    /// Regel hier zu prüfen statt in einer Aufzählung hält sie auch für Profile, die es heute
    /// noch nicht gibt.
    /// </remarks>
    [Fact]
    public void KeinProfilMitGanzemFenstertitelLiefertEineKennung()
    {
        foreach (MonitoringProfile profil in MonitoringProfiles.All)
        {
            if (profil.DestinationResolveMethod == DestinationResolveMethod.WindowTitle)
            {
                Assert.False(profil.YieldsDeviceIdentity,
                    $"„{profil.Key}“ nimmt den ganzen Fenstertitel als Ziel und darf keine "
                    + "Gerätekennung liefern.");
            }
        }
    }
}
