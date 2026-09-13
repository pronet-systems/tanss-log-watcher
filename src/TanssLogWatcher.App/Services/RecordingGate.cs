using System.IO;
using System.Runtime.Versioning;
using TanssLogWatcher.Recording;
using TanssLogWatcher.Storage.Config;

namespace TanssLogWatcher.App.Services;

/// <summary>
/// Die Torwache: Darf aufgezeichnet werden, und ist noch Platz dafür?
/// </summary>
/// <remarks>
/// <para><b>Beide Fragen an einer Stelle und nicht an dreien.</b> Die Einstellungsseite, der
/// Dienst und die Anzeige sollen denselben Satz sagen. Ein Werkzeug, das an zwei Stellen
/// verschiedene Gründe nennt, wird an beiden nicht geglaubt.</para>
///
/// <para><b>Ohne die Aufnahme selbst.</b> Diese Klasse kommt ohne Windows-Version 19041 aus —
/// anders als alles, was tatsächlich aufnimmt. Deshalb kann die Einstellungsseite sie fragen,
/// auch auf einem Rechner, auf dem es gar keine Bildschirmaufnahme gibt: Dort ist die Antwort
/// eben, dass es sie nicht gibt.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class RecordingGate
{
    /// <summary>
    /// Sagt in einem Satz, warum gerade nicht aufgezeichnet wird — oder <c>null</c>, wenn
    /// aufgezeichnet werden darf.
    /// </summary>
    /// <param name="recording">Der Konfigurationsabschnitt.</param>
    /// <returns>Der Grund, oder <c>null</c>.</returns>
    public static string? WhyNotRecording(RecordingSection recording)
    {
        ArgumentNullException.ThrowIfNull(recording);

        // Was an der Konfiguration liegt, beantwortet der Abschnitt selbst - dort steht die
        // Entscheidung, und der Grund gehoert daneben. Hier kommt nur dazu, was diese Stelle
        // ueberhaupt wissen kann: ob der Rechner aufnehmen kann.
        if (recording.UnusableReason is { } reason)
        {
            return reason;
        }

        if (!CaptureSupport.IsAvailable())
        {
            return "Dieser Rechner hat keine Bildschirmaufnahme: Windows.Graphics.Capture "
                + "meldet sich als nicht verfügbar. Gebraucht wird mindestens "
                + CaptureSupport.MinimumWindows + ", und eine Sitzung mit Bildschirm.";
        }

        return null;
    }

    /// <summary>
    /// Wie viele Megabyte auf dem Datenträger eines Ordners noch frei sind.
    /// </summary>
    /// <remarks>
    /// Lässt sich das nicht feststellen — ein Netzpfad, ein entferntes Laufwerk —, gilt
    /// „reichlich“. Die Aufzeichnung aus einer nicht beantwortbaren Frage heraus zu verweigern
    /// wäre die falsche Vorsicht: Der Platz wird bei jedem Takt erneut gefragt, und eine volle
    /// Platte meldet sich spätestens beim Schreiben.
    /// </remarks>
    /// <param name="root">Der Ordner, auf dessen Datenträger es ankommt.</param>
    public static long FreeMegabytes(string root)
    {
        try
        {
            string? drive = Path.GetPathRoot(Path.GetFullPath(root));

            return string.IsNullOrEmpty(drive)
                ? long.MaxValue
                : new DriveInfo(drive).AvailableFreeSpace / 1024 / 1024;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException
                                      or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }
}
