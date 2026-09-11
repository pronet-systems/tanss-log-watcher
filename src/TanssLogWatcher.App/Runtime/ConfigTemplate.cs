using System.IO;
using System.Runtime.Versioning;
using System.Text;
using TanssLogWatcher.Storage;

namespace TanssLogWatcher.App.Runtime;

/// <summary>Was das Schreiben der Vorlage ergeben hat.</summary>
/// <param name="Written">Ist eine Datei entstanden?</param>
/// <param name="Path">Der Ort, um den es ging — auch im Misserfolg, damit die Anzeige ihn nennt.</param>
/// <param name="Message">
/// Der Satz für den Techniker: was geschehen ist, warum, und was als Nächstes zu tun ist.
/// </param>
public sealed record ConfigTemplateResult(bool Written, string Path, string Message);

/// <summary>
/// Die mitgelieferte <c>config.example.json</c> und der Weg an den erwarteten Ort.
/// </summary>
/// <remarks>
/// <para><b>Die Vorlage steckt in der Programmdatei</b> und nicht bloss daneben. Eine Datei
/// neben der Anwendung fehlt genau dann, wenn sie gebraucht wird — bei einer verkürzt
/// ausgerollten Kopie, in einem Netzpfad ohne Leserecht, nach einem Virenscanner, der sie
/// aussortiert hat. Der Zustand „Nicht eingerichtet“ ist der einzige, in dem das Werkzeug
/// noch helfen kann; er darf nicht an einer fehlenden Beispieldatei scheitern. Zusätzlich
/// liegt sie in lesbarer Form neben der Anwendung, damit man sie auch ohne Start ansehen
/// kann.</para>
///
/// <para><b>Es wird niemals überschrieben.</b> Eine bestehende <c>config.json</c> ist die
/// Einrichtung eines Technikers; sie durch eine Vorlage mit <c>tanss.kunde.de</c> zu ersetzen
/// wäre der Verlust genau der Angaben, die am schwersten wiederzubeschaffen sind. Liegt dort
/// etwas, sagt das Ergebnis das und tut nichts.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ConfigTemplate
{
    /// <summary>Der Name der eingebetteten Ressource.</summary>
    private const string ResourceName = "TanssLogWatcher.App.config.example.json";

    /// <summary>Dateiname der Vorlage.</summary>
    public const string FileName = "config.example.json";

    /// <summary>Der Ort, an dem die Konfiguration erwartet wird.</summary>
    public static string TargetPath => StoragePaths.ConfigFile;

    /// <summary>Die Vorlage neben der Anwendung, sofern sie dort liegt.</summary>
    public static string SideBySidePath =>
        Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>
    /// Liest die Vorlage.
    /// </summary>
    /// <remarks>
    /// Zuerst aus der Programmdatei; nur wenn die Einbettung beim Bau unterblieben ist, wird
    /// die Datei daneben versucht. Fehlt beides, wirft diese Stelle nicht — sie liefert einen
    /// Text, der erklärt, was fehlt. Eine Ausnahme im Zustand „Nicht eingerichtet“ wäre der
    /// eine Absturz, den dieses Werkzeug sich nicht leisten darf.
    /// </remarks>
    public static string Read()
    {
        try
        {
            using Stream? stream = typeof(ConfigTemplate).Assembly
                .GetManifestResourceStream(ResourceName);

            if (stream is not null)
            {
                using StreamReader reader = new(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
        }
        catch (IOException)
        {
            // Weiter zum Weg daneben.
        }

        try
        {
            if (File.Exists(SideBySidePath))
            {
                return File.ReadAllText(SideBySidePath, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return "// Die Vorlage ist in dieser Programmfassung weder eingebettet noch liegt sie\n"
            + "// neben der Anwendung. Das ist ein Fehler der Auslieferung, keine Frage der\n"
            + "// Einrichtung. Die Vorlage steht im Quellbestand unter\n"
            + "// src/TanssLogWatcher.App/config.example.json.\n";
    }

    /// <summary>Schreibt die Vorlage an den vorgesehenen Ort im Benutzerprofil.</summary>
    public static ConfigTemplateResult WriteToDefaultLocation() => WriteTo(TargetPath);

    /// <summary>
    /// Schreibt die Vorlage an einen Ort, ohne je etwas zu überschreiben.
    /// </summary>
    /// <param name="path">Der Zielpfad.</param>
    public static ConfigTemplateResult WriteTo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = Path.GetFullPath(path);

        if (File.Exists(full))
        {
            return new ConfigTemplateResult(false, full,
                $"Unter {full} liegt bereits eine Datei; sie wurde nicht angerührt. Das ist "
                + "Absicht: Eine bestehende Konfiguration ist die Einrichtung eines Technikers, "
                + "und eine Vorlage mit Beispieladressen darüberzuschreiben wäre der Verlust "
                + "genau der Angaben, die am schwersten wiederzubeschaffen sind. Lässt sie sich "
                + "nicht laden, nennt der Betriebszustand die Stelle — zu berichtigen ist dann "
                + "diese eine Stelle, nicht die ganze Datei.");
        }

        try
        {
            string? directory = Path.GetDirectoryName(full);
            if (directory is { Length: > 0 })
            {
                _ = Directory.CreateDirectory(directory);
            }

            // Ueber AtomicFile und nicht ueber File.WriteAllText: Ein Absturz mitten im
            // Schreiben liesse sonst eine halbe Datei zurueck - und die naechste Anzeige
            // meldete einen Syntaxfehler in einer Datei, die das Werkzeug selbst erzeugt hat.
            AtomicFile.WriteText(full, Read());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or StorageException)
        {
            return new ConfigTemplateResult(false, full,
                $"Die Vorlage liess sich nicht nach {full} schreiben. Übliche Ursachen: Das "
                + "Benutzerprofil liegt auf einem gerade nicht erreichbaren Netzlaufwerk, oder "
                + "eine Richtlinie verbietet das Schreiben dorthin. Die Datei lässt sich auch "
                + $"von Hand anlegen; die Vorlage liegt unter {SideBySidePath}. Meldung: "
                + ex.Message);
        }

        return new ConfigTemplateResult(true, full,
            $"Die Vorlage steht jetzt unter {full}. Darin sind drei Angaben zu berichtigen, "
            + "bevor etwas nach TANSS geht: tanss.base_url (die Adresse der Instanz, endend auf "
            + "/backend), tanss.employee_id (die eigene Mitarbeiter-ID) und die Kennungen unter "
            + "monitoring[].remote_support_type_id. Danach diese Anzeige neu laden. Ein Token "
            + "richtet bis auf Weiteres „tanss-logwatch token rotate“ ein.");
    }
}
