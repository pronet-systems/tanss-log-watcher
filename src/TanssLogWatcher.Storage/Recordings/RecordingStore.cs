using System.Globalization;
using Microsoft.Data.Sqlite;
using TanssLogWatcher.Api;

namespace TanssLogWatcher.Storage.Recordings;

/// <summary>
/// Die Buchführung über Bildschirmaufzeichnungen.
/// </summary>
/// <remarks>
/// <para><b>Das Dateisystem ist Nutzlast, nicht Wahrheit.</b> Ein Verzeichnisdurchlauf als
/// Grundlage hätte zwei Schwächen, die beide teuer sind: Er fände Dateien, die jemand von Hand
/// hineinkopiert hat, und löschte sie. Und er verlöre jede Spur von dem, was schon gelöscht
/// wurde — für eine Auskunft nach Art. 15 DSGVO wäre das wertlos, denn dort ist zu sagen, was
/// es einmal gab und wann es verschwand.</para>
///
/// <para><b>Der Löschzeitpunkt ist absolut und wird beim Beginn festgeschrieben.</b> Eine
/// mitlaufende Frist wäre keine: Sie liefe nur, solange das Werkzeug läuft, und ein Notebook,
/// das zwei Monate im Schrank liegt, käme mit lauter überfälligen Aufzeichnungen zurück, deren
/// Frist in Wahrheit nie ablief.</para>
///
/// <para><b>Verkürzen wirkt, verlängern nicht.</b> Wer die eingestellte Frist heruntersetzt,
/// verkürzt auch die bestehenden Aufzeichnungen — das ist ein Versprechen, das man jederzeit
/// enger fassen darf. Wer sie heraufsetzt, verlängert die bestehenden <b>nicht</b>: Ein
/// gegebenes Versprechen wird nicht nachträglich gedehnt.</para>
/// </remarks>
public sealed class RecordingStore : IDisposable
{
    private readonly StateDatabase _database;
    private readonly bool _ownsDatabase;
    private readonly TimeProvider _time;

    /// <summary>Öffnet die Buchführung an einem Ort.</summary>
    /// <param name="path">Die Zustandsdatenbank.</param>
    /// <param name="timeProvider">Die Uhr; ohne Angabe die des Systems.</param>
    public RecordingStore(string path, TimeProvider? timeProvider = null)
        : this(new StateDatabase(path), timeProvider, ownsDatabase: true)
    {
    }

    /// <summary>Benutzt eine bereits geöffnete Zustandsdatenbank mit.</summary>
    /// <param name="database">Die Datenbank.</param>
    /// <param name="timeProvider">Die Uhr; ohne Angabe die des Systems.</param>
    public RecordingStore(StateDatabase database, TimeProvider? timeProvider = null)
        : this(database, timeProvider, ownsDatabase: false)
    {
    }

    private RecordingStore(StateDatabase database, TimeProvider? timeProvider, bool ownsDatabase)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
        _time = timeProvider ?? TimeProvider.System;
        _ownsDatabase = ownsDatabase;
    }

    /// <summary>
    /// Trägt eine beginnende Aufzeichnung ein.
    /// </summary>
    /// <remarks>
    /// Beim Beginn und nicht beim Ende: Stürzt das Werkzeug mitten in der Aufzeichnung ab,
    /// bliebe die Datei sonst als Waise liegen — niemand wüsste, zu welcher Sitzung sie gehört
    /// und wann sie zu löschen wäre.
    /// </remarks>
    /// <param name="sessionId">Die Sitzung; zugleich die Kennung bei TANSS.</param>
    /// <param name="relativePath">Der Pfad unterhalb der eingestellten Wurzel.</param>
    /// <param name="segment">Der wievielte Abschnitt dieser Sitzung.</param>
    /// <param name="retention">Die zugesagte Aufbewahrungsdauer.</param>
    /// <returns>Die Kennung des Eintrags.</returns>
    public long Begin(string sessionId, string relativePath, int segment, TimeSpan retention)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(segment, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        DateTimeOffset now = _time.GetUtcNow();

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO recordings
                    (remote_maintenance_id, relative_path, segment, started_at,
                     delete_after, state)
                VALUES ($session, $path, $segment, $now, $delete, 'recording');
                SELECT last_insert_rowid();
                """;
            _ = command.Parameters.AddWithValue("$session", sessionId);
            _ = command.Parameters.AddWithValue("$path", relativePath);
            _ = command.Parameters.AddWithValue("$segment", segment);
            _ = command.Parameters.AddWithValue("$now", TanssTime.ToUnixSeconds(now));
            _ = command.Parameters.AddWithValue("$delete",
                TanssTime.ToUnixSeconds(now + retention));

            return (long)(command.ExecuteScalar() ?? 0L);
        });
    }

    /// <summary>
    /// Schliesst eine Aufzeichnung ab.
    /// </summary>
    /// <param name="id">Der Eintrag.</param>
    /// <param name="recorded">
    /// Die aufgezeichnete Zeit <b>ohne</b> die Pausen. Sie ist nicht die Differenz aus Beginn
    /// und Ende, und genau das ist der Punkt: Eine Sitzung von fünfundvierzig Minuten mit
    /// fünfundzwanzig Minuten Pause hinterlässt hier zwanzig.
    /// </param>
    /// <param name="bytes">Wie gross die Datei geworden ist.</param>
    /// <returns><c>false</c>, wenn es den Eintrag nicht gibt.</returns>
    public bool Finish(long id, TimeSpan recorded, long bytes)
    {
        long now = TanssTime.ToUnixSeconds(_time.GetUtcNow());

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE recordings
                   SET ended_at = $now,
                       recorded_seconds = $recorded,
                       bytes = $bytes,
                       state = 'kept'
                 WHERE id = $id AND state = 'recording';
                """;
            _ = command.Parameters.AddWithValue("$id", id);
            _ = command.Parameters.AddWithValue("$now", now);
            _ = command.Parameters.AddWithValue("$recorded", (long)recorded.TotalSeconds);
            _ = command.Parameters.AddWithValue("$bytes", bytes);

            return command.ExecuteNonQuery() > 0;
        });
    }

    /// <summary>
    /// Die Aufzeichnungen, deren Frist abgelaufen ist.
    /// </summary>
    /// <remarks>
    /// Massgeblich ist der <b>frühere</b> von zugesagtem Löschzeitpunkt und dem, was die
    /// aktuelle Einstellung ergäbe. So wirkt eine verkürzte Frist sofort auf bestehende
    /// Aufzeichnungen, eine verlängerte aber nicht.
    /// </remarks>
    /// <param name="retention">Die aktuell eingestellte Aufbewahrungsdauer.</param>
    /// <param name="limit">Höchstens so viele auf einmal.</param>
    public IReadOnlyList<RecordingEntry> Due(TimeSpan retention, int limit = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        long now = TanssTime.ToUnixSeconds(_time.GetUtcNow());
        long cutoff = TanssTime.ToUnixSeconds(_time.GetUtcNow() - retention);

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, remote_maintenance_id, relative_path, segment, started_at,
                       ended_at, recorded_seconds, bytes, delete_after, state
                  FROM recordings
                 WHERE state IN ('recording','kept')
                   AND (delete_after <= $now OR started_at <= $cutoff)
                 ORDER BY delete_after
                 LIMIT $limit;
                """;
            _ = command.Parameters.AddWithValue("$now", now);
            _ = command.Parameters.AddWithValue("$cutoff", cutoff);
            _ = command.Parameters.AddWithValue("$limit", limit);

            return Read(command);
        });
    }

    /// <summary>Die Aufzeichnungen einer Sitzung, jüngste zuletzt.</summary>
    /// <param name="sessionId">Die Sitzung.</param>
    public IReadOnlyList<RecordingEntry> For(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, remote_maintenance_id, relative_path, segment, started_at,
                       ended_at, recorded_seconds, bytes, delete_after, state
                  FROM recordings
                 WHERE remote_maintenance_id = $session
                 ORDER BY segment;
                """;
            _ = command.Parameters.AddWithValue("$session", sessionId);

            return Read(command);
        });
    }

    /// <summary>
    /// Hält fest, dass eine Aufzeichnung gelöscht wurde.
    /// </summary>
    /// <remarks>
    /// <b>Die Zeile bleibt als Grabstein stehen.</b> Sie überlebt die Datei, und das ist
    /// Absicht: Für eine Auskunft nach Art. 15 DSGVO ist zu sagen, was es gab und wann es
    /// verschwand — eine gelöschte Zeile könnte das nicht.
    /// </remarks>
    /// <param name="id">Der Eintrag.</param>
    /// <param name="reason">Warum gelöscht wurde, in deutscher Prosa.</param>
    public bool MarkPurged(long id, string reason) => SetState(id, "purged", reason);

    /// <summary>
    /// Hält fest, dass die Datei nicht mehr da ist, ohne dass wir sie gelöscht hätten.
    /// </summary>
    /// <remarks>
    /// Jemand hat sie von Hand entfernt, ein Sicherungslauf hat sie verschoben, der
    /// Datenträger war abgezogen. Das ist kein Fehler, aber es gehört festgehalten — sonst
    /// meldete der Aufräumer sie bei jedem Durchlauf erneut.
    /// </remarks>
    /// <param name="id">Der Eintrag.</param>
    /// <param name="reason">Was vorgefunden wurde.</param>
    public bool MarkMissing(long id, string reason) => SetState(id, "missing", reason);

    /// <summary>Wie viele Einträge in einem Zustand stehen.</summary>
    /// <param name="state">Der Zustand.</param>
    public int Count(string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM recordings WHERE state = $state;";
            _ = command.Parameters.AddWithValue("$state", state);

            return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        });
    }

    /// <summary>Schliesst die Datenbank, falls sie hier geöffnet wurde.</summary>
    public void Dispose()
    {
        if (_ownsDatabase)
        {
            _database.Dispose();
        }
    }

    private bool SetState(long id, string state, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        long now = TanssTime.ToUnixSeconds(_time.GetUtcNow());

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE recordings
                   SET state = $state, deleted_at = $now, delete_reason = $reason
                 WHERE id = $id AND state IN ('recording','kept');
                """;
            _ = command.Parameters.AddWithValue("$id", id);
            _ = command.Parameters.AddWithValue("$state", state);
            _ = command.Parameters.AddWithValue("$now", now);
            _ = command.Parameters.AddWithValue("$reason", reason);

            return command.ExecuteNonQuery() > 0;
        });
    }

    private static List<RecordingEntry> Read(SqliteCommand command)
    {
        List<RecordingEntry> entries = [];

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            entries.Add(new RecordingEntry
            {
                Id = reader.GetInt64(0),
                SessionId = reader.GetString(1),
                RelativePath = reader.GetString(2),
                Segment = reader.GetInt32(3),
                StartedAt = TanssTime.FromUnixSeconds(reader.GetInt64(4)) ?? default,
                EndedAt = reader.IsDBNull(5)
                    ? null
                    : TanssTime.FromUnixSeconds(reader.GetInt64(5)),
                Recorded = TimeSpan.FromSeconds(reader.GetInt64(6)),
                Bytes = reader.GetInt64(7),
                DeleteAfter = TanssTime.FromUnixSeconds(reader.GetInt64(8)) ?? default,
                State = reader.GetString(9),
            });
        }

        return entries;
    }
}

/// <summary>Eine Aufzeichnung, so wie die Buchführung sie kennt.</summary>
public sealed record RecordingEntry
{
    /// <summary>Die Kennung des Eintrags.</summary>
    public required long Id { get; init; }

    /// <summary>Die Sitzung; zugleich die Kennung bei TANSS.</summary>
    public required string SessionId { get; init; }

    /// <summary>Der Pfad unterhalb der eingestellten Wurzel.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Der wievielte Abschnitt dieser Sitzung.</summary>
    public int Segment { get; init; } = 1;

    /// <summary>Wann die Aufzeichnung begann.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Wann sie endete; <c>null</c>, solange sie läuft.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>
    /// Die aufgezeichnete Zeit — ohne die Pausen.
    /// </summary>
    /// <remarks>
    /// Sie ist ausdrücklich nicht die Differenz aus <see cref="StartedAt"/> und
    /// <see cref="EndedAt"/>. Wer beides vergleicht, sieht auf einen Blick, wie viel der
    /// Sitzung aus Pausen bestand.
    /// </remarks>
    public TimeSpan Recorded { get; init; }

    /// <summary>Wie gross die Datei ist.</summary>
    public long Bytes { get; init; }

    /// <summary>Der zugesagte Löschzeitpunkt.</summary>
    public required DateTimeOffset DeleteAfter { get; init; }

    /// <summary>Der Zustand: <c>recording</c>, <c>kept</c>, <c>purged</c> oder <c>missing</c>.</summary>
    public required string State { get; init; }

    /// <summary>Liegt die Datei noch?</summary>
    public bool Exists => State is "recording" or "kept";
}
