using System.Net;
using TanssLogWatcher.Cli.Commands;
using TanssLogWatcher.Monitoring.Model;
using Xunit;

namespace TanssLogWatcher.Cli.Tests;

/// <summary>
/// Die Fensterliste.
/// </summary>
/// <remarks>
/// Sie ist das Werkzeug, mit dem ein Techniker ein nicht mehr greifendes Muster repariert.
/// Geprüft wird sie gegen erfundene Fenster: Eine Prüfung gegen den echten Bildschirm wäre von
/// dem abhängig, was gerade offen ist — also von nichts Prüfbarem.
/// </remarks>
public sealed class WindowsCommandTests
{
    [Fact]
    public void Fenster_erscheinen_mit_Prozess_PID_Handle_Titel_und_Verbindungen()
    {
        string output = Run(null);

        Assert.Contains("mstsc", output, StringComparison.Ordinal);
        Assert.Contains("4711", output, StringComparison.Ordinal);
        Assert.Contains("0x2A", output, StringComparison.Ordinal);
        Assert.Contains("srv01 - Remotedesktopverbindung", output, StringComparison.Ordinal);
        Assert.Contains("203.0.113.9", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Das_erste_besitzerlose_Fenster_ist_das_Hauptfenster()
    {
        // Nicht das oberste: Ein offener Dialog machte sich sonst zum Hauptfenster und
        // unterdrueckte bei MonitorOnlyMainWindowHandle genau das Fenster, auf das es ankommt.
        string output = Run(null);

        int mainLine = output.IndexOf("Hauptfenster", StringComparison.Ordinal);
        int ownedLine = output.IndexOf("besessen", StringComparison.Ordinal);

        Assert.True(mainLine >= 0 && ownedLine >= 0);
        Assert.True(mainLine < ownedLine);
    }

    [Fact]
    public void Ein_Profil_zeigt_das_Ziel_das_es_ableiten_wuerde()
    {
        string output = Run("mstsc");

        Assert.Contains("Profil „mstsc“", output, StringComparison.Ordinal);
        Assert.Contains("Ziel „srv01“", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_unbekanntes_Profil_endet_mit_dem_Aufruffehler_und_nennt_die_bekannten()
    {
        using StringWriter writer = new();
        int code = WindowsCommand.Run(Inputs(), "gibtsnicht", writer);

        Assert.Equal(ExitCode.Usage, code);
        Assert.Contains("mstsc", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Nicht_lesbare_Verbindungen_kosten_nur_diesen_Prozess()
    {
        // Hausregel 5: Ein Prozess, der zwischen Momentaufnahme und Abfrage stirbt, darf nicht
        // die ganze Liste kosten.
        WindowsInputs inputs = Inputs() with { TcpConnections = new FailingTcpSource() };

        using StringWriter writer = new();
        int code = WindowsCommand.Run(inputs, null, writer);

        Assert.Equal(ExitCode.Healthy, code);
        Assert.Contains("nicht lesbar", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("mstsc", writer.ToString(), StringComparison.Ordinal);
    }

    private static string Run(string? profileKey)
    {
        using StringWriter writer = new();
        _ = WindowsCommand.Run(Inputs(), profileKey, writer);
        return writer.ToString();
    }

    private static WindowsInputs Inputs() => new()
    {
        Windows = new FakeWindowSource
        {
            Windows =
            [
                new WindowInfo("srv01 - Remotedesktopverbindung", 4711, 42),
                new WindowInfo("Verbindung wird hergestellt", 4711, 43, IsOwned: true),
            ],
        },
        Processes = new FakeProcessSource
        {
            Processes = [new ProcessInfo(4711, 900, "mstsc", DateTimeOffset.UnixEpoch)],
        },
        TcpConnections = new FakeTcpConnectionSource
        {
            Remotes = [IPAddress.Parse("203.0.113.9")],
        },
    };

    /// <summary>Eine Verbindungsquelle, die scheitert.</summary>
    private sealed class FailingTcpSource : TanssLogWatcher.Monitoring.ITcpConnectionSource
    {
        public IReadOnlyList<IPAddress> GetRemoteAddresses(int processId, IEnumerable<int> childIds) =>
            throw new InvalidOperationException("Prozess inzwischen beendet.");
    }
}
