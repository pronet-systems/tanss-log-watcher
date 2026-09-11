using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;

namespace TanssLogWatcher.Storage;

/// <summary>
/// Die Zustandsdatenbank unter
/// <c>%LOCALAPPDATA%\ProNet Systems\TanssLogWatcher\state.db</c>.
/// </summary>
/// <remarks>
/// <para>Sie trägt alles, was ein Absturz nicht verschlucken darf: die Warteschlange noch
/// nicht hochgeladener Fernwartungen, die gerade laufenden Sitzungen und das
/// Änderungsprotokoll. Das Original hatte davon nichts — ein Netzfehler kostete dort die
/// Sitzung ersatzlos, und ein Absturz während einer laufenden Fernwartung ebenso.</para>
///
/// <para><b>WAL ist keine Feinabstimmung, sondern Bedingung.</b> Der Beobachtungstakt
/// schreibt die laufenden Sitzungen fort, während der Versandarbeiter in dieselbe Datei
/// schreibt. Ohne WAL sperren sich beide gegenseitig aus und landen in
/// <c>database is locked</c>.</para>
///
/// <para>Der Stand des Aufbaus steht in <c>PRAGMA user_version</c>. Eine Datei mit höherer
/// Nummer wird <b>nicht</b> geöffnet: Ein älterer Programmstand kennt die neueren Spalten
/// nicht und verlöre beim Schreiben stillschweigend Felder — also genau die Sitzungen, die
/// schon erfasst waren.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StateDatabase : IDisposable
{
    /// <summary>Stand des Datenbankaufbaus, den diese Programmfassung erwartet.</summary>
    /// <remarks>
    /// Stand 2 kennt <c>queue.outcome_unknown</c> (Ausgang eines abgebrochenen Versands
    /// ungeklärt) und <c>session_log.window_title</c> (die eigens benannte
    /// Fensterbeschriftung, die geschwärzt werden kann).
    /// </remarks>
    public const int SchemaVersion = 2;

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Öffnet — und legt bei Bedarf an — die Datenbank am angegebenen Pfad.</summary>
    public StateDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);

        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Ohne Pooling schliesst Dispose die Datei wirklich. Mit Pooling bliebe sie
            // offen, und die Tests koennten ihr temporaeres Verzeichnis nicht aufraeumen.
            Pooling = false,
            ForeignKeys = true,
        };

        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();
        Configure();
        Migrate();
    }

    /// <summary>Öffnet die Datenbank am vorgesehenen Ort im lokalen Profil.</summary>
    public static StateDatabase Default() => new(StoragePaths.StateDatabaseFile);

    /// <summary>Pfad der Datenbankdatei.</summary>
    public string Path { get; }

    /// <summary>Führt eine Arbeit unter der Instanzsperre aus und liefert ihr Ergebnis.</summary>
    /// <remarks>
    /// Eine <see cref="SqliteConnection"/> verträgt keine Nebenläufigkeit. Die Sperre schützt
    /// jedoch nur <i>diese</i> Instanz; gegen einen zweiten Prozess wirkt allein das
    /// Sperrverhalten von SQLite. Deshalb laufen alle Schreibvorgänge, die vorher lesen
    /// müssen, zusätzlich in einer <c>BEGIN IMMEDIATE</c>-Transaktion.
    /// </remarks>
    internal T Execute<T>(Func<SqliteConnection, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        // Die Prüfung gehört INNERHALB der Sperre. Davor könnte ein Aufrufer sie passieren
        // und danach auf einer bereits geschlossenen Verbindung arbeiten - beim geordneten
        // Beenden schreibt der Beobachtungstakt erfahrungsgemäß noch, während abgebaut wird.
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return work(_connection);
        }
    }

    /// <summary>Führt eine Arbeit ohne Ergebnis unter der Instanzsperre aus.</summary>
    internal void Execute(Action<SqliteConnection> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        _ = Execute(connection =>
        {
            work(connection);
            return true;
        });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nimmt dieselbe Sperre wie <c>Execute</c> und wartet damit auf eine laufende Abfrage.
    /// Ohne sie schlösse ein Beenden die <see cref="SqliteConnection"/> mitten im Lesen —
    /// und der Eintrag, der gerade geschrieben wurde, wäre fort.
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection.Dispose();
        }
    }

    private void Configure()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            """;
        _ = command.ExecuteNonQuery();
    }

    private void Migrate()
    {
        long found = ReadUserVersion();

        if (found > SchemaVersion)
        {
            throw new StateDatabaseException(
                $"{Path} trägt den Stand {found}, dieses Werkzeug kennt höchstens "
                + $"{SchemaVersion}. Die Datei stammt aus einer neueren Programmfassung. "
                + "Sie wird nicht geöffnet, weil ein älterer Stand beim Schreiben Felder "
                + "verlöre, die er nicht kennt — betroffen wären genau die Sitzungen, die "
                + "bereits erfasst sind. Bitte das Werkzeug aktualisieren.");
        }

        if (found == SchemaVersion)
        {
            return;
        }

        using SqliteTransaction transaction = _connection.BeginTransaction();
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = Schema;
            _ = command.ExecuteNonQuery();
        }

        // CREATE TABLE IF NOT EXISTS rührt eine vorhandene Tabelle nicht an. Eine Datei vom
        // Stand 1 bekäme die neuen Spalten also nie - und jede Abfrage darauf scheiterte
        // beim nächsten Start. Deshalb hier einzeln und für sich nachgezogen.
        EnsureColumn(transaction, "queue", "outcome_unknown", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(transaction, "session_log", "window_title", "TEXT");

        using (SqliteCommand version = _connection.CreateCommand())
        {
            version.Transaction = transaction;
            // PRAGMA nimmt keine Parameter entgegen; der Wert ist eine eigene Konstante.
            version.CommandText = string.Create(CultureInfo.InvariantCulture,
                $"PRAGMA user_version = {SchemaVersion};");
            _ = version.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Legt eine Spalte an, falls sie fehlt.</summary>
    /// <remarks>
    /// <c>ALTER TABLE … ADD COLUMN</c> kennt kein <c>IF NOT EXISTS</c>; ein zweiter Lauf
    /// bräche sonst mit „duplicate column name“ ab. Die Überführung muss aber wiederholbar
    /// sein, sonst wäre eine abgebrochene Überführung ein Fall für Handarbeit.
    /// </remarks>
    private void EnsureColumn(SqliteTransaction transaction, string table, string column,
                              string definition)
    {
        using (SqliteCommand probe = _connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
            _ = probe.Parameters.AddWithValue("$table", table);
            _ = probe.Parameters.AddWithValue("$column", column);
            if (Convert.ToInt64(probe.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }

        using SqliteCommand alter = _connection.CreateCommand();
        alter.Transaction = transaction;
        // Tabellen- und Spaltenname sind hausgemachte Konstanten; ALTER TABLE nimmt dafür
        // ohnehin keine Parameter entgegen.
        alter.CommandText = string.Create(CultureInfo.InvariantCulture,
            $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
        _ = alter.ExecuteNonQuery();
    }

    private long ReadUserVersion()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Der Aufbau, Stand <see cref="SchemaVersion"/>.
    /// </summary>
    /// <remarks>
    /// Bewusst durchweg <c>IF NOT EXISTS</c>: Das Skript ist damit wiederholbar und eine
    /// abgebrochene Erstanlage kein Sonderfall, der von Hand aufgeräumt werden müsste.
    /// </remarks>
    private const string Schema = """
        -- Noch nicht bei TANSS angekommene Fernwartungen.
        -- Der Primaerschluessel ist unsere eigene Sitzungskennung. Er ist der einzige
        -- Schutz gegen Doppelbuchungen: TANSS dedupliziert nicht, ein zweiter POST mit
        -- derselben remoteMaintenanceId legt nachweislich einen zweiten Datensatz an.
        CREATE TABLE IF NOT EXISTS queue (
          remote_maintenance_id TEXT    PRIMARY KEY,
          payload               TEXT    NOT NULL,
          created_at            INTEGER NOT NULL,
          attempts              INTEGER NOT NULL DEFAULT 0,
          next_attempt_at       INTEGER NOT NULL DEFAULT 0,
          last_error            TEXT,
          leased_at             INTEGER,
          completed_at          INTEGER,
          -- 1 heisst: Der Versand brach ab, ohne dass jemand die Antwort gesehen hat.
          -- TANSS dedupliziert nicht, der Eintrag kann trotzdem dort stehen. Der Aufrufer
          -- MUSS dann vor dem naechsten Versuch die Existenzpruefung stellen.
          outcome_unknown       INTEGER NOT NULL DEFAULT 0,
          state                 TEXT    NOT NULL DEFAULT 'pending'
              CHECK (state IN ('pending','sending','done','failed'))
        );

        CREATE INDEX IF NOT EXISTS idx_queue_due ON queue(state, next_attempt_at);
        CREATE INDEX IF NOT EXISTS idx_queue_leased ON queue(leased_at) WHERE state = 'sending';

        -- Laufende Sitzungen. Ohne sie verschluckt ein Absturz jede Fernwartung, die
        -- gerade laeuft - beim Neustart waere ihr Beginn unbekannt und damit verloren.
        CREATE TABLE IF NOT EXISTS open_sessions (
          remote_maintenance_id  TEXT    PRIMARY KEY,
          monitor_key            TEXT    NOT NULL,
          remote_support_type_id INTEGER NOT NULL,
          started_at             INTEGER NOT NULL,
          last_seen_at           INTEGER NOT NULL,
          process_id             INTEGER NOT NULL DEFAULT 0,
          target                 TEXT,
          device_name            TEXT,
          user_name              TEXT,
          comment                TEXT    NOT NULL DEFAULT '',
          ticket_id              INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS idx_open_sessions_seen ON open_sessions(last_seen_at);

        -- Aenderungsprotokoll. Haelt fest, WARUM etwas geschah, nicht nur DASS.
        CREATE TABLE IF NOT EXISTS session_log (
          id                    INTEGER PRIMARY KEY,
          ts                    INTEGER NOT NULL,
          remote_maintenance_id TEXT,
          operation             TEXT    NOT NULL,
          outcome               TEXT    NOT NULL,
          reason                TEXT    NOT NULL,
          trigger               TEXT    NOT NULL,
          http_status           INTEGER,
          duration_ms           INTEGER,
          tanss_support_id      INTEGER,
          detail                TEXT,
          -- Eigene Spalte, damit sich die Beschriftung als solche schwaerzen laesst.
          -- In Freitext waere sie von gewoehnlicher Prosa nicht zu unterscheiden.
          window_title          TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_session_log_ts ON session_log(ts);
        CREATE INDEX IF NOT EXISTS idx_session_log_session ON session_log(remote_maintenance_id);
        """;
}
