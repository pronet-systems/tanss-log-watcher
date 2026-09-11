using TanssLogWatcher.Api.Auth;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Api.Tests.Fakes;
using Xunit;

namespace TanssLogWatcher.Api.Tests;

/// <summary>
/// Prüft die Zeitrechnung gegenüber TANSS — und hält fest, wo bewusst nicht über
/// <see cref="TanssTime"/> gerechnet wird.
/// </summary>
public sealed class TanssTimeTests
{
    [Fact]
    public void Die_Laufzeit_Eines_Timers_Rechnet_In_Tanss_Sekunden()
    {
        // StartTime kommt in TANSS-Sekunden vom Server; beide Seiten der Subtraktion muessen
        // deshalb aus demselben Umrechner stammen.
        TanssTimer running = new() { StartTime = 1_757_500_000, Duration = 60 };
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_757_500_100);

        Assert.Equal(TimeSpan.FromSeconds(160), running.Elapsed(now));
        Assert.Equal(TanssTime.ToUnixSeconds(now) - running.StartTime,
                     (long)running.Elapsed(now).TotalSeconds - running.Duration);
    }

    [Fact]
    public void Ein_Angehaltener_Timer_Rechnet_Die_Gegenwart_Nicht_Ein()
    {
        TanssTimer stopped = new() { StartTime = 0, Duration = 90 };

        Assert.False(stopped.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(90), stopped.Elapsed(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Die_Null_Bedeutet_Fuer_Tanss_Zeiten_Nicht_Gesetzt()
    {
        Assert.Null(TanssTime.FromUnixSeconds(0));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), TanssTime.FromUnixSeconds(1));
    }

    [Fact]
    public void Ein_Exp_Von_Null_Heisst_Abgelaufen_Und_Nicht_Unbefristet()
    {
        // Genau deshalb rechnet AuthModel bei exp und iat absichtlich nicht ueber TanssTime:
        // dessen 0 hiesse "nicht gesetzt", und ein fehlendes exp gilt hier als unbefristet.
        // Ein Token mit exp=0 waere damit ewig gueltig statt seit 1970 abgelaufen.
        string token = TestEnvironment.Base64Url("{}") + "."
                       + TestEnvironment.Base64Url("""{"exp":0,"iat":0}""") + ".sig";

        TanssTokenClaims claims = TanssAuth.DecodeClaims(token);

        Assert.Equal(DateTimeOffset.UnixEpoch, claims.ExpiresAt);
        Assert.Equal(DateTimeOffset.UnixEpoch, claims.IssuedAt);
        Assert.True(claims.DaysRemaining(DateTimeOffset.UtcNow) < 0);
    }

    [Fact]
    public void Ein_Fehlendes_Exp_Bleibt_Der_Einzige_Fall_Von_Unbefristet()
    {
        string token = TestEnvironment.Base64Url("{}") + "."
                       + TestEnvironment.Base64Url("""{"sub":"42"}""") + ".sig";

        TanssTokenClaims claims = TanssAuth.DecodeClaims(token);

        Assert.Null(claims.ExpiresAt);
        Assert.Equal(double.PositiveInfinity, claims.DaysRemaining(DateTimeOffset.UtcNow));
    }
}
