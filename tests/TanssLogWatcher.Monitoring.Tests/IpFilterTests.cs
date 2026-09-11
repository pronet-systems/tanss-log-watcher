using System.Net;
using Xunit;

namespace TanssLogWatcher.Monitoring.Tests;

/// <summary>
/// Grenzwerte der CIDR-Prüfung. Der Auslöser dieser Prüfungen ist ein Defekt der Vorlage: sie
/// griff ohne Bereichsprüfung ins Adressfeld und warf bei einem Tippfehler wie <c>/40</c> in jedem
/// Durchlauf eine Ausnahme.
/// </summary>
public sealed class IpFilterTests
{
    [Theory]
    [InlineData("10.1.2.3", "0.0.0.0/0", true)]
    [InlineData("255.255.255.255", "0.0.0.0/0", true)]
    [InlineData("10.1.2.3", "10.1.2.3/32", true)]
    [InlineData("10.1.2.4", "10.1.2.3/32", false)]
    [InlineData("10.1.2.3", "10.0.0.0/8", true)]
    [InlineData("11.1.2.3", "10.0.0.0/8", false)]
    [InlineData("192.168.5.7", "192.168.4.0/22", true)]
    [InlineData("192.168.8.7", "192.168.4.0/22", false)]
    [InlineData("10.0.0.130", "10.0.0.128/25", true)]
    [InlineData("10.0.0.127", "10.0.0.128/25", false)]
    public void CidrPruefungTrifftGenau(string address, string cidr, bool expected) =>
        Assert.Equal(expected, IpFilter.IsInSubnet(IPAddress.Parse(address), cidr));

    /// <summary>
    /// Jede unsinnige Eingabe ergibt <c>false</c> — insbesondere ein Präfix jenseits von 32, das in
    /// der Vorlage eine Ausnahme warf.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.1", "10.0.0.0/40")]
    [InlineData("10.0.0.1", "10.0.0.0/33")]
    [InlineData("10.0.0.1", "10.0.0.0/-1")]
    [InlineData("10.0.0.1", "10.0.0.0/ acht")]
    [InlineData("10.0.0.1", "10.0.0.0")]
    [InlineData("10.0.0.1", "10.0.0.0/8/16")]
    [InlineData("10.0.0.1", "keine-adresse/8")]
    [InlineData("10.0.0.1", "")]
    [InlineData("10.0.0.1", "   ")]
    [InlineData("10.0.0.1", null)]
    public void UnsinnigeEingabeErgibtFalschOhneAusnahme(string address, string? cidr) =>
        Assert.False(IpFilter.IsInSubnet(IPAddress.Parse(address), cidr));

    /// <summary>Verschiedene Adressfamilien passen nie zueinander und werfen nicht.</summary>
    [Theory]
    [InlineData("2001:db8::1", "10.0.0.0/8")]
    [InlineData("10.0.0.1", "2001:db8::/32")]
    public void GemischteAdressfamilienPassenNie(string address, string cidr) =>
        Assert.False(IpFilter.IsInSubnet(IPAddress.Parse(address), cidr));

    [Fact]
    public void IPv6PasstInEinenIPv6Bereich()
    {
        Assert.True(IpFilter.IsInSubnet(IPAddress.Parse("2001:db8::5"), "2001:db8::/32"));
        Assert.False(IpFilter.IsInSubnet(IPAddress.Parse("2001:db9::5"), "2001:db8::/32"));
        Assert.False(IpFilter.IsInSubnet(IPAddress.Parse("2001:db8::5"), "2001:db8::/128"));
    }

    [Fact]
    public void NullAdresseErgibtFalsch()
    {
        Assert.False(IpFilter.IsInSubnet(null, "10.0.0.0/8"));
        Assert.False(IpFilter.IsExcluded(null, "10.0.0.0/8"));
    }

    [Theory]
    [InlineData("10.1.2.3", "10.1.2.3", true)]
    [InlineData("10.1.2.3", "10.0.0.0/8", true)]
    [InlineData("10.1.2.3", "192.168.0.1;10.0.0.0/8", true)]
    [InlineData("10.1.2.3", " 192.168.0.1 ; 10.0.0.0/8 ", true)]
    [InlineData("10.1.2.3", "192.168.0.1;;172.16.0.0/12", false)]
    [InlineData("10.1.2.3", "kaputt;10.1.2.3", true)]
    [InlineData("10.1.2.3", "kaputt", false)]
    [InlineData("10.1.2.3", null, false)]
    [InlineData("10.1.2.3", "", false)]
    public void BenutzerlisteWirdMitSemikolonGetrennt(string address, string? list, bool expected) =>
        Assert.Equal(expected, IpFilter.IsExcluded(IPAddress.Parse(address), list));

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.5.5.5", true)]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("2001:db8::1", false)]
    public void UninteressanteAdressenWerdenErkannt(string address, bool expected) =>
        Assert.Equal(expected, IpFilter.IsUninteresting(IPAddress.Parse(address)));
}
