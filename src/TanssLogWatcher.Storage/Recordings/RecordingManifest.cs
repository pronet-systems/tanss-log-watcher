using System.Text.Json;
using System.Text.Json.Serialization;

namespace TanssLogWatcher.Storage.Recordings;

/// <summary>
/// Die Begleitdatei neben den Videodateien einer Sitzung.
/// </summary>
/// <remarks>
/// <para><b>Sie ist die Umsetzung von Hausregel 2 für dieses Erzeugnis.</b> Das Video ist zwanzig
/// Minuten lang, die Sitzung dauerte fünfundvierzig. Ohne diese Datei sähe das aus wie eine
/// abgebrochene Aufzeichnung; mit ihr steht da, welche fünfundzwanzig Minuten Pause waren und
/// warum. Eine Aufzeichnung, die ihre eigenen Lücken nicht erklärt, taugt im Streitfall
/// nichts.</para>
///
/// <para><b>Sie trägt, was nicht in den Pfad gehört.</b> Gegenstelle, Arbeitsplatz und Techniker
/// stehen hier und nicht im Ordnernamen: Ein Dateipfad wandert in Sicherungsläufe, Suchindizes
/// und Fehlermeldungen — diese Datei wird mit der Aufzeichnung zusammen gelöscht.</para>
///
/// <para><b>Sie nennt das Löschdatum.</b> Wer eine Aufzeichnung in der Hand hat, soll ohne
/// Rückfrage sehen können, bis wann sie aufbewahrt wird.</para>
/// </remarks>
public sealed record RecordingManifest
{
    /// <summary>Die Sitzungskennung; zugleich die Kennung der Fernwartung bei TANSS.</summary>
    public required string SessionId { get; init; }

    /// <summary>Die Gegenstelle — Rechnername, Adresse oder Kennung.</summary>
    public string Destination { get; init; } = string.Empty;

    /// <summary>Die beobachtete Anwendung, etwa <c>Microsoft Remotedesktop</c>.</summary>
    public string Application { get; init; } = string.Empty;

    /// <summary>Der Arbeitsplatz, an dem aufgezeichnet wurde.</summary>
    public string Workstation { get; init; } = string.Empty;

    /// <summary>Der Techniker, sofern bekannt.</summary>
    public string Technician { get; init; } = string.Empty;

    /// <summary>Wann die Sitzung begann.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Wann sie endete.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>
    /// Die aufgezeichnete Zeit in Sekunden — ohne die Pausen.
    /// </summary>
    /// <remarks>
    /// Getrennt von <see cref="StartedAt"/> und <see cref="EndedAt"/> ausgewiesen, weil sie
    /// etwas anderes ist. Die Differenz der beiden ist die Dauer der Sitzung; dieser Wert ist
    /// die Dauer des Videos.
    /// </remarks>
    public long RecordedSeconds { get; init; }

    /// <summary>Die Pausen, in der Reihenfolge ihres Auftretens.</summary>
    public IReadOnlyList<ManifestPause> Pauses { get; init; } = [];

    /// <summary>Die Dateien dieser Sitzung, in der Reihenfolge ihrer Entstehung.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Die Abmessungen des Bildes.</summary>
    public string Canvas { get; init; } = string.Empty;

    /// <summary>Bilder je Sekunde.</summary>
    public int FramesPerSecond { get; init; }

    /// <summary>Das Format, mit dem kodiert wurde.</summary>
    public string Codec { get; init; } = "H.264";

    /// <summary>Bis wann diese Aufzeichnung aufbewahrt wird.</summary>
    public required DateTimeOffset DeleteAfter { get; init; }

    /// <summary>
    /// Die Rechtsgrundlage, auf die sich die Aufzeichnung stützt.
    /// </summary>
    /// <remarks>
    /// Sie wandert mit, weil sie zur Aufzeichnung gehört: Wer sie Monate später in der Hand
    /// hält, soll nicht in einer Konfigurationsdatei nachsehen müssen, worauf sie sich stützte.
    /// </remarks>
    public string LegalBasis { get; init; } = string.Empty;

    /// <summary>Der Beleg zur Rechtsgrundlage.</summary>
    public string LegalReference { get; init; } = string.Empty;

    /// <summary>Schreibt die Begleitdatei.</summary>
    /// <param name="path">Der vollständige Pfad der Datei.</param>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        AtomicFile.WriteText(path, JsonSerializer.Serialize(this, ManifestJson.Options));
    }

    /// <summary>Liest eine Begleitdatei. <c>null</c>, wenn sie fehlt oder unlesbar ist.</summary>
    /// <param name="path">Der vollständige Pfad der Datei.</param>
    public static RecordingManifest? Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RecordingManifest>(
                File.ReadAllText(path), ManifestJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Eine unlesbare Begleitdatei ist kein Grund, die Videodatei fuer wertlos zu
            // halten. Der Aufrufer zeigt dann weniger an - mehr nicht.
            return null;
        }
    }
}

/// <summary>Ein Abschnitt, in dem nicht aufgezeichnet wurde.</summary>
/// <param name="StartedAt">Wann die Pause begann.</param>
/// <param name="Seconds">Wie lange sie dauerte.</param>
public readonly record struct ManifestPause(DateTimeOffset StartedAt, long Seconds);

/// <summary>Die Schreibweise der Begleitdatei.</summary>
/// <remarks>
/// Eingerückt und mit Unterstrichen, wie <c>config.json</c>: Beide Dateien liest im Zweifel ein
/// Mensch, und beide sollen dabei gleich aussehen.
/// </remarks>
internal static class ManifestJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
