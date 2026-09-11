using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Secrets;

namespace TanssLogWatcher.Storage.Logging;

/// <summary>
/// Das Änderungsprotokoll in der Zustandsdatenbank.
/// </summary>
/// <remarks>
/// <para>Getrennt von der gewöhnlichen Protokolldatei und bewusst in der Datenbank: Eine
/// Textdatei, die rotiert und gekürzt wird, beantwortet die Frage „was geschah mit dieser
/// Sitzung“ nach zwei Wochen nicht mehr. Hier hängt jeder Eintrag an der Sitzungskennung und
/// ist auch dann noch auffindbar, wenn niemand mehr weiß, an welchem Tag es war.</para>
///
/// <para>Das Protokoll ist der einzige Ort, an dem ein Trockenlauf als solcher steht. Deshalb
/// ist <see cref="SessionOutcome.DryRun"/> ein eigenes Ergebnis: Ein Eintrag, der aussieht
/// wie ein Erfolg, während nichts geschrieben wurde, macht jede spätere Fehlersuche falsch.</para>
///
/// <para>Geschwärzt wird an <b>einer</b> Stelle: Freitext läuft durch
/// <see cref="Redaction"/> — die Fassung der API-Schicht, die auch das Feldmuster
/// <c>apiKey</c> kennt, in der <c>POST /api/v1/login</c> das Token liefert —, und die
/// Fensterbeschriftung zusätzlich durch <c>logging.redact_window_titles</c>. Eine
/// Einstellung, die zusagt zu schwärzen und es nicht tut, ist schlimmer als gar keine: Der
/// Techniker hält Kundennamen für geschützt, während sie im Klartext in <c>state.db</c>
/// stehen.</para>
///
/// <para><b>Verdrahtet wird über <see cref="FromConfig(AppConfig, StateDatabase, TimeProvider)"/>.</b>
/// Die Einstellung von Hand durchzureichen ist möglich, aber genau die Stelle, an der sie
/// beim nächsten Umbau vergessen wird — und ein vergessener Schalter zeigt sich nicht, er
/// schreibt nur Klartext.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SessionLog : IDisposable
{
    private readonly StateDatabase _database;
    private readonly bool _ownsDatabase;
    private readonly TimeProvider _time;
    private readonly bool _redactWindowTitles;
    private readonly Lazy<KeyedFingerprint> _titleFingerprint;
    private int _disposed;

    /// <summary>Öffnet das Protokoll auf einer eigenen Datenbank am angegebenen Pfad.</summary>
    /// <param name="path">Pfad der Zustandsdatenbank.</param>
    /// <param name="timeProvider">Uhr; fehlt sie, gilt die Systemuhr.</param>
    /// <param name="redactWindowTitles">
    /// Aus <c>logging.redact_window_titles</c>. Standardmäßig <c>true</c> wie die
    /// Einstellung selbst — wer den Wert nicht durchreicht, bekommt die Zusage, nicht ihr
    /// Gegenteil.
    /// </param>
    /// <param name="windowTitleFingerprint">
    /// Der Abdruckgeber für Fensterbeschriftungen; fehlt er, wird der Schlüssel neben der
    /// Datenbank geführt und beim ersten Bedarf erzeugt.
    /// </param>
    public SessionLog(string path, TimeProvider? timeProvider = null,
                      bool redactWindowTitles = true,
                      KeyedFingerprint? windowTitleFingerprint = null)
        : this(new StateDatabase(path), timeProvider, redactWindowTitles,
               windowTitleFingerprint, ownsDatabase: true)
    {
    }

    /// <summary>Öffnet das Protokoll auf einer bereits geöffneten Datenbank.</summary>
    /// <param name="database">Die geöffnete Zustandsdatenbank.</param>
    /// <param name="timeProvider">Uhr; fehlt sie, gilt die Systemuhr.</param>
    /// <param name="redactWindowTitles">Aus <c>logging.redact_window_titles</c>.</param>
    /// <param name="windowTitleFingerprint">
    /// Der Abdruckgeber für Fensterbeschriftungen; fehlt er, wird der Schlüssel neben der
    /// Datenbank geführt und beim ersten Bedarf erzeugt.
    /// </param>
    public SessionLog(StateDatabase database, TimeProvider? timeProvider = null,
                      bool redactWindowTitles = true,
                      KeyedFingerprint? windowTitleFingerprint = null)
        : this(database, timeProvider, redactWindowTitles, windowTitleFingerprint,
               ownsDatabase: false)
    {
    }

    private SessionLog(StateDatabase database, TimeProvider? timeProvider,
                       bool redactWindowTitles, KeyedFingerprint? windowTitleFingerprint,
                       bool ownsDatabase)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _ownsDatabase = ownsDatabase;
        _time = timeProvider ?? TimeProvider.System;
        _redactWindowTitles = redactWindowTitles;

        // Traege: Eine Installation, die nie eine Beschriftung protokolliert, legt auch
        // keine Schluesseldatei an. Lazy sperrt dabei selbst - der Beobachtungstakt und ein
        // Handbefehl schreiben durchaus gleichzeitig, und zwei zugleich erzeugte Schluessel
        // ergaeben zwei Abdruecke desselben Fensters.
        _titleFingerprint = windowTitleFingerprint is { } given
            ? new Lazy<KeyedFingerprint>(given)
            : new Lazy<KeyedFingerprint>(
                () => FingerprintKeyStore.BesideDatabase(database.Path).Load());
    }

    /// <summary>
    /// Baut das Protokoll aus der Konfiguration, auf einer bereits geöffneten Datenbank.
    /// </summary>
    /// <param name="config">Die geprüfte Konfiguration.</param>
    /// <param name="database">Dieselbe Datenbank, auf der auch die Warteschlange liegt.</param>
    /// <param name="timeProvider">Uhr; fehlt sie, gilt die Systemuhr.</param>
    /// <remarks>
    /// <b>Der vorgesehene Weg für den Zusammenbau.</b> <c>logging.redact_window_titles</c>
    /// wirkt im Protokoll, steht aber in der Konfiguration; jede Aufrufstelle, die den
    /// Schalter selbst durchreicht, ist eine Stelle, an der er fehlen kann — und sein Fehlen
    /// fällt nicht auf, es schreibt bloß Klartext in <c>state.db</c>.
    /// </remarks>
    public static SessionLog FromConfig(AppConfig config, StateDatabase database,
                                        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new SessionLog(database, timeProvider, config.Logging.RedactWindowTitles);
    }

    /// <summary>
    /// Baut das Protokoll aus der Konfiguration auf einer eigenen Datenbank am vorgesehenen
    /// Ort im lokalen Profil.
    /// </summary>
    /// <remarks>
    /// Für Vorgänge, die keine Warteschlange öffnen — etwa ein Befehl, der nur das Protokoll
    /// ausgibt oder <see cref="Prune"/> aufruft.
    /// </remarks>
    public static SessionLog FromConfig(AppConfig config, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new SessionLog(StoragePaths.StateDatabaseFile, timeProvider,
                              config.Logging.RedactWindowTitles);
    }

    /// <summary>
    /// Die Aufbewahrungszeit aus <c>logging.retention_days</c>, fertig für
    /// <see cref="Prune"/>.
    /// </summary>
    /// <remarks>
    /// Steht hier, damit die Umrechnung von Tagen in eine Zeitspanne nicht an jeder
    /// Aufrufstelle erneut geschrieben wird.
    /// </remarks>
    public static TimeSpan RetentionOf(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return TimeSpan.FromDays(config.Logging.RetentionDays);
    }

    /// <summary>Ob Fensterbeschriftungen beim Schreiben geschwärzt werden.</summary>
    public bool RedactsWindowTitles => _redactWindowTitles;

    /// <summary>Schreibt einen Eintrag und liefert seine Kennung.</summary>
    public long Append(SessionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Operation);

        DateTimeOffset moment = entry.Timestamp == default ? _time.GetUtcNow() : entry.Timestamp;

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO session_log (ts, remote_maintenance_id, operation, outcome, reason,
                                         trigger, http_status, duration_ms, tanss_support_id,
                                         detail, window_title)
                VALUES ($ts, $id, $operation, $outcome, $reason, $trigger, $status, $duration,
                        $support, $detail, $title)
                RETURNING id;
                """;
            _ = command.Parameters.AddWithValue("$ts", TanssTime.ToUnixSeconds(moment));
            _ = command.Parameters.AddWithValue("$id",
                (object?)entry.RemoteMaintenanceId ?? DBNull.Value);
            _ = command.Parameters.AddWithValue("$operation", entry.Operation);
            _ = command.Parameters.AddWithValue("$outcome", ToText(entry.Outcome));
            _ = command.Parameters.AddWithValue("$reason", Redaction.Scrub(entry.Reason) ?? string.Empty);
            _ = command.Parameters.AddWithValue("$trigger", ToText(entry.Trigger));
            _ = command.Parameters.AddWithValue("$status", (object?)entry.HttpStatus ?? DBNull.Value);
            _ = command.Parameters.AddWithValue("$duration", (object?)entry.DurationMs ?? DBNull.Value);
            _ = command.Parameters.AddWithValue("$support", (object?)entry.TanssSupportId ?? DBNull.Value);
            // Ein fehlendes Detail bleibt NULL. Redaction.Scrub liefert für null eine leere
            // Zeichenkette, und "" hieße „nichts zu sagen“ statt „gar kein Detail erfasst“.
            _ = command.Parameters.AddWithValue("$detail",
                entry.Detail is null ? DBNull.Value : Redaction.Scrub(entry.Detail));
            _ = command.Parameters.AddWithValue("$title",
                (object?)WindowTitleFor(entry.WindowTitle) ?? DBNull.Value);

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        });
    }

    /// <summary>Die jüngsten Einträge, neueste zuerst.</summary>
    public IReadOnlyList<SessionLogEntry> Recent(int limit = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return Query("ORDER BY id DESC LIMIT $limit",
            command => command.Parameters.AddWithValue("$limit", limit));
    }

    /// <summary>Alle Einträge zu einer Sitzung, älteste zuerst — ihre Geschichte von Anfang an.</summary>
    public IReadOnlyList<SessionLogEntry> ForSession(string remoteMaintenanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteMaintenanceId);

        return Query("WHERE remote_maintenance_id = $id ORDER BY id",
            command => command.Parameters.AddWithValue("$id", remoteMaintenanceId));
    }

    /// <summary>
    /// Entfernt Einträge, die älter sind als <paramref name="retention"/>.
    /// </summary>
    /// <returns>Anzahl der entfernten Zeilen.</returns>
    /// <remarks>
    /// Wird aus <c>logging.retention_days</c> gespeist. Die Aufbewahrung ist bewusst
    /// einstellbar und nicht fest: Das Protokoll enthält Rechnernamen und Gegenstellen und
    /// gehört damit demjenigen, dessen Profil es liegt.
    /// </remarks>
    public int Prune(TimeSpan retention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, TimeSpan.Zero);
        long cutoff = TanssTime.ToUnixSeconds(_time.GetUtcNow() - retention);

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM session_log WHERE ts < $cutoff;";
            _ = command.Parameters.AddWithValue("$cutoff", cutoff);
            return command.ExecuteNonQuery();
        });
    }

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

    /// <summary>
    /// Die Fensterbeschriftung, so wie sie in der Datenbank landen darf.
    /// </summary>
    /// <remarks>
    /// <para>Steht <c>logging.redact_window_titles</c>, bleibt statt des Textes nur sein
    /// Abdruck. Zwei Einträge derselben Beschriftung tragen denselben Abdruck, die Frage
    /// „ist das dasselbe Fenster“ bleibt also beantwortbar, ohne dass Kundenname,
    /// Rechnername oder E-Mail-Betreff auf der Platte stehen.</para>
    ///
    /// <para><b>Der Abdruck ist gesalzen, und das ist hier keine Feinheit.</b> Eine
    /// Fensterbeschriftung stammt aus einem überschaubaren Vorrat — „RDP: <c>&lt;host&gt;</c>“
    /// über die eigene Rechnerliste. Ein ungesalzener Hash ließe sich für jeden Kandidaten
    /// nachrechnen und mit der Spalte vergleichen; die Zusage „steht nicht auf der Platte“
    /// wäre dann nur formal wahr. Deshalb <see cref="KeyedFingerprint"/> mit einem je
    /// Installation erzeugten Schlüssel, der DPAPI-versiegelt neben dem Token liegt: Wer
    /// <c>state.db</c> mitnimmt, kann keinen Abdruck nachbilden. Die Kehrseite ist
    /// hingenommen — über Rechnergrenzen hinweg sind Abdrücke nicht mehr vergleichbar.</para>
    ///
    /// <para>Ohne die Einstellung wird wenigstens nach Geheimnissen geschwärzt — eine
    /// Beschriftung trägt gelegentlich ein Kennwort, das jemand ins falsche Feld getippt hat.</para>
    /// </remarks>
    private string? WindowTitleFor(string? title)
    {
        if (title is null)
        {
            return null;
        }

        return _redactWindowTitles
            ? $"(Fensterbeschriftung geschwärzt, Abdruck {_titleFingerprint.Value.Of(title)})"
            : Redaction.Scrub(title);
    }

    private IReadOnlyList<SessionLogEntry> Query(string tail, Action<SqliteCommand> bind) =>
        _database.Execute<IReadOnlyList<SessionLogEntry>>(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, ts, remote_maintenance_id, operation, outcome, reason, trigger, "
                + "http_status, duration_ms, tanss_support_id, detail, window_title "
                + "FROM session_log " + tail + ";";
            bind(command);

            List<SessionLogEntry> entries = [];
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new SessionLogEntry
                {
                    Id = reader.GetInt64(0),
                    // Der Umweg über TanssTime ist Absicht: Es ist der einzige Umrechner
                    // des Projekts. Dass er 0 als „nicht gesetzt“ liest, passt für einen
                    // Zeitstempel nicht, der immer steht - daher der Rückfall auf den Beginn
                    // der Unix-Zeit statt einer zweiten Zeitrechnung.
                    Timestamp = TanssTime.FromUnixSeconds(reader.GetInt64(1))
                        ?? DateTimeOffset.UnixEpoch,
                    RemoteMaintenanceId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Operation = reader.GetString(3),
                    Outcome = OutcomeFrom(reader.GetString(4)),
                    Reason = reader.GetString(5),
                    Trigger = TriggerFrom(reader.GetString(6)),
                    HttpStatus = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    DurationMs = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    TanssSupportId = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    Detail = reader.IsDBNull(10) ? null : reader.GetString(10),
                    WindowTitle = reader.IsDBNull(11) ? null : reader.GetString(11),
                });
            }

            return entries;
        });

    /// <summary>Der Wert, wie er in der Datenbank steht.</summary>
    public static string ToText(SessionOutcome outcome) => outcome switch
    {
        SessionOutcome.Ok => "ok",
        SessionOutcome.DryRun => "dry_run",
        SessionOutcome.Skipped => "skipped",
        SessionOutcome.Deferred => "deferred",
        SessionOutcome.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unbekanntes Ergebnis."),
    };

    /// <summary>Der Wert, wie er in der Datenbank steht.</summary>
    public static string ToText(SessionTrigger trigger) => trigger switch
    {
        SessionTrigger.Watcher => "watcher",
        SessionTrigger.Retry => "retry",
        SessionTrigger.Startup => "startup",
        SessionTrigger.Shutdown => "shutdown",
        SessionTrigger.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unbekannter Auslöser."),
    };

    private static SessionOutcome OutcomeFrom(string text) => text switch
    {
        "ok" => SessionOutcome.Ok,
        "dry_run" => SessionOutcome.DryRun,
        "skipped" => SessionOutcome.Skipped,
        "deferred" => SessionOutcome.Deferred,
        "error" => SessionOutcome.Error,
        _ => throw new StateDatabaseException(
            $"Unbekanntes Ergebnis „{text}“ im Änderungsprotokoll. Die Datenbank stammt "
            + "vermutlich aus einer neueren Programmfassung."),
    };

    private static SessionTrigger TriggerFrom(string text) => text switch
    {
        "watcher" => SessionTrigger.Watcher,
        "retry" => SessionTrigger.Retry,
        "startup" => SessionTrigger.Startup,
        "shutdown" => SessionTrigger.Shutdown,
        "manual" => SessionTrigger.Manual,
        _ => throw new StateDatabaseException(
            $"Unbekannter Auslöser „{text}“ im Änderungsprotokoll. Die Datenbank stammt "
            + "vermutlich aus einer neueren Programmfassung."),
    };
}
