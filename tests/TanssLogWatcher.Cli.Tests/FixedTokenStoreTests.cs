using TanssLogWatcher.Api.Contract;
using Xunit;

namespace TanssLogWatcher.Cli.Tests;

/// <summary>
/// Der Tokenspeicher der Probe beim Tokenwechsel.
/// </summary>
/// <remarks>
/// <para><b>Warum es diese Tests gibt.</b> Dieser Speicher gab das übergebene Token wörtlich
/// zurück. <see cref="ITokenStore.Read"/> sagt aber zu, es <i>einschliesslich</i> des Präfixes
/// <c>Bearer </c> zu liefern, und der Zugang setzt den Rückgabewert unverändert in den
/// Kopfzeileneintrag <c>apiToken</c>. Fehlt das Präfix, überspringt TANSS die Prüfung
/// vollständig und weist den Aufruf mit 403 ab — ununterscheidbar von einem abgelaufenen
/// Token.</para>
///
/// <para>Der Fehler blieb unbemerkt, weil jeder vorhandene Test einen gefälschten Zugang
/// benutzt und deshalb nie eine Kopfzeile gebaut wird. Geprüft wird hier folglich nicht der
/// Netzweg, sondern der Wert, den der Speicher herausgibt — genau die Stelle, an der es
/// auseinanderlief.</para>
/// </remarks>
public sealed class FixedTokenStoreTests
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.Zm9vYmFy";

    [Fact]
    public void Read_SetztDasPraefix_WennEsFehlt()
    {
        FixedTokenStore store = new(Jwt);

        Assert.Equal("Bearer " + Jwt, store.Read());
    }

    [Fact]
    public void Read_BehaeltDasPraefix_WennEsSchonDaIst()
    {
        FixedTokenStore store = new("Bearer " + Jwt);

        Assert.Equal("Bearer " + Jwt, store.Read());
    }

    /// <summary>
    /// Zwei Präfixe entstehen nicht.
    /// </summary>
    /// <remarks>
    /// Der gefährlichste Fall, weil er die Prüfung des Servers besteht: <c>startsWith("Bearer ")</c>
    /// trifft zu, und erst das Entschlüsseln scheitert — an einem Token, das keines ist.
    /// </remarks>
    [Fact]
    public void Read_VerdoppeltDasPraefixNicht()
    {
        FixedTokenStore store = new("Bearer " + Jwt);

        Assert.Equal(1, Occurrences(store.Read(), "Bearer "));
    }

    [Fact]
    public void Read_RaeumtLeerraumUmDasToken()
    {
        FixedTokenStore store = new("  " + Jwt + "  ");

        Assert.Equal("Bearer " + Jwt, store.Read());
    }

    /// <summary>
    /// Ablegen bleibt verboten.
    /// </summary>
    /// <remarks>
    /// Dieser Speicher ist die Probe beim Tokenwechsel. Ein Speicher, der stillschweigend
    /// nichts täte, würde beim nächsten Umbau als echter verwendet — und der Tokenwechsel ginge
    /// dann folgenlos ins Leere.
    /// </remarks>
    [Fact]
    public void Write_Wirft()
    {
        FixedTokenStore store = new(Jwt);

        _ = Assert.Throws<InvalidOperationException>(() => store.Write(Jwt));
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        int at = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
