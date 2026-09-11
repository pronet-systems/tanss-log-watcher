using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using TanssLogWatcher.Monitoring.Model;
using Windows.Win32;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;

namespace TanssLogWatcher.Monitoring.Native;

/// <summary>
/// Momentaufnahme aller Prozesse über <c>CreateToolhelp32Snapshot</c>.
/// </summary>
/// <remarks>
/// <b>Eine Aufnahme je Durchlauf, keine WMI-Abfrage.</b> Die Vorlage fragte je überwachtem Prozess
/// zweimal <c>win32_process</c> ab — gemessen 172 bis 220 ms je Abfrage; bei fünf überwachten
/// Anwendungen kostete das mehr Zeit, als zwischen zwei Durchläufen vergehen sollte. Die komplette
/// Tabelle aus Toolhelp32 liefert dieselben Angaben in 16 bis 24 ms.
/// </remarks>
public sealed class ProcessSource : IProcessSource
{
    private readonly ILogger<ProcessSource> logger;

    /// <summary>Erzeugt die Quelle.</summary>
    /// <param name="logger">Protokoll; <c>null</c> schaltet es ab.</param>
    public ProcessSource(ILogger<ProcessSource>? logger = null) =>
        this.logger = logger ?? NullLogger<ProcessSource>.Instance;

    /// <inheritdoc />
    public IReadOnlyList<ProcessInfo> Snapshot()
    {
        using SafeFileHandle snapshot = PInvoke.CreateToolhelp32Snapshot_SafeHandle(
            CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);

        if (snapshot.IsInvalid)
        {
            throw new InvalidOperationException(
                "Die Prozessliste ist nicht lesbar.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        PROCESSENTRY32W entry = default;
        entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>();

        List<ProcessInfo> result = [];
        if (!PInvoke.Process32FirstW(snapshot, ref entry))
        {
            return result;
        }

        do
        {
            string fileName = entry.szExeFile.AsReadOnlySpan().SliceAtNull().ToString();
            result.Add(new ProcessInfo(
                (int)entry.th32ProcessID,
                (int)entry.th32ParentProcessID,
                StripExecutableExtension(fileName),
                TryGetStartTime(entry.th32ProcessID)));
        }
        while (PInvoke.Process32NextW(snapshot, ref entry));

        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("Momentaufnahme mit {Count} Prozessen.", result.Count);
        }

        return result;
    }

    /// <summary>
    /// Die Profile nennen Prozesse ohne Erweiterung, wie <see cref="System.Diagnostics.Process"/>
    /// sie führt. Nur <c>.exe</c> fällt weg — ein Prozess namens <c>pm_fb.config</c> behielte
    /// seinen Namen.
    /// </summary>
    private static string StripExecutableExtension(string fileName) =>
        fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;

    /// <summary>
    /// Der Startzeitpunkt über <c>PROCESS_QUERY_LIMITED_INFORMATION</c>.
    /// </summary>
    /// <remarks>
    /// Dieses Recht genügt auch für Prozesse in fremdem Kontext und für erhöht laufende; das
    /// weitergehende <c>PROCESS_QUERY_INFORMATION</c> der Vorlage scheiterte dort mit
    /// „Zugriff verweigert“ und riss den ganzen Durchlauf mit. Bleibt der Zeitpunkt dennoch
    /// verschlossen, ist <c>null</c> die ehrliche Antwort.
    /// </remarks>
    private static DateTimeOffset? TryGetStartTime(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        using SafeFileHandle handle = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);

        if (handle.IsInvalid)
        {
            return null;
        }

        if (!PInvoke.GetProcessTimes(handle, out System.Runtime.InteropServices.ComTypes.FILETIME creation,
                                     out _, out _, out _))
        {
            return null;
        }

        long fileTime = ((long)(uint)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
