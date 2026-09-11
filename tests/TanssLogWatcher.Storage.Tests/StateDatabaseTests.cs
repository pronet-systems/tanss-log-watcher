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

    [Fact]
    public async Task Das_Beenden_wartet_auf_eine_laufende_Abfrage()
    {
        // Beim geordneten Beenden schreibt der Beobachtungstakt erfahrungsgemaess noch,
        // waehrend abgebaut wird. Ohne Sperre im Dispose schloesse das die Verbindung
        // mitten in der Abfrage - und der Eintrag waere fort.
        using TempDirectory temp = new();
        StateDatabase database = new(temp.File("state.db"));

        using ManualResetEventSlim inside = new();
        bool finished = false;

        Task work = Task.Run(() => database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM queue;";
            _ = command.ExecuteScalar();

            inside.Set();
            Thread.Sleep(400);
            finished = true;
        }));

        Assert.True(inside.Wait(TimeSpan.FromSeconds(5)));
        database.Dispose();

        Assert.True(finished);
        await work;
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
