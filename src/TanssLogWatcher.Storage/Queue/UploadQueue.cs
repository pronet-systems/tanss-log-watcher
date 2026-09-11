using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.Storage.Queue;

/// <summary>
/// Die Warteschlange, umgesetzt auf der Zustandsdatenbank.
/// </summary>
/// <remarks>
/// <para>Alle Zeitangaben stehen als Unix-<b>Sekunden</b> in der Datenbank und laufen über
/// <see cref="TanssTime"/>. Es gibt hier bewusst keine zweite Zeitrechnung: Die Nutzlast
/// trägt dieselben Sekunden, und eine Datenbankspalte in Millisekunden wäre der sicherste
/// Weg, dass beim nächsten Umbau die falsche in das Feld <c>startTime</c> gerät.</para>
///
/// <para>Die Uhr kommt über einen <see cref="TimeProvider"/> herein. Nicht aus Reinheit,
/// sondern weil sich Fälligkeit, Rückstau und das Zurückholen hängengebliebener Einträge
/// sonst nur durch Warten prüfen ließen — und damit gar nicht.</para>
///
/// <para><b>Eine kaputte Zeile hält die Warteschlange nicht an.</b> Die Warteschlange ist
/// der Baustein, der verhindern soll, dass eine Sitzung verlorengeht; eine einzelne
/// unlesbare Nutzlast, die jeden Lease abbrechen ließe, machte genau das zunichte — kein
/// gesunder Eintrag käme danach noch bei TANSS an. Deshalb wird die unlesbare Zeile für
/// sich auf <see cref="QueueState.Failed"/> gesetzt und mit einem sprechenden
/// <c>last_error</c> versehen, während die übrigen ausgeliefert werden.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[SuppressMessage("Naming", "CA1711",
    Justification = "Die Endung „Queue“ ist hier die Sache selbst: eine Warteschlange, "
        + "keine Ableitung von System.Collections.Queue. Ein Deckname wie „UploadBuffer“ "
        + "verschleierte, worum es geht.")]
public sealed class UploadQueue : IUploadQueue
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.General);

    private readonly StateDatabase _database;
    private readonly bool _ownsDatabase;
    private readonly TimeProvider _time;
    private int _disposed;

    /// <summary>Öffnet die Warteschlange auf einer eigenen Datenbank am angegebenen Pfad.</summary>
    public UploadQueue(string path, TimeProvider? timeProvider = null)
        : this(new StateDatabase(path), timeProvider, ownsDatabase: true)
    {
    }

    /// <summary>Öffnet die Warteschlange auf einer bereits geöffneten Datenbank.</summary>
    /// <remarks>
    /// So teilen sich Warteschlange und Änderungsprotokoll eine Verbindung. Das ist der
    /// Normalfall im Dienst: Beide gehören zu demselben Vorgang und sollen auch dieselbe
    /// Sperre benutzen.
    /// </remarks>
    public UploadQueue(StateDatabase database, TimeProvider? timeProvider = null)
        : this(database, timeProvider, ownsDatabase: false)
    {
    }

    private UploadQueue(StateDatabase database, TimeProvider? timeProvider, bool ownsDatabase)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _ownsDatabase = ownsDatabase;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Die zugrundeliegende Datenbank.</summary>
    public StateDatabase Database => _database;

    /// <inheritdoc/>
    public bool Enqueue(RemoteSupportWrite item, TimeSpan? hold = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.RemoteMaintenanceId);

        DateTimeOffset utcNow = _time.GetUtcNow();
        long now = TanssTime.ToUnixSeconds(utcNow);

        // Die Schonfrist steht in next_attempt_at und nicht in einem Merker daneben: Lease()
        // liest ohnehin genau diese Spalte, und damit ueberlebt die Frist jeden Neustart. Ein
        // Merker im Arbeitsspeicher waere nach einem Absturz weg - und der Eintrag ginge
        // ausgerechnet dann sofort hinaus, wenn der Bericht verloren ist.
        long due = hold is { } grace && grace > TimeSpan.Zero
            ? TanssTime.ToUnixSeconds(utcNow + grace)
            : now;

        string payload = JsonSerializer.Serialize(item, PayloadJson);

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO queue (remote_maintenance_id, payload, created_at, attempts,
                                   next_attempt_at, state)
                VALUES ($id, $payload, $now, 0, $due, 'pending')
                ON CONFLICT(remote_maintenance_id) DO NOTHING;
                """;
            _ = command.Parameters.AddWithValue("$id", item.RemoteMaintenanceId);
            _ = command.Parameters.AddWithValue("$payload", payload);
            _ = command.Parameters.AddWithValue("$now", now);
            _ = command.Parameters.AddWithValue("$due", due);
            return command.ExecuteNonQuery() > 0;
        });
    }

    /// <inheritdoc/>
    public bool Release(string remoteMaintenanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteMaintenanceId);

        long now = TanssTime.ToUnixSeconds(_time.GetUtcNow());

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE queue
                   SET next_attempt_at = $now
                 WHERE remote_maintenance_id = $id
                   AND state = 'pending';
                """;
            _ = command.Parameters.AddWithValue("$id", remoteMaintenanceId);
            _ = command.Parameters.AddWithValue("$now", now);
            return command.ExecuteNonQuery() > 0;
        });
    }

    /// <inheritdoc/>
    public bool UpdatePayload(RemoteSupportWrite payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.RemoteMaintenanceId);

        string json = JsonSerializer.Serialize(payload, PayloadJson);

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();

            // state = 'pending' ist die ganze Sicherung dieses Vorgangs: Zwischen dem Lesen im
            // Dialog und dem Schreiben hier kann der Sendedienst den Eintrag geleast haben.
            // Die Bedingung steht deshalb in der Anweisung selbst und nicht in einer vorherigen
            // Abfrage - dazwischen laege genau das Zeitfenster, das sie schliessen soll.
            command.CommandText = """
                UPDATE queue
                   SET payload = $payload
                 WHERE remote_maintenance_id = $id
                   AND state = 'pending';
                """;
            _ = command.Parameters.AddWithValue("$id", payload.RemoteMaintenanceId);
            _ = command.Parameters.AddWithValue("$payload", json);
            return command.ExecuteNonQuery() > 0;
        });
    }

    /// <inheritdoc/>
    public bool Remove(string remoteMaintenanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteMaintenanceId);

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM queue
                 WHERE remote_maintenance_id = $id
                   AND state = 'pending';
                """;
            _ = command.Parameters.AddWithValue("$id", remoteMaintenanceId);
            return command.ExecuteNonQuery() > 0;
        });
    }

    /// <inheritdoc/>
    public IReadOnlyList<QueuedUpload> Lease(int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);

        long now = TanssTime.ToUnixSeconds(_time.GetUtcNow());

        return _database.Execute<IReadOnlyList<QueuedUpload>>(connection =>
        {
            // BEGIN IMMEDIATE nimmt die Schreibsperre sofort, nicht erst beim ersten
            // Schreiben. Ohne sie koennten zwei Prozesse dieselbe Auswahl lesen und
            // denselben Eintrag zweimal ausgeben - und TANSS dedupliziert nicht.
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            List<QueuedUpload> leased = [];
            List<(string Id, string Cause)> unreadable = [];

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    UPDATE queue
                       SET state = 'sending', leased_at = $now
                     WHERE remote_maintenance_id IN (
                           SELECT remote_maintenance_id
                             FROM queue
                            WHERE state = 'pending' AND next_attempt_at <= $now
                            ORDER BY next_attempt_at, created_at
                            LIMIT $max)
                    RETURNING {Columns};
                    """;
                _ = command.Parameters.AddWithValue("$now", now);
                _ = command.Parameters.AddWithValue("$max", max);

                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (TryMap(reader, out QueuedUpload? item, out string? cause))
                    {
                        leased.Add(item);
                    }
                    else
                    {
                        unreadable.Add((reader.GetString(0), cause));
                    }
                }
            }

            // Erst jetzt, nach dem Schliessen des Lesers: Auf derselben Verbindung laesst
            // sich waehrend eines offenen Lesers kein zweiter Befehl ausfuehren.
            foreach ((string id, string cause) in unreadable)
            {
                Fail(connection, transaction, id, cause + " " + ManualRecovery, nextAttempt: null);
            }

            transaction.Commit();

            // RETURNING liefert in Speicherreihenfolge, nicht in der Reihenfolge der
            // Auswahl. Die Sortierung im UPDATE entscheidet also nur, WELCHE Eintraege
            // drankommen - die Reihenfolge, in der sie gesendet werden, entsteht hier.
            leased.Sort(static (left, right) =>
            {
                int byDue = left.NextAttemptAt.CompareTo(right.NextAttemptAt);
                return byDue != 0 ? byDue : left.CreatedAt.CompareTo(right.CreatedAt);
            });

            return leased;
        });
    }

    /// <inheritdoc/>
    public void MarkDone(string remoteMaintenanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteMaintenanceId);
        long now = TanssTime.ToUnixSeconds(_time.GetUtcNow());

        _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE queue
                   SET state = 'done', leased_at = NULL, last_error = NULL,
                       completed_at = $now, attempts = attempts + 1, outcome_unknown = 0
                 WHERE remote_maintenance_id = $id;
                """;
            _ = command.Parameters.AddWithValue("$id", remoteMaintenanceId);
            _ = command.Parameters.AddWithValue("$now", now);
            _ = command.ExecuteNonQuery();
        });
    }

    /// <inheritdoc/>
    public void MarkFailed(string remoteMaintenanceId, string errorText, DateTimeOffset? nextAttempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteMaintenanceId);

        _database.Execute(connection =>
            Fail(connection, null, remoteMaintenanceId, errorText, nextAttempt));
    }

    /// <inheritdoc/>
    public int RequeueStuck(TimeSpan olderThan)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(olderThan, TimeSpan.Zero);

        DateTimeOffset now = _time.GetUtcNow();
        long cutoff = TanssTime.ToUnixSeconds(now - olderThan);

        return _database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            List<(string Id, int Attempts)> stuck = [];
            using (SqliteCommand find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = """
                    SELECT remote_maintenance_id, attempts
                      FROM queue
                     WHERE state = 'sending' AND COALESCE(leased_at, 0) <= $cutoff;
                    """;
                _ = find.Parameters.AddWithValue("$cutoff", cutoff);

                using SqliteDataReader reader = find.ExecuteReader();
                while (reader.Read())
                {
                    stuck.Add((reader.GetString(0), reader.GetInt32(1)));
                }
            }

            foreach ((string id, int attempts) in stuck)
            {
                // Der Versuchszaehler steigt mit, obwohl TANSS nichts abgelehnt hat. Sonst
                // liefe ein Werkzeug, das beim Senden zuverlaessig abstuerzt, ohne jeden
                // Rueckstau im Kreis. Genau deshalb taugt er aber nicht als Kennzeichen fuer
                // den ungeklaerten Ausgang - dafuer steht outcome_unknown.
                Fail(connection, transaction, id,
                    "Beim Senden abgebrochen (Absturz oder Neustart). Vor dem naechsten "
                    + "Versuch pruefen, ob die Fernwartung bereits in TANSS steht.",
                    Backoff.NextAttemptAfter(now, attempts + 1), outcomeUnknown: true);
            }

            transaction.Commit();
            return stuck.Count;
        });
    }

    /// <inheritdoc/>
    public QueuedUpload? Find(string remoteMaintenanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteMaintenanceId);

        return _database.Execute<QueuedUpload?>(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT {Columns} FROM queue WHERE remote_maintenance_id = $id;";
            _ = command.Parameters.AddWithValue("$id", remoteMaintenanceId);

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            // Hier wird nach genau diesem einen Eintrag gefragt; ihn stillschweigend zu
            // ueberspringen hiesse, „nicht vorhanden“ zu antworten, obwohl die Zeile steht.
            return TryMap(reader, out QueuedUpload? item, out string? cause)
                ? item
                : throw new StateDatabaseException(cause + " " + ManualRecovery);
        });
    }

    /// <inheritdoc/>
    public QueueListing List(QueueState state, int limit = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {Columns} FROM queue WHERE state = $state
                 ORDER BY created_at DESC LIMIT $limit;
                """;
            _ = command.Parameters.AddWithValue("$state", ToText(state));
            _ = command.Parameters.AddWithValue("$limit", limit);

            List<QueuedUpload> items = [];
            int unreadable = 0;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                // Eine unlesbare Zeile darf die Anzeige nicht kosten: Der Techniker saehe
                // sonst gar nichts mehr. Sie bleibt hier bewusst ohne Schreibzugriff - der
                // naechste Lease setzt sie auf „failed“, wo sie mit ihrem Grund auffaellt.
                // Stillschweigend uebergehen darf man sie trotzdem nicht: Count zaehlt sie
                // ueber SQL mit, und die beiden Angaben widersprechen sich sonst.
                if (TryMap(reader, out QueuedUpload? item, out _))
                {
                    items.Add(item);
                }
                else
                {
                    unreadable++;
                }
            }

            return new QueueListing { Items = items, Unreadable = unreadable };
        });
    }

    /// <inheritdoc/>
    public int Count(QueueState state) => _database.Execute(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM queue WHERE state = $state;";
        _ = command.Parameters.AddWithValue("$state", ToText(state));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <inheritdoc/>
    public int PurgeCompleted(TimeSpan olderThan)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(olderThan, TimeSpan.Zero);
        long cutoff = TanssTime.ToUnixSeconds(_time.GetUtcNow() - olderThan);

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM queue
                 WHERE state = 'done' AND COALESCE(completed_at, created_at) <= $cutoff;
                """;
            _ = command.Parameters.AddWithValue("$cutoff", cutoff);
            return command.ExecuteNonQuery();
        });
    }

    /// <inheritdoc/>
    public void SaveOpenSession(OpenSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.RemoteMaintenanceId);

        _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO open_sessions (remote_maintenance_id, monitor_key,
                    remote_support_type_id, started_at, last_seen_at, process_id, target,
                    device_name, user_name, comment, ticket_id)
                VALUES ($id, $key, $type, $started, $seen, $pid, $target, $device, $user,
                        $comment, $ticket)
                ON CONFLICT(remote_maintenance_id) DO UPDATE SET
                    monitor_key = excluded.monitor_key,
                    remote_support_type_id = excluded.remote_support_type_id,
                    last_seen_at = excluded.last_seen_at,
                    process_id = excluded.process_id,
                    target = excluded.target,
                    device_name = excluded.device_name,
                    user_name = excluded.user_name,
                    comment = excluded.comment,
                    ticket_id = excluded.ticket_id;
                """;
            _ = command.Parameters.AddWithValue("$id", session.RemoteMaintenanceId);
            _ = command.Parameters.AddWithValue("$key", session.MonitorKey);
            _ = command.Parameters.AddWithValue("$type", session.RemoteSupportTypeId);
            _ = command.Parameters.AddWithValue("$started", TanssTime.ToUnixSeconds(session.StartedAt));
            _ = command.Parameters.AddWithValue("$seen", TanssTime.ToUnixSeconds(session.LastSeenAt));
            _ = command.Parameters.AddWithValue("$pid", session.ProcessId);
            _ = command.Parameters.AddWithValue("$target", (object?)session.Target ?? DBNull.Value);
            _ = command.Parameters.AddWithValue("$device", (object?)session.DeviceName ?? DBNull.Value);
            _ = command.Parameters.AddWithValue("$user", (object?)session.UserName ?? DBNull.Value);
            _ = command.Parameters.AddWithValue("$comment", session.Comment);
            _ = command.Parameters.AddWithValue("$ticket", session.TicketId);
            _ = command.ExecuteNonQuery();
        });
    }

    /// <inheritdoc/>
    public bool RemoveOpenSession(string remoteMaintenanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteMaintenanceId);

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM open_sessions WHERE remote_maintenance_id = $id;";
            _ = command.Parameters.AddWithValue("$id", remoteMaintenanceId);
            return command.ExecuteNonQuery() > 0;
        });
    }

    /// <inheritdoc/>
    public IReadOnlyList<OpenSession> LoadOpenSessions() =>
        _database.Execute<IReadOnlyList<OpenSession>>(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT remote_maintenance_id, monitor_key, remote_support_type_id, started_at,
                       last_seen_at, process_id, target, device_name, user_name, comment,
                       ticket_id
                  FROM open_sessions
                 ORDER BY started_at;
                """;

            List<OpenSession> sessions = [];
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                // Dies ist der erste Griff nach einem Neustart. Eine einzelne unlesbare
                // Zeile kostete hier sonst JEDE laufende Sitzung - also genau das, was diese
                // Tabelle retten soll. Sie wird uebergangen, die uebrigen kommen zurueck.
                if (TryMapSession(reader, out OpenSession? session))
                {
                    sessions.Add(session);
                }
            }

            return sessions;
        });

    /// <inheritdoc/>
    /// <remarks>
    /// Der Riegel ist unteilbar: Beim geordneten Beenden schreibt der Beobachtungstakt
    /// erfahrungsgemäß noch, während abgebaut wird. Ein gewöhnliches <c>bool</c> ließe zwei
    /// Aufrufer gleichzeitig durch.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsDatabase)
        {
            _database.Dispose();
        }
    }

    private const string Columns =
        "remote_maintenance_id, payload, created_at, attempts, next_attempt_at, last_error, "
        + "leased_at, completed_at, state, outcome_unknown";

    /// <summary>Was der Techniker mit einer unlesbaren Zeile zu tun hat.</summary>
    /// <remarks>
    /// Der Hinweis auf die Existenzprüfung gehört in den Text und nicht in die
    /// Dokumentation: TANSS dedupliziert nicht, und wer die Zeile von Hand nachbucht, legt
    /// sonst nachweislich einen zweiten Datensatz an.
    /// </remarks>
    private const string ManualRecovery =
        "Der Eintrag lässt sich daraus nicht mehr herstellen und steht auf „failed“; die "
        + "übrigen Einträge der Warteschlange sind davon unberührt. Ob die Fernwartung "
        + "trotzdem schon in TANSS steht, ist von hier aus nicht zu erkennen — TANSS "
        + "dedupliziert nicht. Vor einer Nachbuchung von Hand deshalb erst mit "
        + "PUT /api/v1/remoteSupports nach der remoteMaintenanceId suchen.";

    private static void Fail(SqliteConnection connection, SqliteTransaction? transaction,
                             string id, string errorText, DateTimeOffset? nextAttempt,
                             bool outcomeUnknown = false)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        // Das Kennzeichen wird hier nur gesetzt, nie geloescht: Ein misslungener Versuch
        // klaert einen frueher abgebrochenen nicht auf. Geloescht wird es allein von
        // MarkDone - dann steht die Fernwartung nachweislich in TANSS.
        command.CommandText = $"""
            UPDATE queue
               SET state = $state, attempts = attempts + 1, last_error = $error,
                   next_attempt_at = $next, leased_at = NULL{(outcomeUnknown ? ", outcome_unknown = 1" : string.Empty)}
             WHERE remote_maintenance_id = $id;
            """;
        _ = command.Parameters.AddWithValue("$id", id);
        _ = command.Parameters.AddWithValue("$error", errorText ?? string.Empty);
        _ = command.Parameters.AddWithValue("$state", nextAttempt is null ? "failed" : "pending");
        _ = command.Parameters.AddWithValue("$next",
            nextAttempt is { } moment ? TanssTime.ToUnixSeconds(moment) : 0L);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Wandelt eine Zeile um und sagt bei Misserfolg, <b>warum</b> — statt zu werfen.
    /// </summary>
    /// <remarks>
    /// Der Unterschied ist der ganze Punkt: Eine Ausnahme aus der Leseschleife heraus rollt
    /// die Transaktion zurück und nimmt die gesunden Einträge mit. Der Aufrufer entscheidet
    /// hier selbst, was eine kaputte Zeile kostet — in <c>Lease</c> genau sich selbst.
    /// </remarks>
    private static bool TryMap(SqliteDataReader reader,
                               [NotNullWhen(true)] out QueuedUpload? item,
                               [NotNullWhen(false)] out string? cause)
    {
        string id = reader.GetString(0);

        RemoteSupportWrite? payload;
        try
        {
            payload = JsonSerializer.Deserialize<RemoteSupportWrite>(reader.GetString(1), PayloadJson);
        }
        catch (JsonException exception)
        {
            item = null;
            cause = $"Die Nutzlast des Warteschlangeneintrags {id} ist unlesbar: "
                + exception.Message;
            return false;
        }

        if (payload is null)
        {
            item = null;
            cause = $"Der Warteschlangeneintrag {id} enthält eine leere Nutzlast.";
            return false;
        }

        if (!TryFromText(reader.GetString(8), out QueueState state))
        {
            item = null;
            cause = $"Der Warteschlangeneintrag {id} trägt den unbekannten Zustand "
                + $"„{reader.GetString(8)}“. Die Datenbank stammt vermutlich aus einer "
                + "neueren Programmfassung.";
            return false;
        }

        try
        {
            item = new QueuedUpload
            {
                RemoteMaintenanceId = id,
                Payload = payload,
                CreatedAt = Moment(reader.GetInt64(2)),
                Attempts = reader.GetInt32(3),
                NextAttemptAt = Moment(reader.GetInt64(4)),
                LastError = reader.IsDBNull(5) ? null : reader.GetString(5),
                LeasedAt = MomentOrNull(reader, 6),
                CompletedAt = MomentOrNull(reader, 7),
                State = state,
                OutcomeUnknown = reader.GetInt64(9) != 0,
            };
        }
        catch (ArgumentOutOfRangeException exception)
        {
            item = null;
            cause = $"Ein Zeitwert des Warteschlangeneintrags {id} liegt ausserhalb des "
                + "darstellbaren Bereichs - dort stehen vermutlich Millisekunden statt "
                + $"Sekunden ({exception.Message}).";
            return false;
        }

        cause = null;
        return true;
    }

    /// <summary>Wandelt eine Zeile der laufenden Sitzungen um. <c>false</c>, wenn unlesbar.</summary>
    private static bool TryMapSession(SqliteDataReader reader,
                                      [NotNullWhen(true)] out OpenSession? session)
    {
        try
        {
            session = new OpenSession
            {
                RemoteMaintenanceId = reader.GetString(0),
                MonitorKey = reader.GetString(1),
                RemoteSupportTypeId = reader.GetInt32(2),
                StartedAt = Moment(reader.GetInt64(3)),
                LastSeenAt = Moment(reader.GetInt64(4)),
                ProcessId = reader.GetInt32(5),
                Target = reader.IsDBNull(6) ? null : reader.GetString(6),
                DeviceName = reader.IsDBNull(7) ? null : reader.GetString(7),
                UserName = reader.IsDBNull(8) ? null : reader.GetString(8),
                Comment = reader.GetString(9),
                TicketId = reader.GetInt32(10),
            };
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            // Der haeufigste Fall: In der Zeitspalte stehen Millisekunden. Instant und
            // DateTimeOffset lesen das als Datum im Jahr 51667 - hier faellt es auf.
            session = null;
            return false;
        }
        catch (InvalidCastException)
        {
            // Ein Wert, der nicht zum Spaltentyp passt - SQLite laesst das zu, und ein von
            // Hand berichtigter Datenbestand bringt es mit.
            session = null;
            return false;
        }
        catch (FormatException)
        {
            session = null;
            return false;
        }
        catch (OverflowException)
        {
            session = null;
            return false;
        }
        catch (SqliteException)
        {
            session = null;
            return false;
        }
    }

    /// <summary>Liest eine Sekundenangabe aus einer Spalte, die immer gesetzt ist.</summary>
    /// <remarks>
    /// Der Umweg über <see cref="TanssTime"/> ist Absicht: Es ist der einzige Umrechner des
    /// Projekts, und eine zweite Zeitrechnung an dieser Stelle wäre der sicherste Weg,
    /// irgendwann Millisekunden in <c>startTime</c> zu bekommen. Dass
    /// <see cref="TanssTime.FromUnixSeconds"/> die 0 als „nicht gesetzt“ liest, passt hier
    /// nicht: <c>created_at</c> steht immer, und in <c>next_attempt_at</c> heißt 0
    /// „sofort fällig“ — so schreibt <c>Fail</c> es beim Aufgeben. Daher der ausdrückliche
    /// Rückfall auf den Beginn der Unix-Zeit.
    /// </remarks>
    private static DateTimeOffset Moment(long seconds) =>
        TanssTime.FromUnixSeconds(seconds) ?? DateTimeOffset.UnixEpoch;

    /// <summary>Liest eine Sekundenangabe, die auch fehlen darf.</summary>
    /// <remarks>
    /// Hier gilt die Lesart von <see cref="TanssTime.FromUnixSeconds"/> unverändert: Eine 0
    /// heißt „nicht gesetzt“ und ergibt <c>null</c>. Ein Zeitpunkt im Jahr 1970 wäre die
    /// falschere Antwort — er sähe aus wie eine Angabe.
    /// </remarks>
    private static DateTimeOffset? MomentOrNull(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : TanssTime.FromUnixSeconds(reader.GetInt64(index));

    private static string ToText(QueueState state) => state switch
    {
        QueueState.Pending => "pending",
        QueueState.Sending => "sending",
        QueueState.Done => "done",
        QueueState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unbekannter Zustand."),
    };

    /// <summary>
    /// Liest den Zustand. <c>false</c> bei einem unbekannten Wert.
    /// </summary>
    /// <remarks>
    /// Auch das wirft bewusst nicht: Die Spalte trägt zwar eine CHECK-Bedingung, die einen
    /// fremden Wert heute ausschließt — aber ein Werfen aus der Leseschleife heraus kostete
    /// wieder den ganzen Durchlauf, und diese Zeile ist es allein.
    /// </remarks>
    private static bool TryFromText(string text, out QueueState state)
    {
        switch (text)
        {
            case "pending": state = QueueState.Pending; return true;
            case "sending": state = QueueState.Sending; return true;
            case "done": state = QueueState.Done; return true;
            case "failed": state = QueueState.Failed; return true;
            default: state = default; return false;
        }
    }
}
