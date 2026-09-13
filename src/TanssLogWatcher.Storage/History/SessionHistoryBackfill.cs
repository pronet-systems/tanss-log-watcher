using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Monitoring;
using TanssLogWatcher.Storage.Config;

namespace TanssLogWatcher.Storage.History;

/// <summary>Was ein einmaliger Nachtrag zustande gebracht hat.</summary>
/// <param name="FromQueue">
/// Zeilen aus der Warteschlange — mit Beginn, Ende, Gegenstelle, Typ und Ticket.
/// </param>
/// <param name="FromLog">
/// Zeilen nur aus dem Änderungsprotokoll — ohne Anwendung, Typ und Ticket, mit geschätztem
/// Ende.
/// </param>
/// <param name="Unreadable">
/// Warteschlangenzeilen, deren Nutzlast sich nicht lesen liess. Sie werden gezählt und nicht
/// verschwiegen: Eine Zahl, die sie unterschlüge, sagte „fünfzehn nachgetragen“, wo zwei
/// fehlen.
/// </param>
public readonly record struct BackfillResult(int FromQueue, int FromLog, int Unreadable)
{
    /// <summary>Wie viele Zeilen insgesamt entstanden sind.</summary>
    public int Total => FromQueue + FromLog;

    /// <summary>
    /// Ein Satz fürs Änderungsprotokoll; <c>null</c>, wenn es nichts zu berichten gab.
    /// </summary>
    /// <remarks>
    /// Der Satz benennt, was an den nachgetragenen Zeilen fehlt. Ein Nachtrag, der sich wie
    /// eine Messung liest, wäre genau die vorgetäuschte Angabe, die hier nicht vorkommen
    /// darf — und wer später eine Lücke sucht, findet im Protokoll den Grund.
    /// </remarks>
    public string? Notice
    {
        get
        {
            if (Total == 0 && Unreadable == 0)
            {
                return null;
            }

            string hint = FromLog > 0
                ? " Den Zeilen aus dem Änderungsprotokoll fehlen Anwendung, Fernwartungstyp "
                    + "und Ticket; ihr Ende ist der letzte Protokolleintrag und nicht das "
                    + "gemessene Sitzungsende."
                : string.Empty;

            string lost = Unreadable > 0
                ? string.Create(CultureInfo.CurrentCulture,
                    $" {Unreadable} Warteschlangenzeilen liessen sich nicht lesen und blieben "
                    + $"aussen vor.")
                : string.Empty;

            return string.Create(CultureInfo.CurrentCulture,
                $"Verlauf nachgetragen: {Total} Sitzungen, davon {FromQueue} aus der "
                + $"Warteschlange und {FromLog} nur aus dem Änderungsprotokoll.{hint}{lost}");
        }
    }
}

/// <summary>
/// Der einmalige Nachtrag des Verlaufs aus dem, was schon in der Datenbank steht.
/// </summary>
/// <remarks>
/// <para><b>Warum überhaupt.</b> Eine neue Tabelle fängt bei null an. Die Seite wäre am ersten
/// Tag leer, obwohl die Sitzungen der letzten Wochen noch vollständig in <c>queue</c> und
/// <c>session_log</c> liegen.</para>
///
/// <para><b>Zwei Quellen, zwei Güteklassen.</b> Die Warteschlangenzeile trägt die Nutzlast,
/// die an TANSS ging: Beginn, Ende, Gegenstelle, Fernwartungstyp und Ticket, alles gemessen.
/// Ist sie fort — verworfen oder nach der Frist aufgeräumt —, bleibt nur das
/// Änderungsprotokoll: Beginn und Ende sind dann der erste und der letzte Eintrag der
/// Sitzung, und gemessen weicht dieses Ende um bis zu 325 Sekunden vom tatsächlichen ab.
/// Solche Zeilen tragen deshalb <c>ended_estimated = 1</c>, damit die Anzeige es sagen kann;
/// Anwendung, Typ und Ticket bleiben leer statt geraten.</para>
///
/// <para><b>Nichts wird aus einem Fenstertitel zusammengereimt.</b> Die Gegenstelle kommt aus
/// dem eigenen Feld <c>deviceName</c> der Nutzlast und nicht aus einem Zerlegen des
/// Kommentars: Gemessen tragen nur neun von zwölf Nutzlasten überhaupt beschriftete Zeilen,
/// und ein Trennen an <c>": "</c> zerbricht, sobald der Fenstertitel selbst eine solche Stelle
/// enthält — bei den Profilen <c>cmd</c>, <c>powershell</c> und <c>RoyalTS</c> ist das der
/// Regelfall. Die Anwendung wird nur übernommen, wenn sie sich belegen lässt (siehe
/// <c>ApplicationOf</c>); sonst bleibt das Feld leer.</para>
///
/// <para><b>Ein Abdruck ist keine Gegenstelle.</b> Aus dem Protokoll kommt bestenfalls der
/// Abdruck des Ziels, und in der Mehrzahl der Fälle ist es der Abdruck des Platzhalters
/// <see cref="SessionConstants.NoDestinationTitle"/> — er belegt gerade, dass nie ein Titel
/// aufgelöst wurde. Beides trennt <see cref="SessionHistoryStore.Record"/>, das den
/// Platzhalterabdruck verwirft; die Anzeige sagt dann „Gegenstelle nicht ermittelt“ und nicht
/// acht Hexziffern, die wie eine Kundenkennung aussehen.</para>
///
/// <para><b>Wiederholbar und ungefährlich.</b> Nachgetragen wird nur, was noch nicht im
/// Verlauf steht und nicht älter ist als die Aufbewahrungsfrist — Älteres würde der nächste
/// Aufräumlauf ohnehin sofort wieder entfernen. Ein zweiter Lauf kostet drei Abfragen und
/// ändert nichts. Der Auslöser gehört trotzdem an den Aufrufer: <see cref="IsDue"/> beantwortet
/// ihn aus <see cref="StateDatabase.PreviousSchemaVersion"/>.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class SessionHistoryBackfill
{
    /// <summary>Wie die Warteschlange ihre Nutzlast schreibt — dieselben Einstellungen.</summary>
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.General);

    /// <summary>
    /// Der Beginn der Schwärzungsnotiz, die das Änderungsprotokoll statt der Beschriftung
    /// schreibt.
    /// </summary>
    /// <remarks>
    /// Erkannt wird bewusst nur der Anfang und nicht der ganze Wortlaut: Ändert sich der Satz,
    /// soll die Notiz weiterhin als Notiz erkannt werden. Sie als Gegenstelle in die Zeile zu
    /// schreiben wäre der schlimmere Fehler — dann stünde dort ein Satz über eine Schwärzung,
    /// wo ein Rechnername hingehört.
    /// </remarks>
    private const string RedactionNotice = "(Fensterbeschriftung";

    /// <summary>Das Wort, hinter dem in dieser Notiz der Abdruck steht.</summary>
    private const string PrintMarker = "Abdruck ";

    /// <summary>Die Beschriftung, hinter der in der Nutzlast die Anwendung steht.</summary>
    private const string ApplicationLabel = "Anwendung:";

    /// <summary>Der Protokollvorgang, den der Abschlussdialog beim Verwerfen schreibt.</summary>
    private const string DiscardOperation = "queue.discard";

    /// <summary>
    /// Ist der Nachtrag fällig — trug die Datei beim Öffnen einen älteren Stand?
    /// </summary>
    /// <param name="database">Die geöffnete Zustandsdatenbank.</param>
    /// <remarks>
    /// <c>0</c> heisst „frisch angelegt“ und damit: nichts nachzutragen. Der aktuelle Stand
    /// heisst dasselbe. Dazwischen lag <c>session_history</c> noch nicht vor, während
    /// Warteschlange und Protokoll bereits schrieben — genau dann ist etwas nachzuholen.
    /// </remarks>
    public static bool IsDue(StateDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.PreviousSchemaVersion is > 0 and < StateDatabase.SchemaVersion;
    }

    /// <summary>Trägt den Verlauf nach, mit der Frist aus der Konfiguration.</summary>
    /// <param name="config">Die geprüfte Konfiguration.</param>
    /// <param name="database">Die geöffnete Zustandsdatenbank.</param>
    /// <param name="history">Der Verlauf, in den geschrieben wird.</param>
    /// <param name="timeProvider">Uhr; fehlt sie, gilt die Systemuhr.</param>
    /// <returns>Was zustande kam.</returns>
    /// <remarks>
    /// Der vorgesehene Weg. Die Frist steht damit an einer einzigen Stelle: Reichte der
    /// Aufrufer sie von Hand durch und griffe dabei daneben, trüge der Nachtrag Sitzungen ein,
    /// die der nächste Aufräumlauf sofort wieder entfernt — oder er liesse welche aus, die
    /// bleiben dürften.
    /// </remarks>
    public static BackfillResult FromConfig(AppConfig config, StateDatabase database,
                                            SessionHistoryStore history,
                                            TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Run(database, history, TimeSpan.FromDays(config.History.RetentionDays),
                   timeProvider);
    }

    /// <summary>
    /// Trägt den Verlauf aus Warteschlange und Änderungsprotokoll nach.
    /// </summary>
    /// <param name="database">Die geöffnete Zustandsdatenbank.</param>
    /// <param name="history">
    /// Der Verlauf, in den geschrieben wird — auf derselben Datenbank. Er entscheidet über
    /// Schwärzung und Abdruck; der Nachtrag reicht nur weiter, was er vorfindet.
    /// </param>
    /// <param name="retention">
    /// Die eingestellte Aufbewahrung. Was älter ist, wird nicht nachgetragen: Es wäre im
    /// selben Augenblick fällig.
    /// </param>
    /// <param name="timeProvider">Uhr; fehlt sie, gilt die Systemuhr.</param>
    /// <returns>Was zustande kam.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="retention"/> ist nicht positiv.</exception>
    public static BackfillResult Run(StateDatabase database, SessionHistoryStore history,
                                     TimeSpan retention, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        TimeProvider clock = timeProvider ?? TimeProvider.System;
        DateTimeOffset cutoff = clock.GetUtcNow() - retention;

        HashSet<string> known = ExistingIds(database);
        Dictionary<string, LogFacts> log = LogFactsById(database);

        int fromQueue = 0;
        int unreadable = 0;

        foreach ((string id, string payload, string state) in
                 QueueRows(database, TanssTime.ToUnixSeconds(cutoff)))
        {
            if (known.Contains(id))
            {
                continue;
            }

            RemoteSupportWrite? write = Parse(payload);
            if (write is null || TanssTime.FromUnixSeconds(write.StartTime) is not { } begin)
            {
                // Hausregel 5: Eine zerstoerte Nutzlast kostet diese eine Zeile, nicht den
                // ganzen Nachtrag. Ohne Beginn ist die Zeile ebenso unbrauchbar wie ohne
                // Nutzlast - ein Beginn aus dem Nichts waere eine erfundene Angabe.
                unreadable++;
                continue;
            }

            if (begin <= cutoff)
            {
                continue;
            }

            _ = log.TryGetValue(id, out LogFacts? facts);
            (DateTimeOffset ended, bool estimated) = EndOf(write, begin, facts);
            string reason = QueueReason(state, facts?.SupportId is not null, estimated);

            _ = history.Record(new SessionHistoryEntry
            {
                SessionId = id,
                StartedAt = begin,
                EndedAt = ended,
                ProfileName = ApplicationOf(write),
                // Das eigene Feld der Nutzlast, nicht die erste Kommentarzeile. Was davon in
                // die Zeile darf - Klartext, Abdruck oder nichts -, entscheidet der Speicher.
                Destination = Meaningful(write.DeviceName) ?? facts?.Plain,
                DestinationFingerprint = facts?.Print,
                RemoteSupportTypeId = write.TypeId,
                TicketId = write.TicketId,
                Disposition = QueueDisposition(state),
                Reason = reason,
                EndedEstimated = estimated,
                Origin = HistoryOrigin.Backfill,
                DeleteAfter = default,
            });

            Book(history, id, facts, reason);
            _ = known.Add(id);
            fromQueue++;
        }

        int fromLog = 0;

        foreach ((string id, LogFacts facts) in log.OrderBy(pair => pair.Value.First))
        {
            if (known.Contains(id)
                || facts.First is not { } begin
                || facts.Last is not { } end
                || begin <= cutoff)
            {
                continue;
            }

            string reason = LogReason(facts);

            _ = history.Record(new SessionHistoryEntry
            {
                SessionId = id,
                StartedAt = begin,
                EndedAt = end,
                // Kein Klartext aus der Nutzlast - die Zeile ist fort. Was das Protokoll
                // hergibt, ist entweder der geschwaerzte Abdruck oder die Beschriftung
                // selbst; beides reicht der Speicher durch seine eigene Pruefung.
                Destination = facts.Plain,
                DestinationFingerprint = facts.Print,
                Disposition = facts.Discarded
                    ? HistoryDisposition.Discarded
                    : HistoryDisposition.Unknown,
                Reason = reason,
                // Immer geschaetzt: Das Ende ist der letzte Protokolleintrag der Sitzung.
                EndedEstimated = true,
                Origin = HistoryOrigin.Backfill,
                DeleteAfter = default,
            });

            Book(history, id, facts, reason);
            _ = known.Add(id);
            fromLog++;
        }

        return new BackfillResult(fromQueue, fromLog, unreadable);
    }

    /// <summary>
    /// Trägt die TANSS-Kennung nach, wenn das Protokoll eine kennt.
    /// </summary>
    /// <remarks>
    /// Über <see cref="SessionHistoryStore.MarkBooked"/> und nicht über den Ausgang in
    /// <see cref="SessionHistoryStore.Record"/>: „gebucht“ soll nur dastehen, wo auch die
    /// Kennung steht. Eine Zeile, die „gebucht“ sagt und nicht dazusagt, wo, beantwortet
    /// ausgerechnet die Frage nicht, wegen der jemand sie aufschlägt — und ohne Kennung bleibt
    /// der Ausgang deshalb offen.
    /// </remarks>
    private static void Book(SessionHistoryStore history, string sessionId, LogFacts? facts,
                             string reason)
    {
        if (facts?.SupportId is { } number)
        {
            _ = history.MarkBooked(sessionId, number, reason);
        }
    }

    /// <summary>Das Ende der Sitzung und ob es geschätzt ist.</summary>
    /// <remarks>
    /// Die Nutzlast trägt das gemessene Ende; sie ist der Text, der bei TANSS steht. Fehlt es
    /// dort (<c>endTime = 0</c> heisst „läuft noch“) oder liegt es vor dem Beginn, tritt der
    /// letzte Protokolleintrag ein — und dann ist das Ende geschätzt und wird auch so
    /// ausgewiesen. Bleibt auch der aus, endet die Sitzung mit ihrem Beginn: eine Dauer von
    /// null ist erkennbar nichts, ein Ende von 1970 wäre eine erfundene Zahl.
    /// </remarks>
    private static (DateTimeOffset Ended, bool Estimated) EndOf(RemoteSupportWrite write,
                                                                DateTimeOffset begin,
                                                                LogFacts? facts)
    {
        if (TanssTime.FromUnixSeconds(write.EndTime) is { } measured && measured >= begin)
        {
            return (measured, false);
        }

        return facts?.Last is { } last && last >= begin ? (last, true) : (begin, true);
    }

    /// <summary>Der Ausgang, wie ihn die Warteschlangenzeile belegt.</summary>
    /// <remarks>
    /// <c>done</c> ergibt <b>nicht</b> „gebucht“: Der Zustand sagt, dass der Versand
    /// durchlief, nicht unter welcher Kennung die Fernwartung in TANSS steht. Steht sie im
    /// Protokoll, trägt <c>Book</c> sie samt Ausgang nach; steht sie nirgends, bleibt der
    /// Ausgang offen statt behauptet.
    /// </remarks>
    private static HistoryDisposition QueueDisposition(string state) => state switch
    {
        "failed" => HistoryDisposition.Failed,
        "done" => HistoryDisposition.Unknown,
        _ => HistoryDisposition.Enqueued,
    };

    /// <summary>Woher die Zeile stammt und was an ihr offen ist — in deutscher Prosa.</summary>
    private static string QueueReason(string state, bool booked, bool estimated)
    {
        string outcome = booked
            ? " Die TANSS-Kennung stammt aus dem Änderungsprotokoll."
            : state switch
            {
                "done" => " Die Warteschlangenzeile ist abgeschlossen, eine TANSS-Kennung "
                    + "steht aber in keinem Protokolleintrag — unter welcher Nummer die "
                    + "Fernwartung dort geführt wird, ist nicht ermittelt.",
                "failed" => " Der Versand ist nach der Warteschlangenzeile endgültig "
                    + "misslungen.",
                _ => " Die Zeile wartete noch auf den nächsten Sendelauf.",
            };

        string end = estimated
            ? " Das Ende stammt aus dem letzten Protokolleintrag, weil die Nutzlast keines "
                + "trägt; es ist deshalb geschätzt."
            : string.Empty;

        return "Nachgetragen aus der Warteschlange: Beginn, Ende, Gegenstelle, "
            + "Fernwartungstyp und Ticket stammen aus der Nutzlast, die an TANSS ging."
            + outcome + end;
    }

    /// <summary>Woher die Zeile stammt und was ihr fehlt — in deutscher Prosa.</summary>
    private static string LogReason(LogFacts facts)
    {
        string outcome = facts.SupportId is not null
            ? " Die TANSS-Kennung stammt aus dem Änderungsprotokoll."
            : facts.Discarded
                ? " Der Protokolleintrag „queue.discard“ belegt, dass der Abschlussdialog die "
                    + "Sitzung verworfen hat."
                : " Ob sie gebucht, verworfen oder liegengeblieben ist, geht aus dem Protokoll "
                    + "nicht hervor.";

        return "Nachgetragen aus dem Änderungsprotokoll. Die Warteschlangenzeile ist fort — "
            + "verworfen oder aufgeräumt —, deshalb fehlen Anwendung, Fernwartungstyp und "
            + "Ticket; Beginn und Ende sind der erste und der letzte Protokolleintrag der "
            + "Sitzung und nicht die gemessenen Zeiten." + outcome;
    }

    /// <summary>
    /// Die Anwendung aus dem Kommentar der Nutzlast, wenn sie sich belegen lässt.
    /// </summary>
    /// <remarks>
    /// <para>Zwei Wege, beide belegt, sonst keiner. Erstens die beschriftete Zeile
    /// <c>„Anwendung: …“</c>: Dahinter steht der Rest der Zeile, da ist nichts zu trennen und
    /// nichts zu raten. Zweitens — für die gemessen drei von zwölf Nutzlasten ohne
    /// Beschriftungen — die erste Zeile <c>„&lt;Anwendung&gt;: &lt;Gegenstelle&gt;“</c>, und
    /// zwar <b>nur</b>, wenn sie auf <c>": "</c> samt dem Gerätenamen endet. Dann ist belegt,
    /// dass die Zeile wirklich so gebaut ist.</para>
    ///
    /// <para>Ein Trennen an der ersten Stelle <c>": "</c> ohne diesen Abgleich zerbräche,
    /// sobald der Fenstertitel selbst eine solche Stelle enthält — bei <c>cmd</c>,
    /// <c>powershell</c> und <c>RoyalTS</c> der Regelfall. Lässt sich nichts belegen, bleibt
    /// das Feld leer und die Anzeige zeigt den Fernwartungstyp; eine geratene Anwendung wäre
    /// schlechter als keine.</para>
    /// </remarks>
    private static string ApplicationOf(RemoteSupportWrite write)
    {
        foreach (string line in write.Comment.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(ApplicationLabel, StringComparison.Ordinal))
            {
                return trimmed[ApplicationLabel.Length..].Trim();
            }
        }

        string first = write.Comment.Split('\n')[0].TrimEnd('\r');
        string tail = ": " + write.DeviceName;

        return Meaningful(write.DeviceName) is not null
               && first.Length > tail.Length
               && first.EndsWith(tail, StringComparison.Ordinal)
            ? first[..^tail.Length]
            : string.Empty;
    }

    /// <summary>Ein Text, der etwas sagt — oder <c>null</c>.</summary>
    /// <remarks>
    /// Leer, nur Leerzeichen und der Platzhalter der Sitzungserkennung sind hier dasselbe:
    /// nichts ermittelt. Der Nachtrag braucht die Unterscheidung, weil er zwischen zwei
    /// Bewerbern um dieselbe Spalte wählt — der Speicher prüft anschliessend noch einmal.
    /// </remarks>
    private static string? Meaningful(string? text) =>
        string.IsNullOrWhiteSpace(text) || text == SessionConstants.NoDestinationTitle
            ? null
            : text;

    private static RemoteSupportWrite? Parse(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<RemoteSupportWrite>(payload, PayloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HashSet<string> ExistingIds(StateDatabase database) =>
        database.Execute(connection =>
        {
            HashSet<string> ids = new(StringComparer.Ordinal);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT remote_maintenance_id FROM session_history;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                _ = ids.Add(reader.GetString(0));
            }

            return ids;
        });

    /// <summary>Die Warteschlangenzeilen, die jung genug sind.</summary>
    /// <remarks>
    /// <c>created_at</c> ist der Augenblick des Einreihens und liegt nie vor dem Beginn der
    /// Sitzung. Die Bedingung schliesst deshalb nur aus, was ohnehin zu alt ist; über den
    /// Beginn selbst entscheidet der Aufrufer, denn der steht in der Nutzlast und nicht in
    /// einer Spalte.
    /// </remarks>
    private static List<(string Id, string Payload, string State)> QueueRows(
        StateDatabase database, long cutoff) =>
        database.Execute(connection =>
        {
            List<(string, string, string)> rows = [];
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT remote_maintenance_id, payload, state
                  FROM queue
                 WHERE created_at > $cutoff
                 ORDER BY created_at;
                """;
            _ = command.Parameters.AddWithValue("$cutoff", cutoff);

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                {
                    continue;
                }

                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            return rows;
        });

    /// <summary>
    /// Was das Änderungsprotokoll je Sitzung hergibt.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Zeitraum stammt nur aus den Beobachtungseinträgen</b>
    /// (<c>watch</c>, <c>watch.recover</c>). Ein <c>queue.flush</c> kann Stunden später
    /// laufen, wenn das Netz weg war; als Sitzungsende genommen ergäbe er eine Dauer, die es
    /// nie gab.</para>
    ///
    /// <para><b>Beschriftung, Kennung und das Verwerfen stammen aus allen Einträgen der
    /// Sitzung.</b> Der Abschlussdialog schreibt sein <c>queue.discard</c> mitsamt Ziel, und
    /// die TANSS-Kennung steht am Versandeintrag. Genommen wird die <b>letzte</b>
    /// Beschriftung: Der erste Eintrag einer Sitzung trägt gemessen noch den Platzhalter, der
    /// aufgelöste Titel steht erst später.</para>
    ///
    /// <para>Ohne Zeitfilter, obwohl die Frist gleich danach greift: Eine Bedingung auf
    /// <c>ts</c> schnitte den ersten Eintrag ab und verschöbe damit den Beginn der Sitzung.
    /// Der Aufrufer wirft stattdessen ganze Sitzungen weg. Bezahlbar ist das, weil der Lauf
    /// einmalig ist und <c>logging.retention_days</c> die Tabelle kurz hält.</para>
    /// </remarks>
    private static Dictionary<string, LogFacts> LogFactsById(StateDatabase database) =>
        database.Execute(connection =>
        {
            Dictionary<string, LogFacts> facts = new(StringComparer.Ordinal);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.remote_maintenance_id,
                       MIN(CASE WHEN s.operation LIKE 'watch%' THEN s.ts END),
                       MAX(CASE WHEN s.operation LIKE 'watch%' THEN s.ts END),
                       MAX(s.tanss_support_id),
                       (SELECT t.window_title
                          FROM session_log t
                         WHERE t.remote_maintenance_id = s.remote_maintenance_id
                           AND t.window_title IS NOT NULL
                         ORDER BY t.ts DESC, t.id DESC
                         LIMIT 1),
                       MAX(CASE WHEN s.operation = $discard THEN 1 ELSE 0 END)
                  FROM session_log s
                 WHERE s.remote_maintenance_id IS NOT NULL
                 GROUP BY s.remote_maintenance_id;
                """;
            _ = command.Parameters.AddWithValue("$discard", DiscardOperation);

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                (string? plain, string? print) =
                    DestinationFrom(reader.IsDBNull(4) ? null : reader.GetString(4));

                facts[reader.GetString(0)] = new LogFacts
                {
                    First = reader.IsDBNull(1)
                        ? null
                        : TanssTime.FromUnixSeconds(reader.GetInt64(1)),
                    Last = reader.IsDBNull(2)
                        ? null
                        : TanssTime.FromUnixSeconds(reader.GetInt64(2)),
                    SupportId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    Plain = plain,
                    Print = print,
                    Discarded = !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
                };
            }

            return facts;
        });

    /// <summary>
    /// Was in <c>session_log.window_title</c> steht: eine Beschriftung, ein Abdruck — oder
    /// nichts, worauf sich etwas stützen liesse.
    /// </summary>
    /// <remarks>
    /// <para>Das Protokoll legt dort bei eingeschalteter Schwärzung nicht den Abdruck ab,
    /// sondern einen Satz darüber. Aus ihm wird der blosse Abdruck gelöst, und auch der nur,
    /// wenn er aussieht wie einer: acht Hexziffern. <c>(leer)</c> und alles andere ergeben
    /// nichts — ein Satz über eine Schwärzung ist keine Gegenstelle.</para>
    ///
    /// <para>Ohne Schwärzung steht dort das Ziel der Sitzung im Klartext, so wie es auch der
    /// Abschlussdialog verwendet hat. Es wird als Klartext weitergereicht; ob es in die Zeile
    /// darf, entscheidet die Schwärzungseinstellung des Verlaufs und nicht diese Stelle.</para>
    /// </remarks>
    private static (string? Plain, string? Print) DestinationFrom(string? windowTitle)
    {
        if (Meaningful(windowTitle) is not { } text)
        {
            return (null, null);
        }

        if (!text.StartsWith(RedactionNotice, StringComparison.Ordinal))
        {
            return (text, null);
        }

        int start = text.IndexOf(PrintMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return (null, null);
        }

        string rest = text[(start + PrintMarker.Length)..].TrimEnd(')');

        return IsPrint(rest) ? (null, rest) : (null, null);
    }

    /// <summary>Sieht der Text aus wie ein Abdruck — acht Hexziffern?</summary>
    private static bool IsPrint(string text)
    {
        if (text.Length != 8)
        {
            return false;
        }

        foreach (char sign in text)
        {
            if (!char.IsAsciiHexDigit(sign))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Was das Änderungsprotokoll über eine Sitzung weiss.</summary>
    private sealed record LogFacts
    {
        /// <summary>Der erste Beobachtungseintrag; <c>null</c>, wenn es keinen gibt.</summary>
        public DateTimeOffset? First { get; init; }

        /// <summary>Der letzte Beobachtungseintrag; <c>null</c>, wenn es keinen gibt.</summary>
        public DateTimeOffset? Last { get; init; }

        /// <summary>Die TANSS-Kennung, falls ein Eintrag sie trägt.</summary>
        public int? SupportId { get; init; }

        /// <summary>Das Ziel im Klartext, falls das Protokoll nicht schwärzt.</summary>
        public string? Plain { get; init; }

        /// <summary>Der Abdruck des Ziels, falls das Protokoll schwärzt.</summary>
        public string? Print { get; init; }

        /// <summary>Ob ein Eintrag das Verwerfen im Abschlussdialog belegt.</summary>
        public bool Discarded { get; init; }
    }
}
