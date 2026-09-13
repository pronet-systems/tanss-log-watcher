using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Monitoring;
using TanssLogWatcher.Storage.History;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;
using TanssLogWatcher.Storage.Secrets;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Der einmalige Nachtrag: was er aus Warteschlange und Änderungsprotokoll herausholt und
/// was er ausdrücklich <b>nicht</b> behauptet.
/// </summary>
/// <remarks>
/// Durchweg echtes SQLite. Die Fälle, um die es geht, sind Aussagen über einen bestehenden
/// Datenbestand — eine Datei vom Stand 3, eine zerstörte Nutzlast, ein Protokolleintrag, der
/// nur den Abdruck des Platzhalters trägt. Eine Attrappe hätte davon nichts, und ein Test
/// dagegen prüfte nur die Attrappe.
/// </remarks>
public sealed class SessionHistoryBackfillTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    /// <summary>Der Bericht, wie ihn der Abschlussdialog vorausfüllt — mit Beschriftungen.</summary>
    private const string LabelledComment = """
        Microsoft Remotedesktop: kunde-srv01

        Zeitraum:     13.09.2026 10:00 – 10:20 (20 Minuten)
        Gegenstelle:  kunde-srv01
        Anwendung:    Microsoft Remotedesktop
        Arbeitsplatz: WS-07 / smichel
        """;

    [Fact]
    public void Eine_Warteschlangenzeile_ergibt_eine_vollstaendige_Verlaufszeile()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint key = KeyedFingerprint.CreateRandom();
        string path = temp.File("state.db");

        using (StateDatabase old = new(path))
        {
            using UploadQueue queue = new(old, clock);
            using SessionLog log = new(old, clock, redactWindowTitles: true, key);

            Watch(log, "sitzung-1", SessionConstants.NoDestinationTitle);
            clock.Advance(TimeSpan.FromMinutes(20));
            Watch(log, "sitzung-1", "kunde-srv01");

            _ = queue.Enqueue(Upload("sitzung-1", TimeSpan.FromMinutes(20)) with
            {
                Comment = LabelledComment,
                DeviceName = "kunde-srv01",
                TicketId = 4711,
            });
        }

        Downgrade(path);

        using StateDatabase database = new(path);
        using SessionHistoryStore history = Store(database, clock, key, redact: false);

        BackfillResult result = SessionHistoryBackfill.Run(database, history, Retention, clock);

        Assert.Equal(1, result.FromQueue);
        Assert.Equal(0, result.FromLog);
        Assert.Equal(0, result.Unreadable);
        Assert.NotNull(result.Notice);

        SessionHistoryEntry entry = Assert.IsType<SessionHistoryEntry>(history.Find("sitzung-1"));

        Assert.Equal(Start, entry.StartedAt);
        Assert.Equal(Start + TimeSpan.FromMinutes(20), entry.EndedAt);
        Assert.Equal(TimeSpan.FromMinutes(20), entry.Duration);
        // Aus der Nutzlast gemessen, nicht aus dem Protokoll geschaetzt.
        Assert.False(entry.EndedEstimated);
        Assert.Equal(HistoryOrigin.Backfill, entry.Origin);
        Assert.Equal("Microsoft Remotedesktop", entry.ProfileName);
        Assert.Equal("kunde-srv01", entry.Destination);
        Assert.Equal("kunde-srv01", entry.DestinationLabel);
        Assert.Equal(1003, entry.RemoteSupportTypeId);
        Assert.Equal(4711, entry.TicketId);
        Assert.Equal(HistoryDisposition.Enqueued, entry.Disposition);
    }

    /// <summary>
    /// Drei von zwölf gemessenen Nutzlasten tragen nur die eine Zeile
    /// „Microsoft Remotedesktop: sap“. Die Anwendung darf trotzdem herauskommen — aber nur,
    /// weil der Teil hinter dem Doppelpunkt Zeichen für Zeichen der Gegenstelle entspricht.
    /// </summary>
    [Fact]
    public void Eine_Zeile_ohne_Beschriftungen_ergibt_die_Anwendung_nur_wenn_sie_belegt_ist()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint key = KeyedFingerprint.CreateRandom();
        string path = temp.File("state.db");

        using (StateDatabase old = new(path))
        {
            using UploadQueue queue = new(old, clock);

            _ = queue.Enqueue(Upload("belegt", TimeSpan.FromMinutes(5)) with
            {
                Comment = "Microsoft Remotedesktop: sap",
                DeviceName = "sap",
            });

            // Der Fenstertitel traegt selbst ": ". Ein Trennen an der ersten Stelle ergaebe
            // hier "cmd" - und das ist auch richtig, WEIL der Rest dem Geraetenamen
            // entspricht.
            _ = queue.Enqueue(Upload("doppelpunkt", TimeSpan.FromMinutes(5)) with
            {
                Comment = @"cmd: C:\Temp: Sicherung",
                DeviceName = @"C:\Temp: Sicherung",
            });

            // Hier stimmt der Teil dahinter NICHT mit der Gegenstelle ueberein. Dann wird
            // nichts behauptet.
            _ = queue.Enqueue(Upload("unbelegt", TimeSpan.FromMinutes(5)) with
            {
                Comment = "PowerShell: irgendein Fenstertitel",
                DeviceName = "sap",
            });
        }

        Downgrade(path);

        using StateDatabase database = new(path);
        using SessionHistoryStore history = Store(database, clock, key, redact: false);

        Assert.Equal(3, SessionHistoryBackfill.Run(database, history, Retention, clock).FromQueue);

        Assert.Equal("Microsoft Remotedesktop", Entry(history, "belegt").ProfileName);
        Assert.Equal("sap", Entry(history, "belegt").Destination);
        Assert.Equal("cmd", Entry(history, "doppelpunkt").ProfileName);
        Assert.Equal(@"C:\Temp: Sicherung", Entry(history, "doppelpunkt").Destination);
        Assert.Equal(string.Empty, Entry(history, "unbelegt").ProfileName);
        // Die Gegenstelle stammt aus dem eigenen Feld der Nutzlast und bleibt deshalb da.
        Assert.Equal("sap", Entry(history, "unbelegt").Destination);
    }

    /// <summary>
    /// Der Abdruck des Platzhalters bedeutet „kein Titel ermittelt“ und ist keine
    /// Gegenstelle. Acht Hexziffern sähen aus wie eine Kundenkennung.
    /// </summary>
    [Fact]
    public void Nur_der_Abdruck_des_Platzhalters_ergibt_keine_Gegenstelle()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint key = KeyedFingerprint.CreateRandom();
        string path = temp.File("state.db");

        using (StateDatabase old = new(path))
        {
            using SessionLog log = new(old, clock, redactWindowTitles: true, key);

            Watch(log, "ohne-titel", SessionConstants.NoDestinationTitle);
            clock.Advance(TimeSpan.FromMinutes(12));
            Watch(log, "ohne-titel", SessionConstants.NoDestinationTitle);
        }

        Downgrade(path);

        using StateDatabase database = new(path);
        using SessionHistoryStore history = Store(database, clock, key, redact: true);

        Assert.Equal(1, SessionHistoryBackfill.Run(database, history, Retention, clock).FromLog);

        SessionHistoryEntry entry = Entry(history, "ohne-titel");

        Assert.Null(entry.Destination);
        Assert.Null(entry.DestinationFingerprint);
        Assert.Null(entry.RedactedAt);
        Assert.True(entry.IsDestinationUnknown);
        Assert.False(entry.IsDestinationRedacted);
        Assert.Equal("Gegenstelle nicht ermittelt", entry.DestinationLabel);
    }

    /// <summary>
    /// Ohne Warteschlangenzeile bleibt nur das Protokoll: Ende geschätzt, Anwendung, Typ und
    /// Ticket leer, Ausgang offen — und der Abdruck des aufgelösten Ziels zum Gruppieren.
    /// </summary>
    [Fact]
    public void Ohne_Warteschlangenzeile_traegt_der_Verlauf_ein_geschaetztes_Ende()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint key = KeyedFingerprint.CreateRandom();
        string path = temp.File("state.db");

        using (StateDatabase old = new(path))
        {
            using SessionLog log = new(old, clock, redactWindowTitles: true, key);

            Watch(log, "nur-protokoll", SessionConstants.NoDestinationTitle);
            clock.Advance(TimeSpan.FromMinutes(30));
            Watch(log, "nur-protokoll", "kunde-srv01");
        }

        Downgrade(path);

        using StateDatabase database = new(path);
        using SessionHistoryStore history = Store(database, clock, key, redact: true);

        BackfillResult result = SessionHistoryBackfill.Run(database, history, Retention, clock);

        Assert.Equal(0, result.FromQueue);
        Assert.Equal(1, result.FromLog);

        SessionHistoryEntry entry = Entry(history, "nur-protokoll");

        Assert.Equal(Start, entry.StartedAt);
        Assert.Equal(Start + TimeSpan.FromMinutes(30), entry.EndedAt);
        Assert.True(entry.EndedEstimated);
        Assert.Equal(HistoryOrigin.Backfill, entry.Origin);
        Assert.Equal(HistoryDisposition.Unknown, entry.Disposition);
        Assert.Equal(string.Empty, entry.ProfileName);
        Assert.Equal(0, entry.RemoteSupportTypeId);
        Assert.Equal(0, entry.TicketId);
        // Der Abdruck des aufgeloesten Ziels ueberlebt, der Klartext nicht - und die Anzeige
        // unterscheidet "verborgen" von "nie ermittelt".
        Assert.Null(entry.Destination);
        Assert.Equal(key.Of("kunde-srv01"), entry.DestinationFingerprint);
        Assert.True(entry.IsDestinationRedacted);
        Assert.Equal("Gegenstelle geschwärzt", entry.DestinationLabel);
    }

    /// <summary>
    /// Schwärzt das Protokoll nicht, steht dort das Ziel im Klartext. Es ist dieselbe Angabe
    /// und darf in den Verlauf — die Schwärzung des Verlaufs entscheidet trotzdem allein er.
    /// </summary>
    [Fact]
    public void Ein_ungeschwaerztes_Protokoll_liefert_die_Gegenstelle_im_Klartext()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint key = KeyedFingerprint.CreateRandom();
        string path = temp.File("state.db");

        using (StateDatabase old = new(path))
        {
            using SessionLog log = new(old, clock, redactWindowTitles: false, key);

            Watch(log, "klartext", "kunde-srv01");
            clock.Advance(TimeSpan.FromMinutes(4));
            Watch(log, "klartext", "kunde-srv01");
        }

        Downgrade(path);

        using StateDatabase database = new(path);
        using SessionHistoryStore history = Store(database, clock, key, redact: false);

        _ = SessionHistoryBackfill.Run(database, history, Retention, clock);

        Assert.Equal("kunde-srv01", Entry(history, "klartext").Destination);
    }

    [Fact]
    public void Die_TANSS_Kennung_aus_dem_Protokoll_macht_die_Zeile_gebucht()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint key = KeyedFingerprint.CreateRandom();
        string path = temp.File("state.db");

        using (StateDatabase old = new(path))
        {
            using UploadQueue queue = new(old, clock);
            using SessionLog log = new(old, clock, redactWindowTitles: true, key);

            Watch(log, "gebucht", "kunde-srv01");
            Watch(log, "offen", "kunde-srv02");

            _ = queue.Enqueue(Upload("gebucht", TimeSpan.FromMinutes(9)));
            _ = queue.Enqueue(Upload("offen", TimeSpan.FromMinutes(9)));
            clock.Advance(TimeSpan.FromMinutes(10));
            queue.MarkDone("gebucht");
            queue.MarkDone("offen");

            _ = log.Append(new SessionLogEntry
            {
                Operation = "queue.flush",
                Outcome = SessionOutcome.Ok,
                Reason = "HTTP 201, in TANSS angelegt",
                Trigger = SessionTrigger.Retry,
                RemoteMaintenanceId = "gebucht",
                TanssSupportId = 38584,
            });
        }

        Downgrade(path);

        using StateDatabase database = new(path);
        using SessionHistoryStore history = Store(database, clock, key, redact: false);

        Assert.Equal(2, SessionHistoryBackfill.Run(database, history, Retention, clock).FromQueue);

        SessionHistoryEntry booked = Entry(history, "gebucht");
        Assert.Equal(HistoryDisposition.Booked, booked.Disposition);
        Assert.Equal(38584, booked.TanssSupportId);

        // "done" allein heisst nicht "gebucht": Ohne Kennung bliebe offen, unter welcher
        // Nummer die Fernwartung in TANSS steht.
        SessionHistoryEntry open = Entry(history, "offen");
        Assert.Equal(HistoryDisposition.Unknown, open.Disposition);
        Assert.Null(open.TanssSupportId);
    }

    [Fact]
    public void Ein_verworfener_Eintrag_steht_als_verworfen_im_Verlauf()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint key = KeyedFingerprint.CreateRandom();
        string path = temp.File("state.db");

        using (StateDatabase old = new(path))
        {
            using SessionLog log = new(old, clock, redactWindowTitles: true, key);

            Watch(log, "verworfen", "kunde-srv01");
            clock.Advance(TimeSpan.FromMinutes(7));
            Watch(log, "verworfen", "kunde-srv01");
            clock.Advance(TimeSpan.FromMinutes(1));

            _ = log.Append(new SessionLogEntry
            {
                Operation = "queue.discard",
                Outcome = SessionOutcome.Skipped,
                Reason = "Im Abschlussdialog verworfen",
                Trigger = SessionTrigger.Manual,
                RemoteMaintenanceId = "verworfen",
                WindowTitle = "kunde-srv01",
            });
        }

        Downgrade(path);

        using StateDatabase database = new(path);
        using SessionHistoryStore history = Store(database, clock, key, redact: true);

        Assert.Equal(1, SessionHistoryBackfill.Run(database, history, Retention, clock).FromLog);

        Assert.Equal(HistoryDisposition.Discarded, Entry(history, "verworfen").Disposition);
    }

    /// <summary>
    /// Hausregel 5: Die eine zerstörte Nutzlast kostet ihre Zeile, nicht den ganzen Nachtrag —
    /// und sie wird gezählt, damit die Zahl nicht mehr behauptet, als geschah.
    /// </summary>
    [Fact]
    public void Eine_zerstoerte_Nutzlast_kostet_nur_ihre_eigene_Zeile()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint key = KeyedFingerprint.CreateRandom();
        string path = temp.File("state.db");

        using (StateDatabase old = new(path))
        {
            using UploadQueue queue = new(old, clock);
            _ = queue.Enqueue(Upload("heil", TimeSpan.FromMinutes(3)));
        }

        Raw.Execute(path, """
            INSERT INTO queue (remote_maintenance_id, payload, created_at, state)
            VALUES ('zerstoert', '{kein json', 1789279200, 'done');
            """);
        Downgrade(path);

        using StateDatabase database = new(path);
        using SessionHistoryStore history = Store(database, clock, key, redact: false);

        BackfillResult result = SessionHistoryBackfill.Run(database, history, Retention, clock);

        Assert.Equal(1, result.FromQueue);
        Assert.Equal(1, result.Unreadable);
        Assert.Equal(1, history.Count());
        Assert.Contains("nicht lesen", result.Notice ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Was jenseits der Frist beginnt, wird nicht nachgetragen: Der nächste Aufräumlauf
    /// entfernte es im selben Augenblick wieder.
    /// </summary>
    [Fact]
    public void Ein_Beginn_jenseits_der_Frist_wird_nicht_nachgetragen()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint key = KeyedFingerprint.CreateRandom();
        string path = temp.File("state.db");

        using (StateDatabase old = new(path))
        {
            using UploadQueue queue = new(old, clock);

            // Eingereiht wird heute, begonnen hat die Sitzung vor hundert Tagen: Die Zeile
            // kommt durch den Vorfilter auf created_at und muss am Beginn scheitern.
            _ = queue.Enqueue(Upload("uralt", TimeSpan.FromMinutes(5)) with
            {
                StartTime = TanssTime.ToUnixSeconds(Start - TimeSpan.FromDays(100)),
                EndTime = TanssTime.ToUnixSeconds(Start - TimeSpan.FromDays(100)
                                                  + TimeSpan.FromMinutes(5)),
            });
        }

        Downgrade(path);

        using StateDatabase database = new(path);
        using SessionHistoryStore history = Store(database, clock, key, redact: false);

        BackfillResult result = SessionHistoryBackfill.Run(database, history, Retention, clock);

        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.Unreadable);
        Assert.Null(result.Notice);
        Assert.Equal(0, history.Count());
    }

    [Fact]
    public void Ein_zweiter_Lauf_traegt_nichts_ein_zweites_Mal_nach()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint key = KeyedFingerprint.CreateRandom();
        string path = temp.File("state.db");

        using (StateDatabase old = new(path))
        {
            using UploadQueue queue = new(old, clock);
            using SessionLog log = new(old, clock, redactWindowTitles: true, key);

            Watch(log, "aus-der-schlange", "kunde-srv01");
            Watch(log, "nur-protokoll", "kunde-srv02");
            _ = queue.Enqueue(Upload("aus-der-schlange", TimeSpan.FromMinutes(6)));
        }

        Downgrade(path);

        using StateDatabase database = new(path);
        using SessionHistoryStore history = Store(database, clock, key, redact: false);

        BackfillResult first = SessionHistoryBackfill.Run(database, history, Retention, clock);

        Assert.Equal(1, first.FromQueue);
        Assert.Equal(1, first.FromLog);

        BackfillResult second = SessionHistoryBackfill.Run(database, history, Retention, clock);

        Assert.Equal(0, second.Total);
        Assert.Null(second.Notice);
        Assert.Equal(2, history.Count());
    }

    /// <summary>
    /// Der Auslöser: Eine Datei, die den vorigen Stand trug, ist fällig — eine frisch
    /// angelegte nicht, denn dort ist nichts nachzutragen.
    /// </summary>
    [Fact]
    public void Faellig_ist_nur_eine_Datei_vom_vorigen_Stand()
    {
        using TempDirectory temp = new();
        string path = temp.File("state.db");

        using (StateDatabase frisch = new(path))
        {
            Assert.Equal(0, frisch.PreviousSchemaVersion);
            Assert.False(SessionHistoryBackfill.IsDue(frisch));
        }

        Downgrade(path);

        using (StateDatabase alt = new(path))
        {
            Assert.Equal(3, alt.PreviousSchemaVersion);
            Assert.True(SessionHistoryBackfill.IsDue(alt));
        }

        using StateDatabase wieder = new(path);
        Assert.Equal(StateDatabase.SchemaVersion, wieder.PreviousSchemaVersion);
        Assert.False(SessionHistoryBackfill.IsDue(wieder));
    }

    /// <summary>
    /// Stellt die Datei so her, wie sie vor dem Verlauf aussah: ohne
    /// <c>session_history</c> und mit <c>user_version = 3</c>.
    /// </summary>
    /// <remarks>
    /// Der Umweg über die fertige Datenbank ist Absicht. Ein von Hand geschriebenes Schema
    /// vom Stand 3 wäre eine zweite Wahrheit, die beim nächsten Spaltenzusatz still
    /// auseinanderliefe; so entsteht die Datei aus demselben Skript wie im Betrieb, und nur
    /// das Neue wird wieder entfernt.
    /// </remarks>
    private static void Downgrade(string path) =>
        Raw.Execute(path, """
            DROP TABLE IF EXISTS session_history;
            PRAGMA user_version = 3;
            """);

    private static SessionHistoryStore Store(StateDatabase database, TimeProvider clock,
                                             KeyedFingerprint key, bool redact) =>
        new(database, Retention, redact, clock, key);

    private static SessionHistoryEntry Entry(SessionHistoryStore history, string id) =>
        Assert.IsType<SessionHistoryEntry>(history.Find(id));

    /// <summary>Eine Nutzlast, wie sie der Abschlussdialog einreiht.</summary>
    private static RemoteSupportWrite Upload(string id, TimeSpan duration) =>
        Sample.Upload(id, TanssTime.ToUnixSeconds(Start)) with
        {
            EndTime = TanssTime.ToUnixSeconds(Start + duration),
        };

    /// <summary>Ein Beobachtungseintrag, wie ihn der Takt schreibt — samt Ziel.</summary>
    private static void Watch(SessionLog log, string id, string destination) =>
        _ = log.Append(new SessionLogEntry
        {
            Operation = "watch",
            Outcome = SessionOutcome.Deferred,
            Reason = "Sitzung erkannt",
            Trigger = SessionTrigger.Watcher,
            RemoteMaintenanceId = id,
            WindowTitle = destination,
        });
}
