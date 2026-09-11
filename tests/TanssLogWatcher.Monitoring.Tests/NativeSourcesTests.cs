using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using TanssLogWatcher.Monitoring.Model;
using TanssLogWatcher.Monitoring.Native;
using Xunit;

namespace TanssLogWatcher.Monitoring.Tests;

/// <summary>
/// Die Windows-Umsetzungen gegen das laufende System.
/// </summary>
/// <remarks>
/// Kein Netzaufruf: die TCP-Tabelle wird nur gelesen, nicht erzeugt. Geprüft wird, dass die
/// Aufrufe tragen und die Zusagen der Schnittstellen einhalten — nicht, welche Fenster gerade offen
/// sind.
/// </remarks>
public sealed class NativeSourcesTests
{
    [Fact]
    public void ProzessaufnahmeFindetDenEigenenProzess()
    {
        ProcessSource sut = new();

        IReadOnlyList<ProcessInfo> snapshot = sut.Snapshot();

        Assert.NotEmpty(snapshot);
        ProcessInfo own = Assert.Single(snapshot, p => p.Id == Environment.ProcessId);
        Assert.Equal(Process.GetCurrentProcess().ProcessName, own.Name);
        Assert.DoesNotContain(".exe", own.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProzessaufnahmeKenntDenStartzeitpunktDesEigenenProzesses()
    {
        ProcessSource sut = new();

        ProcessInfo own = sut.Snapshot().Single(p => p.Id == Environment.ProcessId);

        DateTimeOffset expected = Process.GetCurrentProcess().StartTime;
        Assert.NotNull(own.StartedAt);
        Assert.True((own.StartedAt.Value - expected).Duration() < TimeSpan.FromSeconds(2),
                    $"Erwartet {expected:O}, geliefert {own.StartedAt:O}.");
    }

    /// <summary>
    /// Ein Prozess ohne Vater ist unauffällig, ein Prozess, der sein eigener Vater ist, wäre ein
    /// Fehler im Auslesen der Momentaufnahme.
    /// </summary>
    [Fact]
    public void ProzessaufnahmeLiefertPlausibleElternbeziehungen()
    {
        ProcessSource sut = new();

        IReadOnlyList<ProcessInfo> snapshot = sut.Snapshot();

        Assert.All(snapshot, p =>
        {
            Assert.True(p.Id >= 0);
            Assert.False(p.Id != 0 && p.ParentId == p.Id, $"Prozess {p.Id} ist sein eigener Vater.");
        });
    }

    [Fact]
    public void FensterquelleLiefertNurSichtbareFensterMitTitel()
    {
        WindowSource sut = new();

        IReadOnlyList<WindowInfo> result = sut.GetVisibleWindows();

        Assert.All(result, w =>
        {
            Assert.False(string.IsNullOrEmpty(w.Title));
            Assert.NotEqual(0, w.ProcessId);
            Assert.NotEqual(0, w.WindowHandle);
        });
    }

    /// <summary>Jedes Fenster erscheint höchstens einmal — sonst entstünden doppelte Sitzungen.</summary>
    [Fact]
    public void FensterquelleLiefertJedesFensterHoechstensEinmal()
    {
        WindowSource sut = new();

        IReadOnlyList<WindowInfo> result = sut.GetVisibleWindows();

        Assert.Equal(result.Count, result.Select(w => w.WindowHandle).Distinct().Count());
    }

    /// <summary>
    /// Ein besessenes Fenster wird als solches gemeldet, ein besitzerloses nicht.
    /// </summary>
    /// <remarks>
    /// Geprüft an zwei echten Fenstern dieses Testprozesses, weit außerhalb des sichtbaren
    /// Bildschirms angelegt. Ohne diese Unterscheidung nähme die Zustandsmaschine bei
    /// <c>MonitorOnlyMainWindowHandle</c> jeden obenauf liegenden Dialog für das Hauptfenster.
    /// </remarks>
    [Fact]
    public void FensterquelleErkenntBesesseneFenster()
    {
        const string OwnerTitle = "TANSS-Probe Hauptfenster";
        const string OwnedTitle = "TANSS-Probe Dialog";

        nint owner = ProbeWindow.Create(OwnerTitle, 0);
        Assert.NotEqual(0, owner);
        nint owned = 0;
        try
        {
            owned = ProbeWindow.Create(OwnedTitle, owner);
            Assert.NotEqual(0, owned);

            WindowSource sut = new();
            List<WindowInfo> mine = [.. sut.GetVisibleWindows().Where(w => w.ProcessId == Environment.ProcessId)];

            Assert.False(Assert.Single(mine, w => w.Title == OwnerTitle).IsOwned);
            Assert.True(Assert.Single(mine, w => w.Title == OwnedTitle).IsOwned);
        }
        finally
        {
            if (owned != 0)
            {
                ProbeWindow.Destroy(owned);
            }

            ProbeWindow.Destroy(owner);
        }
    }

    [Fact]
    public void TcpQuelleLiefertKeineUnbrauchbarenAdressen()
    {
        TcpConnectionSource sut = new();
        ProcessSource processes = new();
        int[] all = [.. processes.Snapshot().Select(p => p.Id)];

        List<IPAddress> found = [];
        foreach (int processId in all)
        {
            found.AddRange(sut.GetRemoteAddresses(processId, []));
        }

        Assert.All(found, address => Assert.False(IpFilter.IsUninteresting(address)));
    }

    [Fact]
    public void TcpQuelleLiefertFuerEineUnbenutzteKennungNichts()
    {
        TcpConnectionSource sut = new();

        Assert.Empty(sut.GetRemoteAddresses(int.MaxValue, []));
    }

    /// <summary>
    /// Die Bestandteile greifen zusammen: echte Fenster, echte Prozesse, echter Katalog. Bei
    /// abgeschalteten Profilen darf dabei nichts entstehen.
    /// </summary>
    [Fact]
    public void ZusammenspielMitEchtenQuellenLaeuftDurch()
    {
        using HostNameResolver dns = new();
        SessionEngine engine = new(new WindowSource(), new ProcessSource(),
                                   new DestinationResolver(new TcpConnectionSource(), dns));

        SessionRunResult result = engine.Run(MonitoringProfiles.Reconcile(null));

        Assert.Empty(result.Started);
        Assert.Empty(result.Ended);
    }
}

/// <summary>
/// Zwei echte Fenster für die Besitzerprüfung, angelegt ohne Fensterklasse eigener Art.
/// </summary>
/// <remarks>
/// Ein Fenster mit <c>WS_POPUP</c> und einem Fenster als drittletztem Argument bekommt genau das
/// als <b>Besitzer</b>, nicht als Elternteil — der Fall, den <c>GW_OWNER</c> meldet. Die Fenster
/// liegen bei -8000/-8000 und erscheinen damit auf keinem Bildschirm.
/// </remarks>
internal static partial class ProbeWindow
{
    private const uint WsPopup = 0x8000_0000;
    private const uint WsVisible = 0x1000_0000;
    private const uint WsExToolWindow = 0x0000_0080;

    internal static nint Create(string title, nint owner) =>
        CreateWindowEx(WsExToolWindow, "STATIC", title, WsPopup | WsVisible,
                       -8000, -8000, 10, 10, owner, 0, 0, 0);

    internal static void Destroy(nint handle) => _ = DestroyWindow(handle);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16,
                   SetLastError = true)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
                                               int x, int y, int width, int height, nint owner, nint menu,
                                               nint instance, nint parameter);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint handle);
}
