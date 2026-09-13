using Xunit;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Ein Test, der eine funktionierende Bildschirmaufnahme braucht.
/// </summary>
/// <remarks>
/// <para><b>Übersprungen ist nicht bestanden.</b> Auf einem Bauläufer ohne Bildschirmsitzung
/// gibt es keine Bildschirmaufnahme; ein Test, der dort still durchginge, behauptete etwas
/// Geprüftes, wo nichts geprüft wurde. Deshalb wird er ausdrücklich übersprungen — und der
/// Testbericht weist Übersprungenes getrennt aus.</para>
/// <para>Dasselbe Muster wie bei den Tests gegen eine echte TANSS-Instanz: Was die Umgebung
/// nicht hergibt, wird benannt und nicht umgangen.</para>
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
            return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                && TanssLogWatcher.Recording.WindowCapture.IsSupported();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
