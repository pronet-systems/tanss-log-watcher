using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using TanssLogWatcher.Api;
using TanssLogWatcher.Monitoring;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Secrets;

namespace TanssLogWatcher.Storage.History;

/// <summary>
/// Der Verlauf abgeschlossener Sitzungen in <c>state.db</c>.
/// </summary>
/// <remarks>
/// <para><b>Warum es ihn überhaupt gibt.</b> Weder Warteschlange noch Änderungsprotokoll
/// beantworten „was lief am Dienstag“. Die Warteschlange verliert ihre Zeile beim Verwerfen
/// und nach der Frist; das Änderungsprotokoll kennt weder Ende noch Dauer noch Typ und führt
/// die Gegenstelle nur als Abdruck — und ein Abdruck ist kein Name.</para>
///
/// <para><b>Die Aufzeichnung wird nicht mitgeschrieben, sondern mitgelesen.</b> Ihr Zustand
/// ändert sich nach dem Ende der Sitzung noch — genau dann nämlich, wenn die Frist greift.
/// Eine Kopie in dieser Tabelle wäre ab diesem Augenblick falsch, und zwar ausgerechnet in
/// der Angabe, wegen der die Seite gebaut wird. Gelesen werden dabei <b>alle</b> Abschnitte
/// einer Sitzung und nicht der erste: Gemessen trägt eine Sitzung dieser Datenbank drei
/// Dateien mit drei verschiedenen Löschzeitpunkten.</para>
///
/// <para><b>Die Gegenstelle ist derselbe Text, den <c>logging.redact_window_titles</c> im
/// Protokoll verbirgt.</b> Deshalb entscheidet über die Schwärzung hier
/// <see cref="HistorySection.ShouldRedactDestination"/> und nicht ein eigenständiger Schalter
/// mit eigener Vorgabe. Der Abdruck wird mit demselben Schlüssel gerechnet wie im
/// Änderungsprotokoll (<see cref="KeyedFingerprint"/>, Schlüsseldatei neben der Datenbank) —
/// ein zweiter Schlüssel ergäbe zwei Abdrücke desselben Textes und machte die einzige Frage
/// unbeantwortbar, für die der Abdruck da ist.</para>
///
/// <para><b>Verdrahtet wird über <see cref="FromConfig"/>.</b> Jede Aufrufstelle, die Frist
/// und Schwärzung selbst durchreicht, ist eine Stelle, an der sie beim nächsten Umbau fehlen
/// — und ihr Fehlen fällt nicht auf, es schreibt bloss Klartext, der länger liegt als
/// zugesagt.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SessionHistoryStore : ISessionHistory, IDisposable
{
    private readonly StateDatabase _database;
    private readonly bool _ownsDatabase;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;
    private readonly bool _redactDestination;
    private readonly Lazy<KeyedFingerprint> _fingerprint;
    private int _disposed;

    /// <summary>Öffnet den Verlauf auf einer bereits geöffneten Datenbank.</summary>
    /// <param name="database">Dieselbe Datenbank, auf der auch die Warteschlange liegt.</param>
    /// <param name="retention">
    /// Wie lange eine Zeile frühestens bleibt; aus <c>history.retention_days</c>.
    /// </param>
    /// <param name="redactDestination">
    /// Ob die Gegenstelle gar nicht erst im Klartext geschrieben wird. Voreingestellt
    /// <c>true</c> — wie bei <c>SessionLog</c> bekommt die Zusage, wer den Wert nicht
    /// durchreicht, und nicht ihr Gegenteil. Der vorgesehene Weg ist ohnehin
    /// <see cref="FromConfig"/>; dort entscheidet
    /// <see cref="HistorySection.ShouldRedactDestination"/>.
    /// </param>
    /// <param name="timeProvider">Uhr; fehlt sie, gilt die Systemuhr.</param>
    /// <param name="fingerprint">
    /// Der Abdruckgeber; fehlt er, gilt der Schlüssel neben der Datenbank — derselbe, mit dem
    /// das Änderungsprotokoll arbeitet.
    /// </param>
    public SessionHistoryStore(StateDatabase database, TimeSpan retention,
                               bool redactDestination = true,
                               TimeProvider? timeProvider = null,
                               KeyedFingerprint? fingerprint = null)
        : this(database, retention, redactDestination, timeProvider, fingerprint,
               ownsDatabase: false)
    {
    }

    /// <summary>Öffnet den Verlauf auf einer eigenen Datenbank am angegebenen Pfad.</summary>
    /// <param name="path">Pfad der Zustandsdatenbank.</param>
    /// <param name="retention">Wie lange eine Zeile frühestens bleibt.</param>
    /// <param name="redactDestination">
    /// Ob die Gegenstelle gar nicht erst im Klartext geschrieben wird; voreingestellt
    /// <c>true</c>.
    /// </param>
    /// <param name="timeProvider">Uhr; fehlt sie, gilt die Systemuhr.</param>
    /// <param name="fingerprint">
    /// Der Abdruckgeber; fehlt er, gilt der Schlüssel neben der Datenbank.
    /// </param>
    public SessionHistoryStore(string path, TimeSpan retention,
                               bool redactDestination = true,
                               TimeProvider? timeProvider = null,
                               KeyedFingerprint? fingerprint = null)
        : this(new StateDatabase(path), retention, redactDestination, timeProvider, fingerprint,
               ownsDatabase: true)
    {
    }

    private SessionHistoryStore(StateDatabase database, TimeSpan retention,
                                bool redactDestination, TimeProvider? timeProvider,
                                KeyedFingerprint? fingerprint, bool ownsDatabase)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        _database = database;
        _ownsDatabase = ownsDatabase;
        _time = timeProvider ?? TimeProvider.System;
        _retention = retention;
        _redactDestination = redactDestination;

        // Traege wie im Aenderungsprotokoll: Eine Installation, die nie eine Gegenstelle
        // festhaelt, legt auch keine Schluesseldatei an. Lazy sperrt selbst - der
        // Beobachtungstakt und ein Handbefehl schreiben durchaus gleichzeitig, und zwei
        // zugleich erzeugte Schluessel ergaeben zwei Abdruecke derselben Gegenstelle.
        _fingerprint = fingerprint is { } given
            ? new Lazy<KeyedFingerprint>(given)
            : new Lazy<KeyedFingerprint>(
                () => FingerprintKeyStore.BesideDatabase(database.Path).Load());
    }

    /// <summary>Baut den Verlauf aus der Konfiguration, auf einer offenen Datenbank.</summary>
    /// <param name="config">Die geprüfte Konfiguration.</param>
    /// <param name="database">Dieselbe Datenbank wie für Warteschlange und Protokoll.</param>
    /// <param name="timeProvider">Uhr; fehlt sie, gilt die Systemuhr.</param>
    /// <remarks>
    /// <para><b>Der vorgesehene Weg für den Zusammenbau.</b> Die Schwärzung wird hier über
    /// <see cref="HistorySection.ShouldRedactDestination"/> aufgelöst und nirgends sonst: Ein
    /// <c>?? false</c> an einer Aufrufstelle kehrte die ausdrückliche Entscheidung des
    /// Technikers still um, denn <c>history.redact_destination</c> fehlt in jeder bestehenden
    /// Datei und bedeutet dort „folge dem Protokoll“.</para>
    ///
    /// <para><c>history.enabled</c> wird hier <b>nicht</b> geprüft. Ob überhaupt geschrieben
    /// wird, entscheidet der Dienst; ein Speicher, der sich je nach Einstellung still in eine
    /// Attrappe verwandelte, verschwiege einer späteren Fehlersuche genau das.</para>
    /// </remarks>
    public static SessionHistoryStore FromConfig(AppConfig config, StateDatabase database,
                                                 TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new SessionHistoryStore(database,
                                       TimeSpan.FromDays(config.History.RetentionDays),
                                       config.History.ShouldRedactDestination(config.Logging),
                                       timeProvider);
    }

    /// <summary>
    /// Wie lange der Klartext der Gegenstelle stehen bleibt, fertig für <see cref="Redact"/>.
    /// </summary>
    /// <param name="config">Die geprüfte Konfiguration.</param>
    /// <remarks>
    /// Steht hier, damit die Umrechnung von Tagen in eine Zeitspanne nicht an jeder
    /// Aufrufstelle erneut geschrieben wird.
    /// </remarks>
    public static TimeSpan PlainTextRetentionOf(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return TimeSpan.FromDays(config.History.PlainTextDays);
    }

    /// <summary>Ob die Gegenstelle schon beim Schreiben geschwärzt wird.</summary>
    public bool RedactsDestination => _redactDestination;

    /// <summary>
    /// Hält eine abgeschlossene Sitzung fest.
    /// </summary>
    /// <remarks>
    /// <para><b>Wiederholbar.</b> Derselbe Aufruf für dieselbe Sitzung schreibt keine zweite
    /// Zeile, sondern schreibt Ende, Ausgang und Begründung fort — nach einem Neustart
    /// schliesst der Beobachter eine unterbrochene Sitzung nachweislich ein zweites Mal ab.
    /// Angaben, die etwas sagen, verdrängen dabei Angaben, die nichts sagen: Ein zweiter
    /// Abschluss ohne Profilnamen löscht den Profilnamen nicht.</para>
    ///
    /// <para><b>Eine gebuchte Zeile wird nicht zurückgestuft.</b> Käme nach dem erfolgreichen
    /// Versand noch ein Abschluss herein, stünde sonst „eingereiht“, wo die Fernwartung
    /// längst in TANSS steht — und der Techniker legte sie ein zweites Mal an.</para>
    ///
    /// <para><b>Der Abdruck des Platzhalters gehört nicht in die Zeile.</b> Eine Sitzung, die
    /// nie einen Fenstertitel auflösen konnte, trägt als Ziel
    /// <see cref="SessionConstants.NoDestinationTitle"/>; daraus einen Abdruck zu rechnen,
    /// ergäbe acht Hexziffern, die wie eine Kundenkennung aussehen und „nichts ermittelt“
    /// bedeuten. Gegenstelle und Abdruck bleiben dann <c>null</c>, und die Anzeige sagt
    /// „Gegenstelle nicht ermittelt“.</para>
    /// </remarks>
    /// <param name="entry">
    /// Die Sitzung. <c>DeleteAfter</c> und <c>CreatedAt</c> setzt der Speicher; die
    /// mitgelesene Aufzeichnung (<see cref="SessionHistoryEntry.Recording"/>) wird nicht
    /// geschrieben, denn sie steht in <c>recordings</c>.
    /// </param>
    /// <returns>
    /// Der für diese Zeile festgeschriebene Löschzeitpunkt — <b>frühestens</b> dann wird sie
    /// entfernt. <c>null</c> heisst: Es wurde nichts geschrieben, weil die Zeile bereits
    /// gebucht ist.
    /// </returns>
    public DateTimeOffset? Record(SessionHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.SessionId);

        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset deleteAfter = entry.StartedAt + _retention;
        (string? plain, string? print, DateTimeOffset? redactedAt) = DestinationOf(entry, now);

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO session_history
                    (remote_maintenance_id, started_at, ended_at, profile_key, profile_name,
                     destination, destination_fingerprint, remote_support_type_id, ticket_id,
                     tanss_support_id, disposition, reason, ended_estimated, origin,
                     delete_after, redacted_at, created_at)
                VALUES ($id, $start, $end, $key, $name, $plain, $print, $type, $ticket,
                        $support, $disposition, $reason, $estimated, $origin, $delete,
                        $redacted, $now)
                ON CONFLICT(remote_maintenance_id) DO UPDATE SET
                    ended_at        = excluded.ended_at,
                    disposition     = excluded.disposition,
                    reason          = excluded.reason,
                    ended_estimated = excluded.ended_estimated,
                    -- Wer etwas weiss, setzt sich gegen den durch, der nichts weiss: Der
                    -- erste Abschluss traegt bisweilen nur den Platzhalter, der zweite den
                    -- aufgeloesten Titel. Umgekehrt darf ein Abschluss ohne Gegenstelle die
                    -- bereits bekannte nicht loeschen - und eine laengst geschwaerzte Zeile
                    -- darf er nicht wieder mit Klartext fuellen. Deshalb entscheidet der
                    -- bereits stehende ABDRUCK und nicht der Klartext: Er steht in beiden
                    -- Faellen, in denen etwas bekannt ist.
                    destination = CASE
                        WHEN session_history.destination_fingerprint IS NULL
                             AND excluded.destination_fingerprint IS NOT NULL
                        THEN excluded.destination
                        ELSE session_history.destination END,
                    destination_fingerprint = COALESCE(session_history.destination_fingerprint,
                                                       excluded.destination_fingerprint),
                    redacted_at = CASE
                        WHEN session_history.destination_fingerprint IS NULL
                             AND excluded.destination_fingerprint IS NOT NULL
                        THEN excluded.redacted_at
                        ELSE session_history.redacted_at END,
                    profile_key = CASE WHEN excluded.profile_key <> ''
                                       THEN excluded.profile_key
                                       ELSE session_history.profile_key END,
                    profile_name = CASE WHEN excluded.profile_name <> ''
                                        THEN excluded.profile_name
                                        ELSE session_history.profile_name END,
                    remote_support_type_id = CASE WHEN excluded.remote_support_type_id <> 0
                                                  THEN excluded.remote_support_type_id
                                                  ELSE session_history.remote_support_type_id END,
                    ticket_id = CASE WHEN excluded.ticket_id <> 0
                                     THEN excluded.ticket_id
                                     ELSE session_history.ticket_id END,
                    tanss_support_id = COALESCE(excluded.tanss_support_id,
                                                session_history.tanss_support_id)
                 WHERE session_history.disposition <> 'booked'
                RETURNING delete_after;
                """;
            _ = command.Parameters.AddWithValue("$id", entry.SessionId);
            _ = command.Parameters.AddWithValue("$start", TanssTime.ToUnixSeconds(entry.StartedAt));
            _ = command.Parameters.AddWithValue("$end", TanssTime.ToUnixSeconds(entry.EndedAt));
            _ = command.Parameters.AddWithValue("$key", entry.ProfileKey);
            _ = command.Parameters.AddWithValue("$name", entry.ProfileName);
            _ = command.Parameters.AddWithValue("$plain", (object?)plain ?? DBNull.Value);
            _ = command.Parameters.AddWithValue("$print", (object?)print ?? DBNull.Value);
            _ = command.Parameters.AddWithValue("$type", entry.RemoteSupportTypeId);
            _ = command.Parameters.AddWithValue("$ticket", entry.TicketId);
            _ = command.Parameters.AddWithValue("$support",
                (object?)entry.TanssSupportId ?? DBNull.Value);
            _ = command.Parameters.AddWithValue("$disposition", ToText(entry.Disposition));
            _ = command.Parameters.AddWithValue("$reason", entry.Reason);
            _ = command.Parameters.AddWithValue("$estimated", entry.EndedEstimated ? 1 : 0);
            _ = command.Parameters.AddWithValue("$origin", ToText(entry.Origin));
            _ = command.Parameters.AddWithValue("$delete", TanssTime.ToUnixSeconds(deleteAfter));
            _ = command.Parameters.AddWithValue("$redacted",
                redactedAt is { } moment
                    ? TanssTime.ToUnixSeconds(moment)
                    : (object)DBNull.Value);
            _ = command.Parameters.AddWithValue("$now", TanssTime.ToUnixSeconds(now));

            // RETURNING liefert nichts, wenn die WHERE-Bedingung des Zweiges DO UPDATE nicht
            // zutrifft - genau der Fall "bereits gebucht". Einen Loeschzeitpunkt
            // zurueckzugeben, der nirgends steht, waere eine erfundene Zusage.
            object? stored = command.ExecuteScalar();
            return stored is null or DBNull
                ? null
                : TanssTime.FromUnixSeconds(
                    Convert.ToInt64(stored, CultureInfo.InvariantCulture));
        });
    }

    /// <summary>Hält fest, dass die Sitzung bei TANSS angekommen ist.</summary>
    /// <remarks>
    /// Der einzige Weg zu <see cref="HistoryDisposition.Booked"/>, und er verlangt die
    /// TANSS-Kennung: Eine Zeile, die „gebucht“ sagt und nicht dazusagt, wo, beantwortet
    /// ausgerechnet die Frage nicht, wegen der jemand sie aufschlägt.
    /// </remarks>
    /// <param name="sessionId">Die Sitzung.</param>
    /// <param name="tanssSupportId">Die von TANSS vergebene Kennung.</param>
    /// <param name="reason">Warum, in deutscher Prosa.</param>
    /// <returns><c>false</c>, wenn es die Zeile nicht (mehr) gibt.</returns>
    public bool MarkBooked(string sessionId, int tanssSupportId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE session_history
                   SET tanss_support_id = $support, disposition = 'booked', reason = $reason
                 WHERE remote_maintenance_id = $id;
                """;
            _ = command.Parameters.AddWithValue("$id", sessionId);
            _ = command.Parameters.AddWithValue("$support", tanssSupportId);
            _ = command.Parameters.AddWithValue("$reason", reason);

            return command.ExecuteNonQuery() > 0;
        });
    }

    /// <summary>Hält fest, dass der Techniker die Sitzung verworfen hat.</summary>
    /// <remarks>
    /// Die Zeile bleibt und wird nicht gelöscht — wie der Grabstein einer Aufzeichnung. Wer am
    /// Monatsende eine Lücke sucht, findet hier den Grund; das Verwerfen entfernt sonst die
    /// Warteschlangenzeile und hinterlässt ausser dem Protokolleintrag keine Spur.
    /// </remarks>
    /// <param name="sessionId">Die Sitzung.</param>
    /// <param name="reason">Warum verworfen wurde, in deutscher Prosa.</param>
    /// <returns><c>false</c>, wenn es die Zeile nicht (mehr) gibt oder sie gebucht ist.</returns>
    public bool MarkDiscarded(string sessionId, string reason) =>
        SetDisposition(sessionId, HistoryDisposition.Discarded, reason);

    /// <summary>Hält fest, dass die Sitzung endgültig misslungen ist.</summary>
    /// <param name="sessionId">Die Sitzung.</param>
    /// <param name="reason">Der Grund, in deutscher Prosa.</param>
    /// <returns><c>false</c>, wenn es die Zeile nicht (mehr) gibt oder sie gebucht ist.</returns>
    public bool MarkFailed(string sessionId, string reason) =>
        SetDisposition(sessionId, HistoryDisposition.Failed, reason);

    /// <inheritdoc/>
    /// <remarks>
    /// Drei Abfragen in <b>einem</b> Durchgang und damit unter derselben Sperre auf derselben
    /// Verbindung: die Zeilen, ihre Abschnitte, die Gesamtzahl. Getrennte Durchgänge könnten
    /// eine Gesamtzahl liefern, die nicht zu der gelieferten Liste gehört — und die Seite
    /// sagte dann „5 von 4“.
    /// </remarks>
    public SessionHistoryPage Recent(int limit, bool onlyWithRecording)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return _database.Execute(connection =>
        {
            List<SessionHistoryEntry> items = [];

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT " + HistoryColumns + " FROM session_history "
                    + "WHERE " + FilterSql
                    + " ORDER BY started_at DESC, rowid DESC LIMIT $limit;";
                Bind(command, limit, onlyWithRecording);
                Read(command, items);
            }

            // Eine zweite Abfrage statt eines Verbunds: Die Abschnitte sind mehrere Zeilen je
            // Sitzung, ein Verbund lieferte die Verlaufszeile vervielfacht. Die Auswahl in
            // der Unterabfrage ist dieselbe wie oben - Filter, Ordnung und Grenze stehen
            // beide Male in denselben Konstanten und koennen nicht auseinanderlaufen.
            Dictionary<string, List<SessionRecordingSegment>> segments;
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT remote_maintenance_id, " + SegmentColumns
                    + " FROM recordings WHERE remote_maintenance_id IN ("
                    + "SELECT remote_maintenance_id FROM session_history WHERE " + FilterSql
                    + " ORDER BY started_at DESC, rowid DESC LIMIT $limit) "
                    + "ORDER BY remote_maintenance_id, segment;";
                Bind(command, limit, onlyWithRecording);
                segments = ReadSegments(command);
            }

            int total;
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT COUNT(*) FROM session_history WHERE " + FilterSql + ";";
                Bind(command, limit, onlyWithRecording);
                total = Convert.ToInt32(command.ExecuteScalar() ?? 0,
                                        CultureInfo.InvariantCulture);
            }

            return new SessionHistoryPage
            {
                Items = Attach(items, segments),
                Total = total,
            };
        });
    }

    /// <summary>Eine einzelne Sitzung samt ihren Abschnitten; <c>null</c>, wenn es sie nicht (mehr) gibt.</summary>
    /// <param name="sessionId">Die Sitzung.</param>
    public SessionHistoryEntry? Find(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return _database.Execute(connection =>
        {
            List<SessionHistoryEntry> items = [];

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT " + HistoryColumns + " FROM session_history "
                    + "WHERE remote_maintenance_id = $id LIMIT 1;";
                _ = command.Parameters.AddWithValue("$id", sessionId);
                Read(command, items);
            }

            if (items.Count == 0)
            {
                return null;
            }

            Dictionary<string, List<SessionRecordingSegment>> segments;
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT remote_maintenance_id, " + SegmentColumns
                    + " FROM recordings WHERE remote_maintenance_id = $id ORDER BY segment;";
                _ = command.Parameters.AddWithValue("$id", sessionId);
                segments = ReadSegments(command);
            }

            return Attach(items, segments)[0];
        });
    }

    /// <summary>Wie viele Sitzungen der Verlauf führt.</summary>
    public int Count() => _database.Execute(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session_history;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    });

    /// <summary>
    /// Nimmt den Klartext der Gegenstelle aus allen Zeilen, deren Klartextfrist abgelaufen ist.
    /// </summary>
    /// <remarks>
    /// <b>Die Zeile bleibt, der Name geht.</b> Ein <c>UPDATE</c> und kein <c>DELETE</c>:
    /// Zeiten, Dauer, Typ, Ticket und TANSS-Kennung beantworten weiter, was wann gebucht
    /// wurde; der Abdruck beantwortet weiter, ob zwei Sitzungen zur selben Gegenstelle
    /// gingen. Nur der Name selbst liegt nicht mehr auf der Platte.
    /// <see cref="SessionHistoryEntry.RedactedAt"/> wird dabei gesetzt, denn ohne diesen
    /// Zeitpunkt sähe „war da und ist fort“ genauso aus wie „wurde nie ermittelt“ — und über
    /// die eine Hälfte wäre die Anzeige dann falsch.
    /// </remarks>
    /// <param name="plainTextRetention">
    /// Aus <c>history.plain_text_days</c>. <c>0</c> ist zulässig und heisst: beim nächsten
    /// Durchlauf ist alles fällig.
    /// </param>
    /// <returns>Anzahl der geschwärzten Zeilen.</returns>
    public int Redact(TimeSpan plainTextRetention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(plainTextRetention, TimeSpan.Zero);

        DateTimeOffset now = _time.GetUtcNow();

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE session_history
                   SET destination = NULL, redacted_at = $now
                 WHERE destination IS NOT NULL AND started_at <= $cutoff;
                """;
            _ = command.Parameters.AddWithValue("$now", TanssTime.ToUnixSeconds(now));
            _ = command.Parameters.AddWithValue("$cutoff",
                TanssTime.ToUnixSeconds(now - plainTextRetention));
            return command.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Entfernt Zeilen, deren Aufbewahrungsfrist abgelaufen ist.
    /// </summary>
    /// <remarks>
    /// <para><b>Massgeblich ist der frühere von festgeschriebenem Löschzeitpunkt und dem, was
    /// die aktuelle Einstellung ergäbe</b> — dieselbe Oder-Bedingung wie in
    /// <c>RecordingStore.Due</c>. So wirkt ein <b>Verkürzen</b> der Frist sofort auf
    /// bestehende Zeilen, ein Verlängern aber nicht: Ein gegebenes Versprechen darf jederzeit
    /// enger gefasst, aber nicht nachträglich gedehnt werden. Eine feste Spalte allein
    /// bewirkte genau das Gegenteil.</para>
    ///
    /// <para><b>Eine Zeile mit noch liegender Aufzeichnung bleibt stehen.</b> Sonst
    /// verschwände die Auskunft über ein Video, das es noch gibt — und das Video wäre danach
    /// eine Datei, zu der niemand mehr sagen kann, wessen Sitzung sie zeigt. Genau deshalb ist
    /// <see cref="SessionHistoryEntry.DeleteAfter"/> eine früheste und keine zugesagte Angabe:
    /// <c>recording.retention_days</c> darf über <c>history.retention_days</c> liegen.</para>
    /// </remarks>
    /// <param name="retention">Die aktuell eingestellte Aufbewahrungsdauer.</param>
    /// <returns>Anzahl der entfernten Zeilen.</returns>
    public int Prune(TimeSpan retention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        DateTimeOffset now = _time.GetUtcNow();

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM session_history
                 WHERE (delete_after <= $now OR started_at <= $cutoff)
                   AND NOT EXISTS (
                       SELECT 1 FROM recordings
                        WHERE recordings.remote_maintenance_id
                              = session_history.remote_maintenance_id
                          AND recordings.state IN ('recording','kept'));
                """;
            _ = command.Parameters.AddWithValue("$now", TanssTime.ToUnixSeconds(now));
            _ = command.Parameters.AddWithValue("$cutoff",
                TanssTime.ToUnixSeconds(now - retention));
            return command.ExecuteNonQuery();
        });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Der Riegel ist unteilbar: Beim geordneten Beenden schreibt der Beobachtungstakt
    /// erfahrungsgemäss noch, während abgebaut wird.
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

    /// <summary>Der Wert, wie er in der Datenbank steht.</summary>
    /// <param name="disposition">Der Ausgang.</param>
    public static string ToText(HistoryDisposition disposition) => disposition switch
    {
        HistoryDisposition.Enqueued => "enqueued",
        HistoryDisposition.Booked => "booked",
        HistoryDisposition.Discarded => "discarded",
        HistoryDisposition.NotMapped => "not_mapped",
        HistoryDisposition.DryRun => "dry_run",
        HistoryDisposition.Failed => "failed",
        HistoryDisposition.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition,
                                                   "Unbekannter Ausgang."),
    };

    /// <summary>Der Wert, wie er in der Datenbank steht.</summary>
    /// <param name="origin">Die Herkunft.</param>
    public static string ToText(HistoryOrigin origin) => origin switch
    {
        HistoryOrigin.Live => "live",
        HistoryOrigin.Backfill => "backfill",
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin,
                                                   "Unbekannte Herkunft."),
    };

    /// <summary>Die Spalten einer Verlaufszeile, in der Reihenfolge, die <c>Read</c> erwartet.</summary>
    private const string HistoryColumns = """
        remote_maintenance_id, started_at, ended_at, profile_key, profile_name, destination,
        destination_fingerprint, remote_support_type_id, ticket_id, tanss_support_id,
        disposition, reason, ended_estimated, origin, delete_after, redacted_at, created_at
        """;

    /// <summary>Die Spalten eines Abschnitts, in der Reihenfolge, die <c>ReadSegments</c> erwartet.</summary>
    private const string SegmentColumns = """
        segment, relative_path, state, recorded_seconds, bytes, delete_after, deleted_at,
        delete_reason
        """;

    /// <summary>
    /// Der Filter der Seite: alle Sitzungen oder nur die mit einem Aufzeichnungseintrag.
    /// </summary>
    /// <remarks>
    /// Eine Konstante, weil dieselbe Bedingung dreimal gebraucht wird — für die Zeilen, für
    /// deren Abschnitte und für die Gesamtzahl. Liefen die drei auseinander, zeigte die Seite
    /// eine Gesamtzahl, die nicht zu ihrer Liste gehört. Gefiltert wird nach dem
    /// <b>Eintrag</b> und nicht nach dem Zustand: Eine Sitzung, deren Aufzeichnung die Frist
    /// bereits geholt hat, gehört weiter in diese Liste — sonst verschwände mit der Datei auch
    /// der Nachweis, dass es sie gab.
    /// </remarks>
    private const string FilterSql = """
        ($onlyWithRecording = 0
         OR EXISTS (SELECT 1 FROM recordings
                     WHERE recordings.remote_maintenance_id
                           = session_history.remote_maintenance_id))
        """;

    /// <summary>
    /// Was von der Gegenstelle in die Zeile darf: Klartext, Abdruck und der Zeitpunkt der
    /// Schwärzung.
    /// </summary>
    /// <remarks>
    /// Die einzige Stelle, an der über die Gegenstelle entschieden wird. Drei Fälle:
    /// <list type="bullet">
    /// <item>Ein echtes Ziel und keine Schwärzung: Klartext und Abdruck, kein Zeitpunkt.</item>
    /// <item>Ein echtes Ziel und Schwärzung: nur der Abdruck, <b>mit</b> Zeitpunkt — sonst
    /// läse die Anzeige „nicht ermittelt“, wo in Wahrheit „ermittelt und verborgen“
    /// gilt.</item>
    /// <item>Kein Ziel oder nur der Platzhalter: alles <c>null</c>. Der Abdruck des
    /// Platzhalters ist keine Gegenstelle, sondern acht Hexziffern, die wie eine
    /// Kundenkennung aussehen.</item>
    /// </list>
    /// </remarks>
    private (string? Plain, string? Print, DateTimeOffset? RedactedAt) DestinationOf(
        SessionHistoryEntry entry, DateTimeOffset now)
    {
        if (Meaningful(entry.Destination) is { } text)
        {
            string print = _fingerprint.Value.Of(text);
            return _redactDestination
                ? (null, print, entry.RedactedAt ?? now)
                : (text, print, null);
        }

        // Ohne Klartext bleibt hoechstens ein mitgebrachter Abdruck - so kommt der Nachtrag
        // an eine Gegenstelle, deren Warteschlangenzeile es nicht mehr gibt. Der Abdruck des
        // Platzhalters wird dabei verworfen: Er belegt gerade, dass nie eine aufgeloest war.
        string? given = Meaningful(entry.DestinationFingerprint);
        if (given is null || given == KeyedFingerprint.EmptyMarker || given == PlaceholderPrint())
        {
            return (null, null, null);
        }

        return (null, given, entry.RedactedAt ?? now);
    }

    /// <summary>Der Abdruck des Platzhalters — der Abdruck, der nie eine Gegenstelle meint.</summary>
    private string PlaceholderPrint() =>
        _fingerprint.Value.Of(SessionConstants.NoDestinationTitle);

    /// <summary>
    /// Ein Text, der etwas sagt — oder <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Leer, nur Leerzeichen und der Platzhalter der Sitzungserkennung sind hier dasselbe:
    /// nichts ermittelt. Sie an einer Stelle zusammenzufassen ist der Grund, warum keine
    /// Aufrufstelle den Wortlaut des Platzhalters kennen muss.
    /// </remarks>
    private static string? Meaningful(string? text) =>
        string.IsNullOrWhiteSpace(text) || text == SessionConstants.NoDestinationTitle
            ? null
            : text;

    private bool SetDisposition(string sessionId, HistoryDisposition disposition, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return _database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            // Gebucht ist endgueltig: Wer die Zeile danach auf "verworfen" zurueckdrehte,
            // liesse eine Fernwartung als fehlend erscheinen, die in TANSS steht.
            command.CommandText = """
                UPDATE session_history
                   SET disposition = $disposition, reason = $reason
                 WHERE remote_maintenance_id = $id AND disposition <> 'booked';
                """;
            _ = command.Parameters.AddWithValue("$id", sessionId);
            _ = command.Parameters.AddWithValue("$disposition", ToText(disposition));
            _ = command.Parameters.AddWithValue("$reason", reason);

            return command.ExecuteNonQuery() > 0;
        });
    }

    private static void Bind(SqliteCommand command, int limit, bool onlyWithRecording)
    {
        _ = command.Parameters.AddWithValue("$limit", limit);
        _ = command.Parameters.AddWithValue("$onlyWithRecording", onlyWithRecording ? 1 : 0);
    }

    private static void Read(SqliteCommand command, List<SessionHistoryEntry> into)
    {
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            into.Add(new SessionHistoryEntry
            {
                SessionId = reader.GetString(0),
                // Der Rueckfall auf den Beginn der Unix-Zeit ist derselbe wie im
                // Aenderungsprotokoll: TanssTime liest 0 als "nicht gesetzt", und das passt
                // fuer einen Zeitstempel nicht, der immer steht.
                StartedAt = TanssTime.FromUnixSeconds(reader.GetInt64(1))
                    ?? DateTimeOffset.UnixEpoch,
                EndedAt = TanssTime.FromUnixSeconds(reader.GetInt64(2))
                    ?? DateTimeOffset.UnixEpoch,
                ProfileKey = reader.GetString(3),
                ProfileName = reader.GetString(4),
                Destination = reader.IsDBNull(5) ? null : reader.GetString(5),
                DestinationFingerprint = reader.IsDBNull(6) ? null : reader.GetString(6),
                RemoteSupportTypeId = reader.GetInt32(7),
                TicketId = reader.GetInt32(8),
                TanssSupportId = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                Disposition = DispositionFrom(reader.GetString(10)),
                Reason = reader.GetString(11),
                EndedEstimated = reader.GetInt64(12) != 0,
                Origin = OriginFrom(reader.GetString(13)),
                DeleteAfter = TanssTime.FromUnixSeconds(reader.GetInt64(14))
                    ?? DateTimeOffset.UnixEpoch,
                RedactedAt = reader.IsDBNull(15)
                    ? null
                    : TanssTime.FromUnixSeconds(reader.GetInt64(15)),
                CreatedAt = TanssTime.FromUnixSeconds(reader.GetInt64(16))
                    ?? DateTimeOffset.UnixEpoch,
            });
        }
    }

    /// <summary>
    /// Die Abschnitte, nach Sitzung geordnet.
    /// </summary>
    /// <remarks>
    /// <b>Alle</b> Abschnitte und nicht der erste: Eine Sitzung dieser Datenbank trägt
    /// gemessen drei Dateien mit drei verschiedenen Löschzeitpunkten, die einzeln fällig
    /// werden. Wer nur den ersten läse, spielte vier von fünfzehn Sekunden und zeigte nach
    /// einem Teil-Löschlauf auf eine Datei, die es nicht mehr gibt.
    /// </remarks>
    private static Dictionary<string, List<SessionRecordingSegment>> ReadSegments(
        SqliteCommand command)
    {
        Dictionary<string, List<SessionRecordingSegment>> bySession =
            new(StringComparer.Ordinal);

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            string session = reader.GetString(0);
            if (!bySession.TryGetValue(session, out List<SessionRecordingSegment>? list))
            {
                list = [];
                bySession[session] = list;
            }

            list.Add(new SessionRecordingSegment
            {
                Number = reader.GetInt32(1),
                RelativePath = reader.GetString(2),
                State = SegmentStateFrom(reader.GetString(3)),
                Recorded = TimeSpan.FromSeconds(reader.GetInt64(4)),
                Bytes = reader.GetInt64(5),
                DeleteAfter = TanssTime.FromUnixSeconds(reader.GetInt64(6))
                    ?? DateTimeOffset.UnixEpoch,
                DeletedAt = reader.IsDBNull(7)
                    ? null
                    : TanssTime.FromUnixSeconds(reader.GetInt64(7)),
                DeleteReason = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }

        return bySession;
    }

    private static List<SessionHistoryEntry> Attach(
        List<SessionHistoryEntry> items,
        Dictionary<string, List<SessionRecordingSegment>> segments)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (segments.TryGetValue(items[i].SessionId,
                                     out List<SessionRecordingSegment>? own))
            {
                items[i] = items[i] with
                {
                    Recording = new SessionRecordingSummary { Segments = own },
                };
            }
        }

        return items;
    }

    private static HistoryDisposition DispositionFrom(string text) => text switch
    {
        "enqueued" => HistoryDisposition.Enqueued,
        "booked" => HistoryDisposition.Booked,
        "discarded" => HistoryDisposition.Discarded,
        "not_mapped" => HistoryDisposition.NotMapped,
        "dry_run" => HistoryDisposition.DryRun,
        "failed" => HistoryDisposition.Failed,
        "unknown" => HistoryDisposition.Unknown,
        _ => throw new StateDatabaseException(
            $"Unbekannter Ausgang „{text}“ im Verlauf. Die Datenbank stammt vermutlich aus "
            + "einer neueren Programmversion."),
    };

    private static HistoryOrigin OriginFrom(string text) => text switch
    {
        "live" => HistoryOrigin.Live,
        "backfill" => HistoryOrigin.Backfill,
        _ => throw new StateDatabaseException(
            $"Unbekannte Herkunft „{text}“ im Verlauf. Die Datenbank stammt vermutlich aus "
            + "einer neueren Programmversion."),
    };

    private static RecordingSegmentState SegmentStateFrom(string text) => text switch
    {
        "recording" => RecordingSegmentState.Running,
        "kept" => RecordingSegmentState.Kept,
        "purged" => RecordingSegmentState.Purged,
        "missing" => RecordingSegmentState.Missing,
        _ => throw new StateDatabaseException(
            $"Unbekannter Zustand „{text}“ einer Aufzeichnung. Die Datenbank stammt "
            + "vermutlich aus einer neueren Programmversion."),
    };
}
