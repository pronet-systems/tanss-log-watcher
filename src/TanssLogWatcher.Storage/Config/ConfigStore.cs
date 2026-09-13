using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace TanssLogWatcher.Storage.Config;

/// <summary>
/// Die Konfiguration unter <c>%APPDATA%\ProNet Systems\TanssLogWatcher\config.json</c>.
/// </summary>
/// <remarks>
/// <para>Geschrieben wird ausschließlich über <see cref="AtomicFile"/>. Die Zieldatei wird
/// niemals geöffnet und teilweise überschrieben — ein Absturz mitten im Schreiben ließe sonst
/// eine halbe Datei zurück, und der nächste Start fände weder die alte noch die neue
/// Konfiguration vor.</para>
///
/// <para>Der Einrichtungsassistent legt seinen Zwischenstand daneben als
/// <c>config.partial.json</c> ab. Das ist kein zweiter Konfigurationsort: Er wird nie
/// geladen, um den Dienst zu betreiben, sondern nur, um eine abgebrochene Einrichtung
/// fortzusetzen.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ConfigStore : IConfigStore
{
    private const string PartialSuffix = ".partial.json";

    /// <summary>Öffnet eine Konfiguration an einem beliebigen Pfad.</summary>
    public ConfigStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);

        string directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new ConfigException($"{path} hat kein Verzeichnis.");
        string stem = System.IO.Path.GetFileNameWithoutExtension(Path);
        PartialPath = System.IO.Path.Combine(directory, stem + PartialSuffix);
    }

    /// <summary>Öffnet die Konfiguration am vorgesehenen Ort im Benutzerprofil.</summary>
    public static ConfigStore Default() => new(StoragePaths.ConfigFile);

    /// <inheritdoc/>
    public string Path { get; }

    /// <inheritdoc/>
    public string PartialPath { get; }

    /// <inheritdoc/>
    public bool Exists() => File.Exists(Path);

    /// <inheritdoc/>
    public AppConfig Load()
    {
        if (!Exists())
        {
            throw new ConfigException(
                $"Keine Konfiguration unter {Path}. "
                + "Einrichtung starten mit: tanss-logwatch setup");
        }

        string raw;
        try
        {
            raw = File.ReadAllText(Path, Encoding.UTF8);
        }
        catch (IOException exception)
        {
            throw new ConfigException(
                $"{Path} ließ sich nicht lesen: {exception.Message}", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ConfigException(
                $"{Path} ließ sich nicht lesen: {exception.Message}. Läuft das Werkzeug "
                + "unter demselben Windows-Benutzer wie bei der Einrichtung?", exception);
        }

        AppConfig config = Parse(raw, Path);
        ConfigValidator.Validate(config, Path);
        return config;
    }

    /// <summary>
    /// Liest eine Konfiguration aus einer Zeichenkette — ohne Dateizugriff, aber mit
    /// denselben Meldungen.
    /// </summary>
    /// <param name="raw">Der JSON-Text.</param>
    /// <param name="origin">Herkunft für die Fehlermeldung, üblicherweise ein Dateipfad.</param>
    public static AppConfig Parse(string raw, string? origin = null)
    {
        string where = string.IsNullOrWhiteSpace(origin) ? "Die Konfiguration" : origin;

        try
        {
            return JsonSerializer.Deserialize<AppConfig>(WithoutRetiredKeys(raw), ConfigJson.Options)
                ?? throw new ConfigException($"{where} enthält nur „null“ statt einer Konfiguration.");
        }
        catch (JsonException exception)
        {
            throw new ConfigException(Explain(where, raw, exception), exception);
        }
    }

    /// <summary>
    /// Die Schlüssel, die es einmal gab und die heute nichts mehr bedeuten.
    /// </summary>
    /// <remarks>
    /// <para>Je Eintrag der Abschnitt und der Schlüssel darin. Die Liste ist absichtlich kurz
    /// und wächst nur, wenn wirklich eine Einstellung stillgelegt wird.</para>
    /// </remarks>
    private static readonly (string Section, string Key)[] RetiredKeys =
    [
        // Es gab einmal einen Zeittakt, nach dem eine neue Videodatei begann: Eine lange
        // Aufzeichnung ohne Abschluss hatte keinen Index und war damit unbrauchbar. Seit die
        // Datei bruchstueckweise mit vorangestelltem Index geschrieben wird, uebersteht sie
        // einen Absturz auch ohne Abschluss - eine Sitzung ergibt genau eine Datei, und der
        // Takt ist ersatzlos entfallen.
        ("recording", "segment_minutes"),
    ];

    /// <summary>
    /// Räumt stillgelegte Schlüssel aus dem gelesenen Text, bevor er abgebildet wird.
    /// </summary>
    /// <remarks>
    /// <para><b>Warum das sein muss.</b> Die Abschnitte lehnen unbekannte Felder ab
    /// (<c>JsonUnmappedMemberHandling.Disallow</c>), und das ist richtig so: Wer
    /// <c>exclude_ip_adresses</c> schreibt, hält seine Ausschlussliste für aktiv, während sie
    /// stillschweigend ins Leere geht. Ein stillgelegter Schlüssel würde von derselben
    /// Strenge getroffen — eine bestehende <c>config.json</c> liefe nach dem Update als
    /// „nicht eingerichtet“ auf, und dann würde auch keine Fernwartung mehr gebucht.</para>
    ///
    /// <para><b>Warum nicht einfach eine tote Eigenschaft stehenlassen.</b> Weil sie dann
    /// bleibt. Eine Einstellung, die im Schema steht und nichts tut, wird gelesen, weitergereicht
    /// und irgendwann wieder von jemandem gesetzt. Hier verschwindet der Schlüssel beim ersten
    /// Speichern von selbst, und das Schema ist von ihm frei.</para>
    ///
    /// <para><b>Die Strenge bleibt, wo sie hingehört:</b> Geräumt wird genau die Liste in
    /// <see cref="RetiredKeys"/> und kein Feld, das bloß unbekannt ist. Ein Tippfehler fällt
    /// weiterhin auf.</para>
    ///
    /// <para>Ist der Text kein Objekt oder gar kein gültiges JSON, wird er <b>unverändert</b>
    /// weitergereicht: Die Fehlermeldung soll aus der Abbildung kommen, wo sie Zeile und Spalte
    /// nennt, und nicht aus dieser Vorstufe.</para>
    ///
    /// <para><b>Kommentare gehen dabei verloren</b> — aber nur in der Abschrift, die zum
    /// Abbilden weitergereicht wird, und nur dann, wenn wirklich etwas zu räumen war. Die
    /// Datei auf der Platte rührt diese Methode nicht an; geschrieben wird sie erst beim
    /// nächsten <see cref="Save"/>, und der schreibt ohnehin neu.</para>
    /// </remarks>
    /// <param name="raw">Der gelesene Text.</param>
    /// <returns>Derselbe Text ohne die stillgelegten Schlüssel.</returns>
    private static string WithoutRetiredKeys(string raw)
    {
        JsonNode? root;

        try
        {
            // MIT denselben Zugestaendnissen wie der Serialisierer: Die Konfiguration darf
            // Kommentare und nachgestellte Kommata enthalten - die mitgelieferte
            // config.example.json macht von beidem Gebrauch. Wer hier mit den Vorgaben laese,
            // fiele bei jeder kommentierten Datei auf die Nase und liesse den stillgelegten
            // Schluessel stehen. Genau das ist beim ersten Anlauf passiert.
            root = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException)
        {
            return raw;
        }

        if (root is not JsonObject document)
        {
            return raw;
        }

        bool changed = false;

        foreach ((string section, string key) in RetiredKeys)
        {
            if (document[section] is JsonObject block && block.Remove(key))
            {
                changed = true;
            }
        }

        return changed ? document.ToJsonString(ConfigJson.Options) : raw;
    }

    /// <inheritdoc/>
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ConfigValidator.Validate(config, Path);
        AtomicFile.WriteText(Path,
            JsonSerializer.Serialize(config, ConfigJson.Options) + Environment.NewLine);
    }

    /// <inheritdoc/>
    public void SavePartial(JsonObject draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        AtomicFile.WriteText(PartialPath,
            draft.ToJsonString(ConfigJson.Options) + Environment.NewLine);
    }

    /// <inheritdoc/>
    public JsonObject? LoadPartial()
    {
        if (!File.Exists(PartialPath))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(PartialPath, Encoding.UTF8)) as JsonObject;
        }
        catch (JsonException)
        {
            // Ein unlesbarer Zwischenstand ist kein Grund, die Einrichtung zu verweigern -
            // er ist eine Bequemlichkeit, kein Datenbestand. Der Assistent faengt dann
            // einfach von vorn an.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public void ClearPartial()
    {
        try
        {
            File.Delete(PartialPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Übersetzt eine <see cref="JsonException"/> in eine Meldung, die sagt, was zu tun ist.
    /// </summary>
    /// <remarks>
    /// <para>Der Sonderfall ist das unbekannte Feld: Es ist der einzige Ladefehler, bei dem
    /// die Datei formal in Ordnung ist und der Benutzer trotzdem etwas gemeint hat, was nie
    /// wirken würde. Genau dafür lehnt das Modell unbekannte Felder ab, und genau deshalb
    /// muss die Meldung erklären, dass das Absicht ist.</para>
    ///
    /// <para><b>Die Einordnung hängt bewusst nicht am Wortlaut der Framework-Meldung.</b>
    /// Ein Vergleich mit „could not be mapped“ griffe auf einem Rechner mit deutschen
    /// .NET-Ressourcen nicht mehr, und gerade die Erklärung zum unbekannten Feld fiele
    /// stumm weg. Stattdessen wird der Fall nachgestellt: Ist die Datei syntaktisch in
    /// Ordnung und gelingt das Lesen, sobald unbekannte Felder übergangen werden, dann war
    /// genau das der Fehler.</para>
    /// </remarks>
    private static string Explain(string where, string raw, JsonException exception)
    {
        string position = exception.LineNumber is { } line
            ? string.Create(CultureInfo.InvariantCulture,
                $" (Zeile {line + 1}, Spalte {(exception.BytePositionInLine ?? 0) + 1})")
            : string.Empty;

        string field = string.IsNullOrEmpty(exception.Path) ? string.Empty : $" bei {exception.Path}";

        if (!IsWellFormed(raw))
        {
            return $"{where} ist kein gültiges JSON{field}{position}: {exception.Message}";
        }

        if (ReadsWithoutUnknownFields(raw))
        {
            return $"{where} enthält ein unbekanntes Feld{field}{position}. "
                + "Unbekannte Felder werden abgelehnt statt übergangen: Eine vertippte "
                + "Einstellung, die stillschweigend wirkungslos bleibt, ist gefährlicher "
                + "als ein Ladefehler — sie sieht aus wie eine gesetzte Regel. "
                + $"Ursprüngliche Meldung: {exception.Message}";
        }

        return $"{where} ist gültiges JSON, passt aber nicht zum erwarteten Aufbau{field}"
            + $"{position}: Dort fehlt ein Pflichtfeld oder ein Wert hat den falschen Typ. "
            + "Zu berichtigen ist damit diese eine Stelle, nicht die ganze Datei. "
            + $"Ursprüngliche Meldung: {exception.Message}";
    }

    /// <summary>Ist der Text überhaupt JSON — mit denselben Zugeständnissen wie beim Laden?</summary>
    private static bool IsWellFormed(string raw)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                // Wie ConfigJson: Eine von Hand stillgelegte Zeile ist kein Syntaxfehler.
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Ließe sich die Datei lesen, wenn unbekannte Felder übergangen würden?</summary>
    private static bool ReadsWithoutUnknownFields(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(raw, Tolerant) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Dieselben Einstellungen wie beim Laden, nur dass unbekannte Felder übergangen werden.
    /// </summary>
    /// <remarks>
    /// Ausdrücklich <b>kein</b> zweiter Weg, eine Konfiguration zu laden — damit wird nichts
    /// in Betrieb genommen. Diese Version dient allein dazu, einen Ladefehler einzuordnen,
    /// ohne den Wortlaut einer Framework-Meldung zu raten. Das Feld
    /// <c>JsonUnmappedMemberHandling</c> steht als Attribut am Modell; überschreiben lässt
    /// es sich nur über den Auflöser der Typinformationen.
    /// </remarks>
    private static readonly JsonSerializerOptions Tolerant = new(ConfigJson.Options)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                // Nur Objekte tragen diese Einstellung; bei allem anderen (Listen,
                // Zeichenketten) wirft der Setzer.
                static info =>
                {
                    if (info.Kind == JsonTypeInfoKind.Object)
                    {
                        info.UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip;
                    }
                },
            },
        },
    };
}
