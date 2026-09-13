namespace TanssLogWatcher.Storage.Recordings;

/// <summary>
/// Löscht, was seine Frist überschritten hat.
/// </summary>
/// <remarks>
/// <para><b>Gelöscht wird nur, was in der Buchführung steht</b> — und davon nur, was
/// tatsächlich unterhalb der eingestellten Wurzel liegt. Beide Prüfungen sind nötig: Die erste
/// schützt Dateien, die jemand von Hand in den Ordner gelegt hat; die zweite schützt alles
/// andere auf der Platte vor einem Eintrag mit einem Pfad, der dort nicht hingehört.</para>
///
/// <para><b>Gelöscht heisst gelöscht, nicht in den Papierkorb.</b> Eine Aufzeichnung, deren
/// Frist abgelaufen ist, liegt sonst weiter auf der Platte — nur an einer anderen Stelle. Das
/// wäre keine Löschung, sondern eine Umbenennung mit gutem Gewissen. Was hier nicht geht und
/// auch nicht behauptet wird: mehrfaches Überschreiben. Auf einer SSD lässt sich das nicht
/// zusichern, und eine Zusicherung, die nicht trägt, ist schlimmer als keine.</para>
///
/// <para><b>Der leere Ordner geht mit.</b> Bleibt nach der letzten Datei einer Sitzung nur noch
/// die Begleitdatei, wird auch sie gelöscht und der Ordner entfernt — sie trägt Gegenstelle und
/// Techniker, und die sollen nicht überleben, was sie beschreiben.</para>
/// </remarks>
public sealed class RecordingCleaner
{
    private readonly RecordingStore _store;
    private readonly string _root;

    /// <summary>Baut den Aufräumer.</summary>
    /// <param name="store">Die Buchführung.</param>
    /// <param name="root">Die Wurzel, unterhalb derer gelöscht werden darf.</param>
    public RecordingCleaner(RecordingStore store, string root)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        _store = store;
        _root = root;
    }

    /// <summary>
    /// Ein Durchlauf.
    /// </summary>
    /// <remarks>
    /// Hausregel 5: Eine Datei, die sich nicht löschen lässt — sie ist gerade in einem
    /// Abspieler offen —, kostet diesen einen Eintrag. Der Durchlauf geht weiter, und beim
    /// nächsten Mal klappt es.
    /// </remarks>
    /// <param name="retention">Die aktuell eingestellte Aufbewahrungsdauer.</param>
    /// <returns>Was der Durchlauf getan hat.</returns>
    public CleanupResult Run(TimeSpan retention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        int deleted = 0;
        int missing = 0;
        int failed = 0;
        long bytes = 0;

        foreach (RecordingEntry entry in _store.Due(retention))
        {
            string path = Path.Combine(_root, entry.RelativePath);

            if (!RecordingPaths.IsInside(_root, path))
            {
                // Ein Eintrag, dessen Pfad aus der Wurzel herausfuehrt. Das darf nicht
                // vorkommen - und wenn doch, wird hier ganz sicher nicht geloescht.
                _ = _store.MarkMissing(entry.Id,
                    "Der eingetragene Pfad liegt nicht unterhalb des eingestellten Ordners. "
                    + "Gelöscht wurde nichts.");

                failed++;
                continue;
            }

            try
            {
                if (File.Exists(path))
                {
                    bytes += new FileInfo(path).Length;
                    File.Delete(path);

                    _ = _store.MarkPurged(entry.Id,
                        $"Aufbewahrungsfrist abgelaufen; zugesagt war der "
                        + $"{entry.DeleteAfter.ToLocalTime():dd.MM.yyyy}.");

                    deleted++;
                }
                else
                {
                    _ = _store.MarkMissing(entry.Id,
                        "Die Datei lag beim Aufräumen nicht mehr dort. Sie ist von Hand "
                        + "entfernt oder von einem Sicherungslauf verschoben worden.");

                    missing++;
                }

                TidyFolder(Path.GetDirectoryName(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Hausregel 5: Diese eine Datei geht schief, der Durchlauf nicht. Der Eintrag
                // bleibt faellig und kommt beim naechsten Mal wieder dran.
                failed++;
            }
        }

        return new CleanupResult(deleted, missing, failed, bytes);
    }

    /// <summary>
    /// Räumt den Ordner einer Sitzung auf, wenn kein Video mehr darin liegt.
    /// </summary>
    /// <remarks>
    /// Die Begleitdatei überlebt die Videos nicht: Sie trägt Gegenstelle und Techniker, und die
    /// sollen nicht länger daliegen als das, was sie beschreiben.
    /// </remarks>
    private static void TidyFolder(string? folder)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            if (Directory.EnumerateFiles(folder, "*.mp4").Any())
            {
                return;
            }

            string manifest = Path.Combine(folder, RecordingPaths.ManifestName);

            if (File.Exists(manifest))
            {
                File.Delete(manifest);
            }

            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ein Ordner, der stehen bleibt, ist kein Schaden - nur unordentlich.
        }
    }
}

/// <summary>Was ein Aufräumdurchlauf getan hat.</summary>
/// <param name="Deleted">Wie viele Dateien gelöscht wurden.</param>
/// <param name="Missing">Wie viele nicht mehr dort lagen.</param>
/// <param name="Failed">Wie viele sich nicht löschen liessen.</param>
/// <param name="FreedBytes">Wie viel Platz frei wurde.</param>
public readonly record struct CleanupResult(int Deleted, int Missing, int Failed, long FreedBytes)
{
    /// <summary>Hat der Durchlauf überhaupt etwas getan?</summary>
    public bool DidAnything => Deleted > 0 || Missing > 0 || Failed > 0;

    /// <summary>Ein Satz für die Anzeige.</summary>
    public string Summary => !DidAnything
        ? "Nichts fällig."
        : $"{Deleted} Aufzeichnung(en) gelöscht, {FreedBytes / 1024 / 1024} MB frei"
          + (Missing > 0 ? $", {Missing} lag(en) nicht mehr dort" : string.Empty)
          + (Failed > 0 ? $", {Failed} liess(en) sich nicht löschen" : string.Empty)
          + ".";
}
