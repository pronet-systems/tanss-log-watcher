using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using TanssLogWatcher.Monitoring.Model;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TanssLogWatcher.Monitoring.Native;

/// <summary>
/// Die sichtbaren Fenster der obersten Ebene über <c>EnumDesktopWindows</c>.
/// </summary>
/// <remarks>
/// <para><b>Titel bleiben UTF-16.</b> Die Vorlage schickte jeden Titel durch die ANSI-Codepage des
/// Systems mit verwerfendem Rückfall und verstümmelte damit still jeden Namen mit Zeichen
/// außerhalb dieser Seite — ein Kundenname mit kyrillischen oder türkischen Buchstaben landete als
/// Bruchstück in der Fernwartung. Hier bleibt, was Windows liefert.</para>
/// <para><b>Die Länge wird erfragt, nicht geraten.</b> Die Vorlage deckelte auf 255 Zeichen und
/// schnitt längere Titel ab.</para>
/// </remarks>
public sealed class WindowSource : IWindowSource
{
    private const string ApplicationFrameHost = "ApplicationFrameHost";

    private readonly ILogger<WindowSource> logger;

    /// <summary>Erzeugt die Quelle.</summary>
    /// <param name="logger">Protokoll; <c>null</c> schaltet es ab.</param>
    public WindowSource(ILogger<WindowSource>? logger = null) =>
        this.logger = logger ?? NullLogger<WindowSource>.Instance;

    /// <inheritdoc />
    public IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        List<WindowInfo> result = [];
        Dictionary<uint, string?> processNames = [];

        BOOL Collect(HWND handle, LPARAM _)
        {
            try
            {
                CollectWindow(handle, result, processNames);
            }
            catch (Exception ex)
            {
                // Ein einzelnes Fenster, das zwischen Aufzaehlung und Abfrage verschwindet, darf
                // die Aufzaehlung nicht abbrechen.
                logger.LogDebug(ex, "Ein Fenster konnte nicht gelesen werden.");
            }

            return true;
        }

        if (!PInvoke.EnumDesktopWindows(default(HDESK), Collect, default))
        {
            // Eine unvollstaendige Fensterliste ist gefaehrlicher als gar keine: sie saehe fuer die
            // Zustandsmaschine wie das Ende aller Sitzungen aus. Deshalb wird der Durchlauf
            // abgebrochen und der bisherige Bestand unberuehrt gelassen.
            throw new InvalidOperationException("Die Fensterliste ist unvollständig.");
        }

        return result;
    }

    private static void CollectWindow(HWND handle, List<WindowInfo> result, Dictionary<uint, string?> processNames)
    {
        if (!PInvoke.IsWindowVisible(handle))
        {
            return;
        }

        string? title = ReadTitle(handle);
        if (string.IsNullOrEmpty(title))
        {
            return;
        }

        uint processId = ReadProcessId(handle);
        if (processId == 0)
        {
            return;
        }

        if (!processNames.TryGetValue(processId, out string? name))
        {
            name = TryGetProcessName(processId);
            processNames[processId] = name;
        }

        bool owned = IsOwnedWindow(handle);

        if (!string.Equals(name, ApplicationFrameHost, StringComparison.OrdinalIgnoreCase))
        {
            result.Add(new WindowInfo(title, (int)processId, handle, owned));
            return;
        }

        // Der ApplicationFrameHost ist nur die Huelle einer Store-Anwendung. Findet sich dahinter
        // keine ABWEICHENDE Kennung, ist die echte Anwendung nicht bestimmbar — dann wird das
        // Fenster verworfen. Die Vorlage gab in diesem Fall die Huellen-Kennung zurueck und ordnete
        // damit jede Store-Anwendung demselben Prozess zu.
        uint? hosted = TryGetHostedProcessId(handle, processId);
        if (hosted is { } hostedId)
        {
            result.Add(new WindowInfo(title, (int)hostedId, handle, owned));
        }
    }

    /// <summary>
    /// Besitzt ein Fenster einen Besitzer, ist es ein Dialog oder ein Werkzeugfenster und niemals
    /// das Hauptfenster seines Prozesses. Genau diese Unterscheidung trifft auch
    /// <c>Process.MainWindowHandle</c>, an dem sich die Vorlage orientierte.
    /// </summary>
    private static bool IsOwnedWindow(HWND handle) =>
        !PInvoke.GetWindow(handle, GET_WINDOW_CMD.GW_OWNER).IsNull;

    private static string? ReadTitle(HWND handle)
    {
        int length = PInvoke.GetWindowTextLength(handle);
        if (length <= 0)
        {
            return null;
        }

        // Zwei Zeichen Luft: zwischen Laengenabfrage und Auslesen kann der Titel wachsen, und
        // GetWindowTextW braucht Platz fuer die abschliessende Null.
        char[] buffer = ArrayPool<char>.Shared.Rent(length + 2);
        try
        {
            int written = PInvoke.GetWindowText(handle, buffer.AsSpan(0, length + 2));
            return written <= 0 ? null : new string(buffer, 0, written);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static unsafe uint ReadProcessId(HWND handle)
    {
        uint processId = 0;
        _ = PInvoke.GetWindowThreadProcessId(handle, &processId);
        return processId;
    }

    private static unsafe string? TryGetProcessName(uint processId)
    {
        using SafeFileHandle process = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);

        if (process.IsInvalid)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[260];
        uint size = (uint)buffer.Length;
        if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size))
        {
            return null;
        }

        ReadOnlySpan<char> path = buffer[..(int)size];
        int separator = path.LastIndexOfAny('\\', '/');
        ReadOnlySpan<char> fileName = separator >= 0 ? path[(separator + 1)..] : path;
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4].ToString()
            : fileName.ToString();
    }

    private static uint? TryGetHostedProcessId(HWND frameHandle, uint framePid)
    {
        uint? hosted = null;

        BOOL Visit(HWND child, LPARAM _)
        {
            uint childPid = ReadProcessId(child);
            if (childPid != 0 && childPid != framePid)
            {
                hosted = childPid;
                return false;
            }

            return true;
        }

        _ = PInvoke.EnumChildWindows(frameHandle, Visit, default);
        return hosted;
    }
}
