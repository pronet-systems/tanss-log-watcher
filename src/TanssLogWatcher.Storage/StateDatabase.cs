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
    /// <para>Stand 2 kennt <c>queue.outcome_unknown</c> (Ausgang eines abgebrochenen Versands
    /// ungeklärt) und <c>session_log.window_title</c> (die eigens benannte
    /// Fensterbeschriftung, die geschwärzt werden kann). Beides sind Spalten in bereits
    /// vorhandenen Tabellen und deshalb die einzigen beiden Zeilen, die <c>EnsureColumn</c>
    /// nachzieht.</para>
    ///
    /// <para>Stand 3 kennt <c>recordings</c> — die Bildschirmaufzeichnungen mit Abschnitt,
    /// aufgezeichneter Zeit, zugesagtem Löschzeitpunkt und Verbleib. Neue Spalten in alten
    /// Tabellen brachte er nicht mit; eine ganze Tabelle entsteht bereits aus dem
    /// Schema-Text, weshalb zu Stand 3 keine eigene Zeile in <c>Migrate</c> steht.</para>
    ///
    /// <para>Stand 4 kennt <c>session_history</c> — den Verlauf abgeschlossener Sitzungen.
    /// Er ist aus dem Vorhandenen nicht abzuleiten: Die Warteschlange ist kein Verlauf, weil
    /// der Abschlussdialog ihre Zeile über <c>IUploadQueue.Remove</c> endgültig entfernt und
    /// <c>PurgeCompleted</c> eigens dafür gebaut ist, abgeschlossene Zeilen nach einer Frist
    /// zu löschen. Im Änderungsprotokoll wiederum steht die Gegenstelle nur als Abdruck —
    /// und ein Abdruck ist kein Name: Eine Liste daraus zeigte dem Techniker acht
    /// Hexziffern, oft genug sogar nur den Abdruck des Platzhalters
    /// <c>&lt;&lt;&lt; Kein Titel ermittelt &gt;&gt;&gt;</c>.</para>
    ///
    /// <para>Stand 5 kennt <c>open_sessions.identity_key</c> — den Bezeichner der Gegenstelle,
    /// aus dem die Gerätekennung für TANSS entsteht. Er ist eine <b>Spalte in einer bereits
    /// vorhandenen Tabelle</b> und braucht deshalb, anders als die ganzen Tabellen der Stände
    /// 3 und 4, eine eigene Zeile in <c>Migrate</c>: <c>CREATE TABLE IF NOT EXISTS</c> rührt
    /// eine vorhandene Tabelle nicht an. Ohne ihn verlor jede laufende Sitzung beim Neustart
    /// ihre Kennung, und TANSS konnte die Firma für genau diese eine Fernwartung nicht mehr
    /// selbst einsetzen.</para>
    ///
    /// <para>Stand 6 kennt <c>queue.awaiting_decision</c> — das Kennzeichen „wartet auf die
    /// Entscheidung des Technikers“. Es tritt an die Stelle der frueheren Schonfrist: Die
    /// Sitzung liegt beim Sitzungsende sofort in der Warteschlange, aber <b>keine Uhr</b>
    /// gibt sie frei. Das ist eine <b>Spalte in einer bereits vorhandenen Tabelle</b> und
    /// braucht deshalb eine eigene Zeile in <c>Migrate</c>. Der Vorgabewert 0 ist der
    /// richtige fuer alles, was schon dasteht: Zeilen aus einem frueheren Lauf haben keinen
    /// Dialog mehr, auf den sie warten koennten, und muessen hinausgehen.</para>
    /// </remarks>
    public const int SchemaVersion = 6;

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

    /// <summary>
    /// Der Stand, den die Datei beim Öffnen trug — <b>vor</b> der Überführung.
    /// </summary>
    /// <remarks>
    /// Er ist der Auslöser für einmalige Nacharbeiten, die nicht in die Überführung gehören.
    /// Der Nachtrag des Verlaufs muss die Nutzlast der Warteschlange auswerten und damit JSON
    /// lesen; das ist Sache einer höheren Schicht, nicht dieser. Eine frisch angelegte Datei
    /// trägt hier <c>0</c> — dort ist nichts nachzutragen.
    /// </remarks>
    public int PreviousSchemaVersion { get; private set; }

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
        // VOR jeder Rueckkehr gesetzt: Der Aufrufer prueft den Wert auch dann, wenn gar
        // nichts zu ueberfuehren war - sonst liefe der Nachtrag bei jedem zweiten Start
        // erneut an.
        PreviousSchemaVersion = (int)found;

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
        // Stand 5. Bewusst ohne NOT NULL: Die Zeilen, die schon dastehen, haben keinen
        // Bezeichner - und ein leerer Ersatzwert waere eine erfundene Angabe. NULL heisst
        // hier "nicht ermittelt", und daraus entsteht keine Geraetekennung.
        EnsureColumn(transaction, "open_sessions", "identity_key", "TEXT");
        // Stand 6. Vorgabe 0, und das mit Bedacht: Was vor dieser Fassung eingereiht wurde,
        // wartet auf keinen Dialog mehr - eine 1 liesse genau diese Zeilen fuer immer liegen.
        EnsureColumn(transaction, "queue", "awaiting_decision", "INTEGER NOT NULL DEFAULT 0");

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
          -- 1 heisst: Die Zeile wartet auf die Entscheidung des Technikers im
          -- Abschlussdialog und wird von Lease NICHT zugeteilt. Kein Zeitablauf hebt das
          -- auf - das ist der Unterschied zur frueheren Schonfrist, die nach fuenf Minuten
          -- ungefragt buchte. Aufgehoben wird es allein durch eine Entscheidung: Release
          -- ("Spaeter"/"Jetzt senden"), LeaseOne ("In TANSS buchen") oder Remove
          -- ("Verwerfen").
          awaiting_decision     INTEGER NOT NULL DEFAULT 0,
          state                 TEXT    NOT NULL DEFAULT 'pending'
              CHECK (state IN ('pending','sending','done','failed'))
        );

        -- Bewusst OHNE awaiting_decision: Der Schema-Text laeuft VOR EnsureColumn, und eine
        -- Datei, die diesen Index noch nicht hat, kennt die Spalte in diesem Augenblick
        -- noch nicht - die Ueberfuehrung braeche mit "no such column" ab.
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
          -- Der Bezeichner der Gegenstelle, UNGEKUERZT. Aus ihm bildet die
          -- Wiederherstellung zusammen mit monitor_key die deviceId, ueber die TANSS die
          -- Firma selbst einsetzt. Hier steht ausdruecklich der Bezeichner und nicht die
          -- fertige Kennung: Der Riegel des Profilkatalogs soll beim LESEN greifen, mit dem
          -- dann gueltigen Katalog - sonst ginge eine einmal gespeicherte Kennung auch dann
          -- noch hinaus, wenn ihr Profil seine Zusicherung verloren hat.
          identity_key           TEXT,
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

        -- Bildschirmaufzeichnungen. Die Wahrheit ueber Faelligkeit und Verbleib steht HIER
        -- und nicht im Dateisystem: Ein Verzeichnisdurchlauf als Grundlage haette zwei
        -- Schwaechen, die beide teuer sind. Er faende Dateien, die jemand von Hand
        -- hineinkopiert hat, und loeschte sie; und er verloere jede Spur von dem, was schon
        -- geloescht wurde - fuer eine Auskunft nach Art. 15 DSGVO waere das wertlos.
        CREATE TABLE IF NOT EXISTS recordings (
          id                    INTEGER PRIMARY KEY,
          remote_maintenance_id TEXT    NOT NULL,
          -- Relativ zur eingestellten Wurzel. Geloescht wird spaeter ausschliesslich, was
          -- nach dem Zusammensetzen unterhalb dieser Wurzel liegt - ein absoluter Pfad in
          -- der Datenbank waere eine Einladung, irgendwohin zu loeschen.
          relative_path         TEXT    NOT NULL,
          segment               INTEGER NOT NULL DEFAULT 1,
          started_at            INTEGER NOT NULL,
          ended_at              INTEGER,
          -- Die aufgezeichnete Zeit OHNE Pausen, in Sekunden. Sie ist nicht die Differenz
          -- aus Beginn und Ende, und genau das ist der Punkt.
          recorded_seconds      INTEGER NOT NULL DEFAULT 0,
          bytes                 INTEGER NOT NULL DEFAULT 0,
          -- Der zugesagte Loeschzeitpunkt, absolut und beim Abschluss festgeschrieben.
          -- Eine mitlaufende Frist waere keine: Sie liefe nur, solange das Werkzeug laeuft.
          delete_after          INTEGER NOT NULL,
          deleted_at            INTEGER,
          delete_reason         TEXT,
          state                 TEXT    NOT NULL DEFAULT 'recording'
              CHECK (state IN ('recording','kept','purged','missing'))
        );

        CREATE INDEX IF NOT EXISTS idx_recordings_due ON recordings(state, delete_after);
        CREATE INDEX IF NOT EXISTS idx_recordings_session ON recordings(remote_maintenance_id);

        -- Der Verlauf abgeschlossener Sitzungen. Er ist die EINZIGE Stelle, an der eine
        -- Sitzung ihren Abschluss ueberlebt: Die Warteschlange verliert ihre Zeile beim
        -- Verwerfen und nach der Frist, und das Aenderungsprotokoll kennt weder Ende noch
        -- Dauer noch Typ - und die Gegenstelle dort nur als Abdruck.
        CREATE TABLE IF NOT EXISTS session_history (
          -- Unsere Sitzungskennung, wie ueberall. Als Primaerschluessel zugleich der Riegel
          -- gegen eine zweite Zeile derselben Sitzung nach einem Neustart.
          remote_maintenance_id   TEXT    PRIMARY KEY,
          started_at              INTEGER NOT NULL,
          ended_at                INTEGER NOT NULL,
          profile_key             TEXT    NOT NULL DEFAULT '',
          profile_name            TEXT    NOT NULL DEFAULT '',
          -- Die Gegenstelle im Klartext - und NULL, sobald history.plain_text_days
          -- abgelaufen ist oder sie nie bekannt war. Sie steht getrennt vom Abdruck, damit
          -- das Schwaerzen ein UPDATE ist und kein Verlust der Zeile.
          destination             TEXT,
          -- Der gesalzene Abdruck derselben Gegenstelle, unter demselben Schluessel wie in
          -- session_log.window_title. Sein Zweck ist die Gruppierung UEBER Sitzungen hinweg:
          -- "dieselbe Gegenstelle wie in jener anderen Sitzung?" bleibt beantwortbar, wenn
          -- der Klartext geht. Er ist KEIN Ersatz fuer den Namen - der Abdruck des
          -- Platzhalters "<<< Kein Titel ermittelt >>>" gehoert hier NICHT hinein, sondern
          -- auf NULL, sonst zeigte die Anzeige acht Hexziffern als Gegenstelle.
          destination_fingerprint TEXT,
          remote_support_type_id  INTEGER NOT NULL DEFAULT 0,
          ticket_id               INTEGER NOT NULL DEFAULT 0,
          tanss_support_id        INTEGER,
          disposition             TEXT    NOT NULL
              CHECK (disposition IN ('enqueued','booked','discarded','not_mapped',
                                     'dry_run','failed','unknown')),
          reason                  TEXT    NOT NULL DEFAULT '',
          -- 1 heisst: Das Ende stammt aus einem Protokolleintrag und nicht aus der Messung.
          -- Gemessen weicht es dann bis zu 325 Sekunden ab; eine Dauer, die das
          -- verschwiege, waere eine erfundene Zahl.
          ended_estimated         INTEGER NOT NULL DEFAULT 0,
          -- 'backfill' heisst: einmalig aus queue und session_log nachgetragen, nicht beim
          -- Abschluss geschrieben. Die Anzeige soll das sagen duerfen.
          origin                  TEXT    NOT NULL DEFAULT 'live'
              CHECK (origin IN ('live','backfill')),
          -- FRUEHESTENS dann wird die Zeile geloescht; absolut und beim Anlegen
          -- festgeschrieben, denn eine mitlaufende Frist waere keine - sie liefe nur,
          -- solange das Werkzeug laeuft. "Fruehestens" deshalb, weil
          -- recording.retention_days laenger sein darf: Solange eine Aufzeichnung dieser
          -- Sitzung liegt, bleibt die Zeile stehen, sonst gaebe es ein Video ohne jede
          -- Auskunft darueber, wozu es gehoert.
          delete_after            INTEGER NOT NULL,
          redacted_at             INTEGER,
          created_at              INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_session_history_started
            ON session_history(started_at);
        -- Auf delete_after allein, nicht zusammengesetzt: Das Aufraeumen fragt mit ODER
        -- zugleich nach started_at, damit ein VERKUERZEN der Frist auch auf Bestehendes
        -- wirkt - fuer diese zweite Haelfte greift idx_session_history_started.
        CREATE INDEX IF NOT EXISTS idx_session_history_due
            ON session_history(delete_after);
        """;
}
