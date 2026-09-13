using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace TanssLogWatcher.Recording;

/// <summary>
/// Die echte Fensteraufzählung über Win32.
/// </summary>
/// <remarks>
/// <para><b>Sie entscheidet nichts.</b> Sie zählt auf und misst; ob daraus eine Aufzeichnung
/// wird, entscheidet der <see cref="RecordingDirector"/>. Deshalb steht hier auch keine
/// Schonfrist, keine Leinwand und kein Segmentwechsel.</para>
///
/// <para><b>Vier Prüfungen, und jede hat einen Grund.</b> Unsichtbare Fenster zeigen nichts.
/// Minimierte haben eine Geometrie, aber sie ist die des Symbols und nicht die des Inhalts.
/// Von der Fensterverwaltung versteckte — eine App auf einem anderen virtuellen Desktop, ein
/// UWP-Fenster im Ruhezustand — melden eine Fläche und liefern trotzdem kein Bild; erkennbar
/// sind sie allein über <c>DWMWA_CLOAKED</c>. Und Fenster ohne Fläche fallen weg, weil ein
/// Rechteck ohne Inhalt ein schwarzer Fleck auf der Leinwand wäre.</para>
///
/// <para><b>Besessene Fenster bleiben drin.</b> Das ist der Unterschied zur Erkennung: Ein
/// Anmeldedialog gehört zur Arbeit, auch wenn er keinen eigenen Eintrag in der Taskleiste hat.
/// Gemessen wurde, dass die Fensteraufnahme ihn nicht von selbst mitliefert — er muss einzeln
/// aufgenommen werden, oder er fehlt.</para>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
public sealed class Win32WindowGeometrySource : IWindowGeometrySource
{
    /// <inheritdoc />
    public IReadOnlyList<WindowBox> WindowsOf(int processId)
    {
        if (processId <= 0)
        {
            return [];
        }

        List<WindowBox> found = [];

        // EnumWindows statt EnumDesktopWindows: Letzteres braucht ein Desktophandle und liefert
        // auf einer Sitzung mit gesperrtem Bildschirm nichts - genau dann laeuft die
        // Fernwartung aber weiter.
        _ = PInvoke.EnumWindows((hwnd, _) =>
        {
            if (Belongs(hwnd, processId) && Measure(hwnd) is { } box)
            {
                found.Add(box);
            }

            return true;
        },
        0);

        return found;
    }

    private static unsafe bool Belongs(HWND hwnd, int processId)
    {
        uint owner = 0;
        _ = PInvoke.GetWindowThreadProcessId(hwnd, &owner);
        return owner == (uint)processId;
    }

    private static unsafe WindowBox? Measure(HWND hwnd)
    {
        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd))
        {
            return null;
        }

        if (IsCloaked(hwnd))
        {
            return null;
        }

        if (!PInvoke.GetWindowRect(hwnd, out RECT rect))
        {
            return null;
        }

        int width = rect.right - rect.left;
        int height = rect.bottom - rect.top;

        return width > 0 && height > 0
            ? new WindowBox((nint)hwnd.Value, rect.left, rect.top, width, height)
            : null;
    }

    /// <summary>
    /// Ist das Fenster von der Fensterverwaltung versteckt?
    /// </summary>
    /// <remarks>
    /// Ein verstecktes Fenster meldet eine ordentliche Geometrie und liefert trotzdem kein
    /// Bild — eine Anwendung auf einem anderen virtuellen Desktop etwa. Ohne diese Prüfung
    /// stünde es als schwarzes Rechteck auf der Leinwand und die Aufzeichnung behauptete, dort
    /// sei etwas gewesen.
    /// <para>Schlägt die Abfrage fehl, gilt das Fenster als sichtbar: Im Zweifel lieber
    /// aufzeichnen als eine Lücke hinterlassen.</para>
    /// </remarks>
    private static unsafe bool IsCloaked(HWND hwnd)
    {
        uint cloaked = 0;

        HRESULT result = PInvoke.DwmGetWindowAttribute(
            hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));

        return result.Succeeded && cloaked != 0;
    }
}
