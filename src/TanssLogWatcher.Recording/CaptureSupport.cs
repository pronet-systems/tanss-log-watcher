using System.Runtime.Versioning;

namespace TanssLogWatcher.Recording;

/// <summary>
/// Die eine Frage, die vor jeder Aufzeichnung zu beantworten ist: Geht das auf diesem Rechner?
/// </summary>
/// <remarks>
/// <para><b>Gefragt und nicht vorausgesetzt.</b> Windows.Graphics.Capture gibt es erst ab
/// Windows 10 Build 19041, und auch dort nicht in jeder Sitzung — eine Dienstsitzung ohne
/// Bildschirm hat keine. Die Einstellungsseite und der Dienst sollen das sagen können, statt
/// die Aufzeichnung stumm scheitern zu lassen.</para>
/// <para>Die Frage steht hier und nicht bei <c>WindowCapture</c>, weil jene Klasse die
/// Grafikinnereien trägt und deshalb intern ist. Dies hier ist die einzige Auskunft, die das
/// Aufnahmeprojekt nach aussen gibt, ohne dass etwas aufgenommen wird.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class CaptureSupport
{
    /// <summary>Steht die Bildschirmaufnahme auf diesem Rechner zur Verfügung?</summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return false;
        }

        return WindowCapture.IsSupported();
    }

    /// <summary>Die kleinste Windows-Version, die aufnehmen kann — für eine Meldung.</summary>
    public const string MinimumWindows = "Windows 10, Version 2004 (Build 19041)";
}
