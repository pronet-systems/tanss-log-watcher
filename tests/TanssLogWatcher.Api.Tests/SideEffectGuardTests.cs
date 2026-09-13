using Xunit;

namespace TanssLogWatcher.Api.Tests;

/// <summary>
/// Der Riegel gegen Wiederholungen auf Pfaden, die trotz <c>GET</c> etwas hinterlassen.
/// </summary>
/// <remarks>
/// <para><b>Warum das einen eigenen Fall verdient.</b> TANSS 10.10.0 kann ein ausgestelltes
/// Token nicht widerrufen. Eine automatische Wiederholung nach einer Zeitüberschreitung prägt
/// deshalb kein zweites Mal dasselbe, sondern ein zweites Token — bis zu ein Jahr gültig, und
/// niemand erfährt davon. Der Riegel ist die einzige Stelle, die das verhindert.</para>
///
/// <para>Er hing bis zuletzt an der einen bekannten Route <c>/api/v1/jwts/tanss_app</c>. Jede
/// weitere Tokenart unter <c>/jwts</c> — etwa die je Fernwartungsanbindung — wäre damit
/// ungeschützt gewesen, und zwar so lange, bis jemand daran gedacht hätte. Diese Fälle halten
/// fest, dass er am Präfix hängt.</para>
/// </remarks>
public sealed class SideEffectGuardTests
{
    [Theory]
    [InlineData("/api/v1/jwts/tanss_app")]
    [InlineData("/api/v1/jwts/remote_support")]
    [InlineData("/api/v1/jwts/tanss_app?duration=60000&isForTesting=true")]
    // Eine Tokenart, die es heute nicht gibt: Der Riegel soll sie tragen, BEVOR sie gebraucht
    // wird - sonst ist der erste Aufruf der ungeschützte.
    [InlineData("/api/v1/jwts/was_auch_immer_spaeter_kommt")]
    public void Alles_unter_jwts_laeuft_mit_genau_einem_Versuch(string pfad)
    {
        Assert.True(TanssRoutes.HasSideEffectOnGet(pfad));
    }

    [Theory]
    [InlineData("/api/v1/timers")]
    [InlineData("/api/tanss.x/v1/technicians")]
    [InlineData("/api/v1/tickets/own")]
    [InlineData("/api/v1/login")]
    // Kein Pfad ausserhalb von /jwts darf in den Riegel geraten: Wer einen gewoehnlichen
    // Lesevorgang nicht wiederholen darf, verliert ihn bei jedem Netzhaenger.
    public void Gewoehnliche_Lesevorgaenge_duerfen_wiederholt_werden(string pfad)
    {
        Assert.False(TanssRoutes.HasSideEffectOnGet(pfad));
    }

    /// <summary>
    /// Der Riegel darf nicht an einem Namen hängen, der sich woanders ändert.
    /// </summary>
    /// <remarks>
    /// <see cref="TanssRoutes.MintToken"/> muss unterhalb von
    /// <see cref="TanssRoutes.JwtPrefix"/> liegen. Zöge jemand die eine Konstante um, ohne die
    /// andere mitzuziehen, wäre der Riegel still wirkungslos — genau der Fehler, der hier
    /// behoben wurde.
    /// </remarks>
    [Fact]
    public void Die_bekannte_Tokenroute_liegt_unter_dem_Praefix()
    {
        Assert.StartsWith(TanssRoutes.JwtPrefix, TanssRoutes.MintToken, StringComparison.Ordinal);
        Assert.True(TanssRoutes.HasSideEffectOnGet(TanssRoutes.MintToken));
    }
}
