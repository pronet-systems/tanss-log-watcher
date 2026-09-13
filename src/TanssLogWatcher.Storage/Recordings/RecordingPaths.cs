using System.Globalization;
using TanssLogWatcher.Storage.Config;

namespace TanssLogWatcher.Storage.Recordings;

/// <summary>
/// Wo eine Aufzeichnung liegt und wie sie heisst.
/// </summary>
/// <remarks>
/// <para><b>Im Pfad steht kein Kundenname.</b> Es wäre bequem, die Gegenstelle in den
/// Ordnernamen zu schreiben — <c>srv-dc01</c> findet sich leichter als <c>7f3a1c94</c>. Ein
/// Dateipfad wandert aber überallhin: in Sicherungsläufe, in Suchindizes, in die Liste der
/// zuletzt geöffneten Dateien, in jede Fehlermeldung. Wer wissen will, zu wem eine Aufzeichnung
/// gehört, findet es in der Begleitdatei — dort, wo es hingehört und wo es mit der Aufzeichnung
/// gelöscht wird.</para>
///
/// <para><b>Ein Ordner je Tag.</b> Nicht je Monat, weil ein Tag mit dreissig Fernwartungen
/// üblich ist und ein Ordner mit neunhundert Einträgen keine Hilfe mehr. Nicht je Sitzung ohne
/// Tag darüber, weil der Ordner darüber sonst irgendwann zehntausend Einträge hätte.</para>
///
/// <para><b>Rein rechnend.</b> Hier wird kein Ordner angelegt und keine Datei angefasst — das
/// macht, wer schreibt. So lässt sich die Benennung ohne Dateisystem prüfen.</para>
/// </remarks>
public static class RecordingPaths
{
    /// <summary>Der Name der Begleitdatei in jedem Sitzungsordner.</summary>
    public const string ManifestName = "sitzung.json";

    /// <summary>
    /// Die Wurzel, unter der alle Aufzeichnungen liegen.
    /// </summary>
    /// <remarks>
    /// Ohne eigene Angabe ein Ordner neben dem übrigen Zustand. Das ist die richtige
    /// Voreinstellung und selten die richtige Einstellung: Videodateien gehören meist nicht auf
    /// dieselbe Platte wie das Betriebssystem.
    /// </remarks>
    /// <param name="recording">Der Konfigurationsabschnitt.</param>
    public static string Root(RecordingSection recording)
    {
        ArgumentNullException.ThrowIfNull(recording);

        return string.IsNullOrWhiteSpace(recording.Directory)
            ? Path.Combine(StoragePaths.StateDirectory, "Aufzeichnungen")
            : recording.Directory;
    }

    /// <summary>
    /// Der Ordner einer Sitzung, relativ zur Wurzel.
    /// </summary>
    /// <param name="startedAt">Wann die Sitzung begann; bestimmt Tag und Uhrzeit im Namen.</param>
    /// <param name="sessionId">Die Sitzungskennung.</param>
    public static string FolderFor(DateTimeOffset startedAt, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        DateTimeOffset local = startedAt.ToLocalTime();

        string day = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string time = local.ToString("HHmm", CultureInfo.InvariantCulture);

        return Path.Combine(day, $"{time}-{Short(sessionId)}");
    }

    /// <summary>Der Name der Videodatei in jedem Sitzungsordner.</summary>
    /// <remarks>
    /// Derselbe Stamm wie die Begleitdatei <c>sitzung.json</c> — das zeigt den Zusammenhang
    /// ohne Erklärung. Früher hiessen die Dateien <c>teil-01.mp4</c>, <c>teil-02.mp4</c> und so
    /// fort; seit eine Sitzung genau eine Datei ergibt, wäre „Teil“ eine Lüge.
    /// </remarks>
    public const string VideoName = "sitzung.mp4";

    /// <summary>
    /// Die Videodatei einer Sitzung, relativ zur Wurzel.
    /// </summary>
    /// <param name="startedAt">Wann die Sitzung begann.</param>
    /// <param name="sessionId">Die Sitzungskennung.</param>
    public static string VideoFor(DateTimeOffset startedAt, string sessionId) =>
        Path.Combine(FolderFor(startedAt, sessionId), VideoName);

    /// <summary>
    /// Prüft, ob ein Pfad tatsächlich unterhalb der Wurzel liegt.
    /// </summary>
    /// <remarks>
    /// <b>Der Riegel vor dem Löschen.</b> Gelöscht wird ausschliesslich, was unterhalb der
    /// eingestellten Wurzel liegt. Ohne diese Prüfung genügte ein <c>..</c> in einem Eintrag
    /// der Buchführung — und ein Werkzeug, das Dateien löscht, muss sich darüber im Klaren
    /// sein, welche.
    /// </remarks>
    /// <param name="root">Die Wurzel.</param>
    /// <param name="candidate">Der zu prüfende, vollständige Pfad.</param>
    public static bool IsInside(string root, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullCandidate = Path.GetFullPath(candidate);

        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar,
                                        StringComparison.OrdinalIgnoreCase);
    }

    // Acht Zeichen reichen, um zwei Sitzungen desselben Tages auseinanderzuhalten, und sie
    // passen in einen Pfad. Die vollstaendige Kennung steht in der Begleitdatei.
    private static string Short(string sessionId)
    {
        string cleaned = new([.. sessionId.Where(char.IsLetterOrDigit)]);

        return cleaned.Length <= 8 ? cleaned : cleaned[..8];
    }
}
