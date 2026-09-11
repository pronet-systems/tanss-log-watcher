using System.Globalization;
using System.Text;

namespace TanssLogWatcher.Storage;

/// <summary>
/// Schreibt Dateien so, dass ein Abbruch niemals einen halben Stand hinterlässt.
/// </summary>
/// <remarks>
/// <para>Das Verfahren ist immer dasselbe: in eine Nachbardatei <b>im selben Verzeichnis</b>
/// schreiben, auf die Platte durchschreiben, dann umbenennen. Das Umbenennen innerhalb eines
/// Datenträgers ist die einzige Operation, die das Dateisystem als Ganzes durchführt oder
/// gar nicht — danach steht entweder der alte oder der neue Inhalt da, nie eine Mischung.</para>
///
/// <para><b>Das Nachbarverzeichnis ist keine Bequemlichkeit, sondern Bedingung.</b> Läge die
/// temporäre Datei unter <c>%TEMP%</c> und damit möglicherweise auf einem anderen Datenträger,
/// würde aus dem Umbenennen ein Kopieren — und genau dabei kann wieder ein halber Stand
/// entstehen.</para>
///
/// <para><b>Durchschreiben ist ebenfalls Bedingung.</b> Ohne <c>Flush(true)</c> quittiert
/// Windows den Schreibvorgang aus dem Cache. Nach einem Stromausfall steht dann der neue
/// Dateiname über altem oder leerem Inhalt — der schlimmste Fall, weil er wie ein
/// erfolgreicher Schreibvorgang aussieht.</para>
/// </remarks>
public static class AtomicFile
{
    /// <summary>Endung der Zwischendatei. Der Punkt am Anfang hält sie aus Dateilisten heraus.</summary>
    private const string TempPrefix = ".tmp-";

    /// <summary>Schreibt <paramref name="content"/> als UTF-8 ohne Byte-Order-Mark.</summary>
    public static void WriteText(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Write(path, stream =>
        {
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        });
    }

    /// <summary>Schreibt rohe Bytes, etwa den DPAPI-Umschlag des Tokens.</summary>
    public static void WriteBytes(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Write(path, stream => stream.Write(content, 0, content.Length));
    }

    /// <summary>
    /// Führt <paramref name="writer"/> gegen eine Zwischendatei aus und benennt sie danach
    /// auf <paramref name="path"/> um.
    /// </summary>
    /// <remarks>
    /// Wirft <paramref name="writer"/>, bleibt die Zieldatei unangetastet und die Zwischendatei
    /// wird entfernt. Genau dieser Weg ist die Prüfstelle für „entweder alt oder neu“.
    /// </remarks>
    public static void Write(string path, Action<Stream> writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(writer);

        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)
            ?? throw new StorageException($"{path} hat kein Verzeichnis, in das geschrieben werden könnte.");
        Directory.CreateDirectory(directory);

        string temp = Path.Combine(directory, TempPrefix + Path.GetFileName(full) + "-"
            + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        try
        {
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writer(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Legt eine Sicherung des bisherigen Standes an. Fehlt die Quelle, geschieht nichts.
    /// </summary>
    /// <remarks>
    /// Die Sicherung entsteht selbst wieder atomar. Eine halb geschriebene <c>.bak</c> wäre
    /// wertlos — und sie entstünde ausgerechnet in dem Moment, in dem sie gebraucht wird.
    /// </remarks>
    public static bool Backup(string path, string backupPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);

        if (!File.Exists(path))
        {
            return false;
        }

        byte[] previous = File.ReadAllBytes(path);
        WriteBytes(backupPath, previous);
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Eine liegengebliebene Zwischendatei ist unschoen, aber harmlos: sie traegt
            // einen eigenen Namen und wird nie gelesen. Den echten Fehler verdecken darf
            // sie nicht.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
