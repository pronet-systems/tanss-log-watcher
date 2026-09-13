using System.IO;
using System.Runtime.Versioning;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Recording;
using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Ein Test, der eine funktionierende Bildschirmaufnahme braucht.
/// </summary>
/// <remarks>
/// <b>Übersprungen ist nicht bestanden.</b> Auf einem Bauläufer ohne Bildschirmsitzung gibt es
/// keine Bildschirmaufnahme; ein Test, der dort still durchginge, behauptete etwas Geprüftes,
/// wo nichts geprüft wurde. Deshalb wird er ausdrücklich übersprungen — und der Testbericht
/// weist Übersprungenes getrennt aus.
/// </remarks>
public sealed class CaptureFactAttribute : FactAttribute
{
    /// <summary>Baut das Kennzeichen und überspringt, wenn keine Aufnahme möglich ist.</summary>
    public CaptureFactAttribute()
    {
        if (!IsAvailable())
        {
            Skip = "Dieser Rechner hat keine Bildschirmaufnahme: Windows.Graphics.Capture "
                + "meldet sich als nicht verfügbar. Üblich auf einem Bauläufer ohne "
                + "Bildschirmsitzung.";
        }
    }

    private static bool IsAvailable()
    {
        try
        {
            return OperatingSystem.IsWindows() && CaptureSupport.IsAvailable();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// Ein echtes temporäres Verzeichnis. Wird am Ende jedes Falls wieder entfernt.
/// </summary>
/// <remarks>
/// Wie im Ablageprojekt: Hier wird weder das Dateisystem noch SQLite nachgebildet. Genau die
/// Eigenschaften, auf die es ankommt — das Anlegen von Ordnern, die Grösse einer geschriebenen
/// Datei, das Sperrverhalten von SQLite —, hat eine Attrappe nicht.
/// </remarks>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tanss-app-tests",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Eine Fensterquelle, die genau die übergebenen Fenster liefert.
/// </summary>
/// <remarks>
/// <b>Keine erfundene Geometrie:</b> Die Werte stammen aus <c>GetWindowRect</c> eines echten
/// Fensters. Ersetzt wird nur die <b>Aufzählung</b> — der Testläufer hat eigene Fenster, und
/// eine echte Aufzählung nähme im Zweifel das Fenster des Testläufers mit auf. Genau dafür gibt
/// es diese Schnittstelle: Sie trennt, was sich entscheiden lässt, von dem, was einen
/// Bildschirm braucht.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class FixedWindows(params WindowBox[] windows) : IWindowGeometrySource
{
    private readonly WindowBox[] _windows = windows;

    public IReadOnlyList<WindowBox> WindowsOf(int processId) => _windows;
}

/// <summary>
/// Eine Fensterquelle, deren Geometrie sich von Abruf zu Abruf ändert.
/// </summary>
/// <remarks>
/// <b>Sie bildet einen gemessenen Vorfall nach.</b> Ein Remotedesktop-Fenster ändert beim
/// Verbindungsaufbau zweimal seine Grösse; im Betrieb ergab das drei Videodateien für eine
/// Fernwartung von neunzehn Sekunden. Die Grössen stammen aus den Dateien, die dabei
/// entstanden sind: 836×496, dann 860×496, dann Vollbild.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class GrowingWindow(nint handle, ScreenBox screen) : IWindowGeometrySource
{
    private int _calls;

    public IReadOnlyList<WindowBox> WindowsOf(int processId)
    {
        int call = Interlocked.Increment(ref _calls);

        // Die dritte Lage ist das maximierte Fenster samt seines unsichtbaren Anfassrahmens -
        // es ragt gemessen um dreizehn Bildpunkte ueber jeden Bildschirmrand hinaus.
        return call switch
        {
            <= 2 => [new WindowBox(handle, screen.Left + 500, screen.Top + 300, 836, 496)],
            <= 4 => [new WindowBox(handle, screen.Left + 500, screen.Top + 300, 860, 496)],
            _ => [new WindowBox(handle, screen.Left - 13, screen.Top - 13,
                                screen.Width + 26, screen.Height - 70)],
        };
    }
}

/// <summary>
/// Die echten Bildschirme dieses Rechners.
/// </summary>
/// <remarks>
/// Hier wird nichts erfunden: Die Aufzeichnung läuft gegen ein echtes Fenster auf einem echten
/// Bildschirm, also muss auch die Bildschirmlage die echte sein. Nachgebildet wird nur die
/// Aufzählung — und auch die nur, damit der Testläufer seine eigenen Fenster nicht mitbringt.
/// </remarks>
[SupportedOSPlatform("windows5.0")]
internal sealed class FixedScreens : IScreenSource
{
    private readonly IReadOnlyList<ScreenInfo> _screens = new Win32ScreenSource().Screens();

    public IReadOnlyList<ScreenInfo> Screens() => _screens;
}

/// <summary>Vorlagen für die Testfälle.</summary>
internal static class Sample
{
    /// <summary>Eine gültige Konfiguration, wie sie geprüft durch die Anwendung geht.</summary>
    public static AppConfig Config() => new()
    {
        Tanss = new TanssSection
        {
            BaseUrl = "https://tanss.kunde.de/backend",
            EmployeeId = 1,
        },
        Monitoring =
        [
            new MonitoringEntry
            {
                Key = "mstsc",
                RemoteSupportTypeId = 1003,
            },
        ],
    };

    /// <summary>Eine Nutzlast, wie sie in die Warteschlange geht.</summary>
    public static RemoteSupportWrite Upload(string id) => new()
    {
        TypeId = 1003,
        EmployeeId = 1,
        StartTime = 1_757_000_000,
        EndTime = 1_757_000_600,
        RemoteMaintenanceId = id,
        Comment = "Microsoft Remotedesktop: srv-test01",
        DeviceName = "srv-test01",
    };

    /// <summary>Eine Sitzung, wie die Beobachtung sie meldet.</summary>
    public static SessionSnapshot Session(nint window, string? id = null) => new()
    {
        SessionId = id ?? Guid.NewGuid().ToString(),
        ProfileKey = "mstsc",
        ProfileName = "Microsoft Remotedesktop",
        Destination = "srv-test01",
        ProcessId = Environment.ProcessId,
        WindowHandle = window,
        StartedAt = DateTimeOffset.Now,
        RemoteSupportTypeId = 1003,
        Comment = "Microsoft Remotedesktop: srv-test01",
    };
}
