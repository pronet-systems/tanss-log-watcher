using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

public sealed class StateDatabaseTests
{
    /// <summary>Der Aufbau, wie ihn Stand 1 anlegte — ohne die beiden neuen Spalten.</summary>
    private const string SchemaVersionOne = """
        CREATE TABLE queue (
          remote_maintenance_id TEXT    PRIMARY KEY,
          payload               TEXT    NOT NULL,
          created_at            INTEGER NOT NULL,
          attempts              INTEGER NOT NULL DEFAULT 0,
          next_attempt_at       INTEGER NOT NULL DEFAULT 0,
          last_error            TEXT,
          leased_at             INTEGER,
          completed_at          INTEGER,
          state                 TEXT    NOT NULL DEFAULT 'pending'
              CHECK (state IN ('pending','sending','done','failed'))
        );
        CREATE TABLE open_sessions (
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
        CREATE TABLE session_log (
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
          detail                TEXT
        );
        PRAGMA user_version = 1;
        """;

    /// <summary>
    /// Der Aufbau, wie ihn Stand 3 hinterliess — mit den Spalten der Stände 2 und 3, aber
    /// ohne <c>open_sessions.identity_key</c> und ohne <c>session_history</c>.
    /// </summary>
    /// <remarks>
    /// Der Sprung 3 → 5 überspringt einen Stand. Er ist der Fall, den eine Installation
    /// mitbringt, die eine Version übersprungen hat — und der einzige, in dem in einem Lauf
    /// sowohl eine ganze Tabelle als auch eine einzelne Spalte nachzuziehen ist.
    /// </remarks>
    private const string SchemaVersionThree = """
        CREATE TABLE queue (
          remote_maintenance_id TEXT    PRIMARY KEY,
          payload               TEXT    NOT NULL,
          created_at            INTEGER NOT NULL,
          attempts              INTEGER NOT NULL DEFAULT 0,
          next_attempt_at       INTEGER NOT NULL DEFAULT 0,
          last_error            TEXT,
          leased_at             INTEGER,
          completed_at          INTEGER,
          outcome_unknown       INTEGER NOT NULL DEFAULT 0,
          state                 TEXT    NOT NULL DEFAULT 'pending'
              CHECK (state IN ('pending','sending','done','failed'))
        );
        CREATE TABLE open_sessions (
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
        CREATE TABLE session_log (
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
          window_title          TEXT
        );
        CREATE TABLE recordings (
          id                    INTEGER PRIMARY KEY,
          remote_maintenance_id TEXT    NOT NULL,
          relative_path         TEXT    NOT NULL,
          segment               INTEGER NOT NULL DEFAULT 1,
          started_at            INTEGER NOT NULL,
          ended_at              INTEGER,
          recorded_seconds      INTEGER NOT NULL DEFAULT 0,
          bytes                 INTEGER NOT NULL DEFAULT 0,
          delete_after          INTEGER NOT NULL,
          deleted_at            INTEGER,
          delete_reason         TEXT,
          state                 TEXT    NOT NULL DEFAULT 'recording'
              CHECK (state IN ('recording','kept','purged','missing'))
        );
        PRAGMA user_version = 3;
        """;

    /// <summary>Was Stand 4 dem Stand 3 hinzufügte: der Verlauf. Wird danach angewandt.</summary>
    private const string SchemaVersionFour = """
        CREATE TABLE session_history (
          remote_maintenance_id   TEXT    PRIMARY KEY,
          started_at              INTEGER NOT NULL,
          ended_at                INTEGER NOT NULL,
          profile_key             TEXT    NOT NULL DEFAULT '',
          profile_name            TEXT    NOT NULL DEFAULT '',
          destination             TEXT,
          destination_fingerprint TEXT,
          remote_support_type_id  INTEGER NOT NULL DEFAULT 0,
          ticket_id               INTEGER NOT NULL DEFAULT 0,
          tanss_support_id        INTEGER,
          disposition             TEXT    NOT NULL
              CHECK (disposition IN ('enqueued','booked','discarded','not_mapped',
                                     'dry_run','failed','unknown')),
          reason                  TEXT    NOT NULL DEFAULT '',
          ended_estimated         INTEGER NOT NULL DEFAULT 0,
          origin                  TEXT    NOT NULL DEFAULT 'live'
              CHECK (origin IN ('live','backfill')),
          delete_after            INTEGER NOT NULL,
          redacted_at             INTEGER,
          created_at              INTEGER NOT NULL
        );
        PRAGMA user_version = 4;
        """;

    /// <summary>Eine laufende Sitzung, wie sie ein Lauf vor Stand 5 hinterliess.</summary>
    private const string RunningSessionBeforeFive = """
        INSERT INTO open_sessions (remote_maintenance_id, monitor_key,
            remote_support_type_id, started_at, last_seen_at, process_id, target,
            device_name, comment)
        VALUES ('sitzung-offen', 'rdp', 1001, 1757000000, 1757000600, 4711,
                'kunde-ts01', 'kunde-ts01', 'Remotedesktop: kunde-ts01');
        """;

    [Fact]
    public void Der_Sprung_von_Stand_4_auf_5_holt_die_Bezeichnerspalte_nach()
    {
        // Eine SPALTE in einer vorhandenen Tabelle: CREATE TABLE IF NOT EXISTS ruehrt die
        // nicht an, nur EnsureColumn zieht sie nach. Ohne diese Zeile scheiterte JEDE
        // Abfrage der laufenden Sitzungen beim naechsten Start - also genau das, was die
        // Tabelle retten soll.
        using TempDirectory temp = new();
        string path = temp.File("state.db");

        Raw.Execute(path, SchemaVersionThree);
        Raw.Execute(path, SchemaVersionFour);
        Raw.Execute(path, RunningSessionBeforeFive);

        using (StateDatabase database = new(path))
        {
            Assert.Equal(4, database.PreviousSchemaVersion);

            using UploadQueue queue = new(database);

            // Die alte Zeile ist noch da und traegt KEINEN Bezeichner. "null" heisst hier
            // "nicht ermittelt" - ein Ersatzwert waere eine erfundene Angabe, und aus einer
            // erfundenen Kennung entstuende in TANSS eine falsche Firmenzuordnung.
            OpenSession old = Assert.Single(queue.LoadOpenSessions());
            Assert.Equal("sitzung-offen", old.RemoteMaintenanceId);
            Assert.Null(old.IdentityKey);

            // Und ab jetzt haelt die Spalte, was sie soll.
            queue.SaveOpenSession(old with { IdentityKey = "kunde-ts01.kunde.local" });
            Assert.Equal("kunde-ts01.kunde.local",
                Assert.Single(queue.LoadOpenSessions()).IdentityKey);
        }

        // Gegen die Konstante und nicht gegen eine Zahl: Der naechste Stand soll diese
        // Pruefung nicht umbringen, sondern sie mitnehmen.
        Assert.Equal(StateDatabase.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                     Raw.Text(path, "PRAGMA user_version;"));
    }

    [Fact]
    public void Eine_Datei_vom_Stand_3_kommt_auch_durch()
    {
        // Der uebersprungene Stand: eine Installation, die eine Version ausgelassen hat.
        // Hier ist in EINEM Lauf beides nachzuziehen - eine ganze Tabelle (session_history,
        // Stand 4) und eine einzelne Spalte (identity_key, Stand 5).
        using TempDirectory temp = new();
        string path = temp.File("state.db");

        Raw.Execute(path, SchemaVersionThree);
        Raw.Execute(path, RunningSessionBeforeFive);

        using (StateDatabase database = new(path))
        {
            Assert.Equal(3, database.PreviousSchemaVersion);

            using UploadQueue queue = new(database);
            OpenSession old = Assert.Single(queue.LoadOpenSessions());
            Assert.Null(old.IdentityKey);

            queue.SaveOpenSession(old with { IdentityKey = "kunde-ts01.kunde.local" });
            Assert.Equal("kunde-ts01.kunde.local",
                Assert.Single(queue.LoadOpenSessions()).IdentityKey);
        }

        Assert.Equal(StateDatabase.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                     Raw.Text(path, "PRAGMA user_version;"));
        // Die Tabelle aus Stand 4 steht ebenfalls - der uebersprungene Stand ist mitgekommen.
        Assert.Equal("1", Raw.Text(path,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' "
            + "AND name = 'session_history';"));

        // Und die Spalte aus Stand 6 ebenso. Sie entscheidet, ob eine Sitzung auf den
        // Abschlussdialog wartet; fehlte sie, scheiterte jede Abfrage der Warteschlange.
        Assert.Equal("1", Raw.Text(path,
            "SELECT COUNT(*) FROM pragma_table_info('queue') WHERE name = 'awaiting_decision';"));
    }

    /// <summary>
    /// Das Beenden wartet auf eine laufende Abfrage.
    /// </summary>
    /// <remarks>
    /// <para>Beim geordneten Beenden schreibt der Beobachtungstakt erfahrungsgemaess noch,
    /// waehrend abgebaut wird. Ohne Sperre im <c>Dispose</c> schloesse das die Verbindung
    /// mitten in der Abfrage — und der Eintrag waere fort.</para>
    ///
    /// <para><b>Warum hier nichts mehr an der Wanduhr haengt.</b> Die frühere Fassung reihte
    /// die Abfrage mit <c>Task.Run</c> in den Strangpool ein und wartete fünf Sekunden darauf,
    /// dass sie anfängt. Auf dem Bauläufer — zwei Kerne, alle Testprojekte gleichzeitig — fing
    /// sie in diesen fünf Sekunden nicht an, und der Test scheiterte an der Warteschlange des
    /// Strangpools statt an der Datenbank (Lauf 34820163227, fehlgeschlagen nach 5 s; derselbe
    /// Quelltext im Lauf 34786953205 grün — genau das Muster eines Zeitfehlers). Die Abfrage
    /// läuft deshalb auf einem eigenen Strang, der niemandem hinten ansteht, und sie endet
    /// nicht nach einer Schlafzeit, sondern auf ein Signal aus diesem Test.</para>
    ///
    /// <para><b>Die Zusicherung ist dadurch schärfer geworden, nicht weicher.</b> Vorher wurde
    /// aus „nach 400 ms Schlaf war <c>finished</c> gesetzt“ nur <i>geschlossen</i>, dass
    /// <c>Dispose</c> gewartet hat. Jetzt wird es gemessen: <c>Dispose</c> läuft auf einem
    /// eigenen Strang und darf, solange die Abfrage in der Sperre steht, <b>nicht</b>
    /// zurückkehren.</para>
    /// </remarks>
    [Fact]
    public void Das_Beenden_wartet_auf_eine_laufende_Abfrage()
    {
        using TempDirectory temp = new();
        StateDatabase database = new(temp.File("state.db"));

        using ManualResetEventSlim inside = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim disposed = new();
        bool finished = false;

        // Eigener Strang statt Strangpool: Dieser hier laeuft sofort los, ganz gleich, wie
        // besetzt der Pool gerade ist. Genau daran scheiterte der Test auf dem Baulaeufer.
        Thread query = new(() => database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM queue;";
            _ = command.ExecuteScalar();

            inside.Set();
            release.Wait();
            finished = true;
        }))
        {
            IsBackground = true,
            Name = "Abfrage",
        };

        Thread closing = new(() =>
        {
            database.Dispose();
            disposed.Set();
        })
        {
            IsBackground = true,
            Name = "Beenden",
        };

        query.Start();

        // Kein Mass, sondern eine Reissleine gegen einen Haenger: Der Strang laeuft, die
        // Abfrage ist ein COUNT auf einer leeren Tabelle. Eine halbe Minute ist dafuer so
        // weit jenseits von allem, dass ein Ablauf hier nur noch eine Verklemmung sein kann -
        // und die soll als roter Test enden und nicht als Lauf, der in sein Zeitlimit rennt.
        Assert.True(inside.Wait(TimeSpan.FromSeconds(30)),
            "Die Abfrage hat die Sperre nie betreten.");

        closing.Start();

        // Der Kern des Falls. Die 200 ms sind keine Grenze, an der etwas scheitern kann:
        // Je langsamer der Rechner, desto sicherer steht die Abfrage noch in der Sperre und
        // desto sicherer haelt diese Zusicherung. Durchfallen kann hier nur ein Dispose, das
        // die Sperre gar nicht erst nimmt - und das ist der Fehler, um den es geht.
        Assert.False(disposed.Wait(TimeSpan.FromMilliseconds(200)),
            "Das Beenden kam zurueck, waehrend die Abfrage noch in der Sperre stand. Die "
            + "Verbindung waere mitten im Lesen geschlossen worden.");
        Assert.False(finished, "Die Abfrage war fertig, bevor der Test sie freigegeben hat.");

        release.Set();

        Assert.True(disposed.Wait(TimeSpan.FromSeconds(30)),
            "Das Beenden kam nach dem Ende der Abfrage nicht zurueck.");
        Assert.True(finished);

        query.Join();
        closing.Join();
    }

    [Fact]
    public void Nach_dem_Beenden_ist_jeder_Zugriff_ein_klarer_Fehler()
    {
        using TempDirectory temp = new();
        StateDatabase database = new(temp.File("state.db"));
        database.Dispose();
        // Zweimal Beenden ist kein Fehler, sondern der Normalfall im geordneten Abbau.
        database.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => database.Execute(_ => 1));
    }

    [Fact]
    public void Eine_Datei_vom_vorigen_Stand_wird_nachgezogen_statt_abgelehnt()
    {
        using TempDirectory temp = new();
        string path = temp.File("state.db");
        string payload = JsonSerializer.Serialize(Sample.Upload("sitzung-alt"));

        Raw.Execute(path, SchemaVersionOne);
        Raw.Execute(path,
            "INSERT INTO queue (remote_maintenance_id, payload, created_at) "
            + $"VALUES ('sitzung-alt', '{payload}', 1757000000);");

        using StateDatabase database = new(path);
        using UploadQueue queue = new(database);
        using SessionLog log = new(database);

        // Die Sitzung aus der alten Datei ist noch da und laesst sich ausliefern ...
        QueuedUpload leased = Assert.Single(queue.Lease(10));
        Assert.Equal("sitzung-alt", leased.RemoteMaintenanceId);
        Assert.False(leased.OutcomeUnknown);

        // ... und die neuen Spalten stehen.
        _ = log.Append(new SessionLogEntry
        {
            RemoteMaintenanceId = "sitzung-alt",
            Operation = "session.opened",
            Outcome = SessionOutcome.Ok,
            Trigger = SessionTrigger.Startup,
            Reason = "Nach der Überführung",
            WindowTitle = "kunde-srv01 — Remotedesktop",
        });

        Assert.NotNull(Assert.Single(log.ForSession("sitzung-alt")).WindowTitle);
    }
}
