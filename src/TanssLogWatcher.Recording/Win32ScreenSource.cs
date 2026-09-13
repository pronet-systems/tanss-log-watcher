using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;

namespace TanssLogWatcher.Recording;

/// <summary>
/// Die echte Bildschirmaufzählung über Win32.
/// </summary>
/// <remarks>
/// <para><b>Sie entscheidet nichts</b> — wie die Fensteraufzählung. Sie zählt auf und misst;
/// welche Bildschirme die Leinwand bilden und wann sie wandert, entscheidet der
/// <see cref="RecordingDirector"/>.</para>
///
/// <para><b>Die ganze Bildschirmfläche, nicht der Arbeitsbereich.</b> Gemessen: Die
/// Bildschirmaufnahme von Windows liefert <c>rcMonitor</c> — auf diesem Rechner 2880×1800,
/// während <c>rcWork</c> nur 2880×1704 misst, weil die Taskleiste 96 Bildpunkte belegt. Wer
/// den Arbeitsbereich als Leinwand nähme, bekäme ein Bild, das um die Höhe der Taskleiste zu
/// klein ist, und ein maximiertes Fenster ragte unten heraus.</para>
///
/// <para><b>Bildpunkte, keine Punkte.</b> Der Prozess läuft laut Anwendungsmanifest als
/// <c>PerMonitorV2</c>; Fenster- und Bildschirmkoordinaten sind damit beide echte Bildpunkte
/// und müssen nicht umgerechnet werden. Ohne diese Einstellung lieferte Windows beide
/// virtualisiert, und die Leinwand wäre auf einem skalierten Bildschirm um den Faktor der
/// Skalierung zu klein.</para>
/// </remarks>
[SupportedOSPlatform("windows5.0")]
public sealed class Win32ScreenSource : IScreenSource
{
    /// <inheritdoc />
    public IReadOnlyList<ScreenInfo> Screens()
    {
        List<ScreenInfo> found = [];

        unsafe
        {
            _ = PInvoke.EnumDisplayMonitors(default, null, (monitor, _, _, _) =>
            {
                MONITORINFO info = new() { cbSize = (uint)sizeof(MONITORINFO) };

                if (!PInvoke.GetMonitorInfo(monitor, &info))
                {
                    // Hausregel 5: Dieser eine Bildschirm faellt weg, die Aufzaehlung nicht.
                    return true;
                }

                found.Add(new ScreenInfo(
                    monitor,
                    new ScreenBox(
                        info.rcMonitor.left,
                        info.rcMonitor.top,
                        info.rcMonitor.right - info.rcMonitor.left,
                        info.rcMonitor.bottom - info.rcMonitor.top),
                    (info.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0));

                return true;
            }, 0);
        }

        return found;
    }
}
