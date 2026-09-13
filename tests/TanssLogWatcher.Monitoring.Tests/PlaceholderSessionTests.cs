using TanssLogWatcher.Monitoring.Model;
using Xunit;

namespace TanssLogWatcher.Monitoring.Tests;

/// <summary>
/// Der Platzhalter: Wann darf aus einem Fenster ohne erkennbares Ziel eine Sitzung werden?
/// </summary>
/// <remarks>
/// <para><b>Der Anlass ist ein Vorfall im Betrieb.</b> Das Aufrufen der AnyDesk-Webseite
/// brachte die installierte AnyDesk-Anwendung nach vorn. Deren Hauptfenster heisst schlicht
/// <c>AnyDesk</c>, solange niemand verbunden ist; das Muster „alles vor dem Bindestrich“ greift
/// darin nicht — und es entstand trotzdem eine Sitzung, weil die Zielauflösung in diesem Fall
/// einen Platzhalter lieferte.</para>
///
/// <para>Die Unterscheidung, um die es geht: Ein Fenster <b>ohne</b> Titel ist eine Anwendung,
/// die gerade verbindet — dafür ist der Platzhalter da, und die Fensterquelle liefert ein
/// solches Fenster erst gar nicht mit. Ein Fenster <b>mit</b> einem Titel, der nichts hergibt,
/// ist eine Anwendung, die offen steht und nichts tut.</para>
/// </remarks>
public sealed class PlaceholderSessionTests
{
    private readonly FakeTcpConnectionSource tcp = new();
    private readonly FakeHostNameResolver dns = new();

    private static readonly MonitoringSetting Aktiv =
        new() { Key = "x", RemoteSupportTypeId = 1003 };

    /// <summary>
    /// Der gemeldete Fall: AnyDesk steht offen, niemand ist verbunden — keine Sitzung.
    /// </summary>
    [Fact]
    public void EinOffenesAnyDeskOhneVerbindungErgibtKeineSitzung()
    {
        DestinationResolver resolver = new(tcp, dns);

        IReadOnlyList<ResolvedDestination> resolved = resolver.Resolve(
            MonitoringProfiles.ByKey["AnyDesk"], Aktiv,
            new ProcessInfo(42, 4, "AnyDesk", null), mainWindowHandle: 7,
            [new WindowInfo("AnyDesk", 42, 7)], []);

        Assert.Empty(resolved);
    }

    /// <summary>
    /// Sobald AnyDesk verbunden ist, trägt das Fenster die Gegenstelle — und die Sitzung
    /// entsteht wie bisher.
    /// </summary>
    [Fact]
    public void EinVerbundenesAnyDeskErgibtEineSitzung()
    {
        DestinationResolver resolver = new(tcp, dns);

        ResolvedDestination resolved = Assert.Single(resolver.Resolve(
            MonitoringProfiles.ByKey["AnyDesk"], Aktiv,
            new ProcessInfo(42, 4, "AnyDesk", null), mainWindowHandle: 7,
            [new WindowInfo("kunde-srv01 - AnyDesk", 42, 7)], []));

        Assert.Equal("kunde-srv01", resolved.Destination);
    }

    /// <summary>
    /// Microsoft Remotedesktop wird gestartet, <b>um</b> sich zu verbinden — dort ist der
    /// Platzhalter richtig und bleibt. Sonst ginge der Beginn der Sitzung verloren: Aus der
    /// Prozessliste ist er rückwirkend nicht zu holen.
    /// </summary>
    [Fact]
    public void BeiMicrosoftRemotedesktopBleibtDerPlatzhalter()
    {
        DestinationResolver resolver = new(tcp, dns);

        ResolvedDestination resolved = Assert.Single(resolver.Resolve(
            MonitoringProfiles.ByKey["mstsc"], Aktiv,
            new ProcessInfo(42, 4, "mstsc", null), mainWindowHandle: 7,
            [new WindowInfo("Verbindung wird hergestellt", 42, 7)], []));

        Assert.Equal(SessionConstants.NoDestinationTitle, resolved.Destination);
    }

    /// <summary>
    /// Ohne Fenster bleibt der Platzhalter auch bei einer dauerhaft offenen Anwendung: Ein
    /// Prozess ohne sichtbares Fenster mit Titel ist genau der Fall, für den er gedacht ist.
    /// </summary>
    [Fact]
    public void OhneFensterBleibtDerPlatzhalter()
    {
        DestinationResolver resolver = new(tcp, dns);

        ResolvedDestination resolved = Assert.Single(resolver.Resolve(
            MonitoringProfiles.ByKey["AnyDesk"], Aktiv,
            new ProcessInfo(42, 4, "AnyDesk", null), mainWindowHandle: 0, [], []));

        Assert.Equal(SessionConstants.NoDestinationTitle, resolved.Destination);
    }

    /// <summary>
    /// Die Angabe ist bewusst nur dort gesetzt, wo sicher ist, dass die Anwendung auch ohne
    /// Fernwartung offen steht — und ausdrücklich <b>nicht</b> bei den beiden Anwendungen, die
    /// es nur während einer Verbindung gibt. Eine Sitzung zu wenig kostet Arbeitszeit.
    /// </summary>
    [Theory]
    [InlineData("mstsc", false)]
    [InlineData("RdClient.Windows", false)]
    [InlineData("AnyDesk", true)]
    [InlineData("TeamViewer", true)]
    [InlineData("Supremo", true)]
    [InlineData("Rustdesk", true)]
    public void NurDauerhaftOffeneAnwendungenVerlangenEinZiel(string key, bool expected)
    {
        Assert.Equal(expected, MonitoringProfiles.ByKey[key].RequiresResolvedDestination);
    }
}
