using System.Net;
using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

public sealed class IpRangeTests
{
    [Theory]
    [InlineData("10.0.0.5", "10.0.0.5", true)]
    [InlineData("10.0.0.5", "10.0.0.6", false)]
    [InlineData("10.0.0.0/8", "10.255.255.254", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("192.168.1.0/24", "192.168.1.200", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    [InlineData("fe80::/10", "fe80::1234", true)]
    [InlineData("fe80::/10", "2003::1", false)]
    public void Zugehoerigkeit(string range, string address, bool expected) =>
        Assert.Equal(expected, IpRange.Parse(range).Contains(address));

    [Fact]
    public void Gesetzte_Bits_unterhalb_des_Praefixes_werden_abgeschnitten()
    {
        // 192.168.1.7/24 ist gleichbedeutend mit 192.168.1.0/24. Eine sonst richtige
        // Zeile deswegen abzulehnen waere kleinlich.
        IpRange range = IpRange.Parse("192.168.1.7/24");

        Assert.Equal(IPAddress.Parse("192.168.1.0"), range.Network);
        Assert.Equal(24, range.PrefixLength);
        Assert.Equal("192.168.1.0/24", range.ToString());
    }

    [Fact]
    public void Eine_einzelne_Adresse_ist_ein_voller_Praefix()
    {
        Assert.Equal(32, IpRange.Parse("10.0.0.5").PrefixLength);
        Assert.Equal(128, IpRange.Parse("::1").PrefixLength);
    }

    [Fact]
    public void Ueber_Adressfamilien_hinweg_wird_nie_getroffen()
    {
        // Wer beides meint, traegt beides ein. Ein stillschweigendes Uebersetzen zwischen
        // IPv4 und IPv6 waere eine Annahme, die niemand geprueft hat.
        Assert.False(IpRange.Parse("10.0.0.0/8").Contains("::ffff:10.0.0.1"));
        Assert.False(IpRange.Parse("::/0").Contains("10.0.0.1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("kunde-srv01")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/acht")]
    [InlineData("fe80::/129")]
    [InlineData("999.1.1.1")]
    public void Unbrauchbare_Angaben_werden_erkannt(string? text)
    {
        Assert.False(IpRange.TryParse(text, out IpRange? range));
        Assert.Null(range);
    }

    [Fact]
    public void Parse_wirft_mit_einer_Meldung_die_Beispiele_nennt()
    {
        FormatException error = Assert.Throws<FormatException>(() => IpRange.Parse("kunde-srv01"));

        Assert.Contains("CIDR", error.Message, StringComparison.Ordinal);
        Assert.Contains("192.168.0.0/16", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Die_Ausschlussliste_einer_Regel_laesst_sich_geschlossen_pruefen()
    {
        MonitoringEntry entry = new()
        {
            Key = "rdp",
            RemoteSupportTypeId = 1001,
            ExcludeIpAddresses = ["10.0.0.0/8", "192.168.178.1"],
        };

        IReadOnlyList<IpRange> ranges = entry.ParseExcludedRanges();

        Assert.Equal(2, ranges.Count);
        Assert.Contains(ranges, r => r.Contains("10.1.2.3"));
        Assert.DoesNotContain(ranges, r => r.Contains("172.16.0.1"));
    }

    [Fact]
    public void Gleichheit_richtet_sich_nach_Netz_und_Praefix()
    {
        Assert.Equal(IpRange.Parse("10.0.0.0/8"), IpRange.Parse("10.1.2.3/8"));
        Assert.NotEqual(IpRange.Parse("10.0.0.0/8"), IpRange.Parse("10.0.0.0/16"));
        Assert.Equal(IpRange.Parse("10.0.0.0/8").GetHashCode(),
                     IpRange.Parse("10.0.0.0/8").GetHashCode());
    }

    [Theory]
    [InlineData("10.0.0.0/8", 1)]
    [InlineData("10.0.0.5", 1)]
    [InlineData("10.0.0.0/8;192.168.0.0/16", 2)]
    [InlineData("10.0.0.0/8; 192.168.0.0/16 ; fe80::/10", 3)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void Eine_getrennte_Liste_wird_Eintrag_fuer_Eintrag_gelesen(string? text, int expected)
    {
        Assert.True(IpRange.TrySplit(text, ';', out IReadOnlyList<string> entries, out string? bad));

        Assert.Equal(expected, entries.Count);
        Assert.Null(bad);
        Assert.All(entries, entry => Assert.True(IpRange.TryParse(entry, out _)));
    }

    [Theory]
    [InlineData("999.0.0.1", "999.0.0.1")]
    [InlineData("10.0.0.0/8;999.0.0.1", "999.0.0.1")]
    [InlineData("10.0.0.0/8; 192.168.0.0/16; nicht-eine-adresse", "nicht-eine-adresse")]
    [InlineData("kaputt; 10.0.0.0/8", "kaputt")]
    [InlineData("10.0.0.0/40", "10.0.0.0/40")]
    public void Ein_einziger_fehlerhafter_Eintrag_verwirft_die_ganze_Liste(string text, string bad)
    {
        // Das ist der Punkt der Uebung: Nicht "zwei von drei uebernommen", sondern nichts -
        // sonst saehe der Benutzer den Grossteil seiner Eingabe wiederkehren und hielte sie
        // fuer vollstaendig.
        Assert.False(IpRange.TrySplit(text, ';', out IReadOnlyList<string> entries,
                                      out string? invalid));

        Assert.Empty(entries);
        Assert.Equal(bad, invalid);
    }

    [Fact]
    public void Ein_abschliessendes_Trennzeichen_ist_kein_Fehler()
    {
        Assert.True(IpRange.TrySplit("10.0.0.0/8; 192.168.0.0/16;", ';',
                                     out IReadOnlyList<string> entries, out _));

        Assert.Equal(["10.0.0.0/8", "192.168.0.0/16"], entries);
    }

    [Fact]
    public void Die_Schreibweise_des_Benutzers_bleibt_erhalten()
    {
        // Zurueckgeschrieben wird, was eingetippt wurde - nicht das maskierte Netz. Sonst
        // aendert sich die Datei bei jedem Speichern, ohne dass jemand etwas geaendert haette.
        Assert.True(IpRange.TrySplit(" 192.168.1.7/24 ", ';',
                                     out IReadOnlyList<string> entries, out _));

        Assert.Equal(["192.168.1.7/24"], entries);
    }
}
