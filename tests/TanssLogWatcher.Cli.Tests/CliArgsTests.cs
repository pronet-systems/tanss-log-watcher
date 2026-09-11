using TanssLogWatcher.Cli.CommandLine;
using Xunit;

namespace TanssLogWatcher.Cli.Tests;

/// <summary>
/// Die Auswertung der Befehlszeile.
/// </summary>
/// <remarks>
/// Sie ist die erste Stelle, an der ein Benutzer etwas falsch machen kann — und die einzige,
/// die ihm sagen kann, was stattdessen erwartet wird. Geprüft wird deshalb nicht nur, dass ein
/// Fehler erkannt wird, sondern dass die Meldung den richtigen Weg weist.
/// </remarks>
public sealed class CliArgsTests
{
    [Fact]
    public void Ohne_Argumente_ist_kein_Befehl_angegeben()
    {
        CliArgs parsed = CliArgs.Parse([]);

        Assert.Equal(CliCommand.None, parsed.Command);
        Assert.NotNull(parsed.Error);
        Assert.Contains("kein Befehl", parsed.Error, StringComparison.Ordinal);
        Assert.Contains("doctor", parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Unbekannter_Befehl_nennt_die_bekannten()
    {
        CliArgs parsed = CliArgs.Parse(["doktor"]);

        Assert.NotNull(parsed.Error);
        Assert.Contains("doktor", parsed.Error, StringComparison.Ordinal);
        Assert.Contains("windows", parsed.Error, StringComparison.Ordinal);
        Assert.Contains("queue", parsed.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    [InlineData("/?")]
    public void Hilfe_wird_in_allen_ueblichen_Schreibweisen_erkannt(string argument)
    {
        CliArgs parsed = CliArgs.Parse([argument]);

        Assert.True(parsed.HelpRequested);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void Hilfe_gilt_auch_hinter_einem_Befehl()
    {
        CliArgs parsed = CliArgs.Parse(["queue", "--help"]);

        Assert.True(parsed.HelpRequested);
        Assert.Equal(CliCommand.Queue, parsed.Command);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void Profile_ohne_Schluessel_ist_ein_fehlendes_Argument()
    {
        CliArgs parsed = CliArgs.Parse(["windows", "--profile"]);

        Assert.NotNull(parsed.Error);
        Assert.Contains("Profilschlüssel", parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_nimmt_den_naechsten_Wert_als_Schluessel()
    {
        CliArgs parsed = CliArgs.Parse(["windows", "--profile", "mstsc"]);

        Assert.Null(parsed.Error);
        Assert.Equal(CliCommand.Windows, parsed.Command);
        Assert.Equal("mstsc", parsed.ProfileKey);
    }

    [Fact]
    public void Token_ohne_Unterbefehl_nennt_status_und_rotate()
    {
        CliArgs parsed = CliArgs.Parse(["token"]);

        Assert.NotNull(parsed.Error);
        Assert.Contains("status", parsed.Error, StringComparison.Ordinal);
        Assert.Contains("rotate", parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Token_mit_falschem_Unterbefehl_wird_abgelehnt()
    {
        CliArgs parsed = CliArgs.Parse(["token", "erneuern"]);

        Assert.NotNull(parsed.Error);
        Assert.Contains("erneuern", parsed.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("rotate")]
    public void Token_nimmt_beide_Unterbefehle(string sub)
    {
        CliArgs parsed = CliArgs.Parse(["token", sub]);

        Assert.Null(parsed.Error);
        Assert.Equal(CliCommand.Token, parsed.Command);
        Assert.Equal(sub, parsed.SubCommand);
    }

    [Fact]
    public void Schalter_des_falschen_Befehls_nennt_die_richtigen()
    {
        // Der haeufigste Aufruffehler: ein Schalter, den es nur woanders gibt.
        CliArgs parsed = CliArgs.Parse(["watch", "--flush"]);

        Assert.NotNull(parsed.Error);
        Assert.Contains("--flush", parsed.Error, StringComparison.Ordinal);
        Assert.Contains("--dry-run", parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Watch_erkennt_den_Trockenlauf()
    {
        CliArgs parsed = CliArgs.Parse(["watch", "--dry-run"]);

        Assert.Null(parsed.Error);
        Assert.True(parsed.DryRun);
    }

    [Fact]
    public void Queue_erkennt_das_Senden()
    {
        CliArgs parsed = CliArgs.Parse(["queue", "--flush"]);

        Assert.Null(parsed.Error);
        Assert.True(parsed.Flush);
    }

    [Fact]
    public void Version_wird_erkannt()
    {
        CliArgs parsed = CliArgs.Parse(["--version"]);

        Assert.True(parsed.VersionRequested);
        Assert.Null(parsed.Error);
    }
}
