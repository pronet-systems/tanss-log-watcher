using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using TanssLogWatcher.Storage.Queue;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

public sealed class UploadQueueTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Zweimaliges_Aufnehmen_derselben_Kennung_ergibt_einen_Eintrag()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using UploadQueue queue = new(temp.File("state.db"), clock);

        Assert.True(queue.Enqueue(Sample.Upload("sitzung-1")));
        // Zweiter Anlauf, etwa nach einem Neustart mitten im Abschluss der Sitzung.
        // TANSS dedupliziert nicht - diese Zeile ist der einzige Schutz davor.
        Assert.False(queue.Enqueue(Sample.Upload("sitzung-1")));

        Assert.Equal(1, queue.Count(QueueState.Pending));
    }

    [Fact]
    public void Eine_bereits_hochgeladene_Kennung_kommt_nicht_zurueck_in_die_Schlange()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using UploadQueue queue = new(temp.File("state.db"), clock);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));
        queue.MarkDone("sitzung-1");

        Assert.False(queue.Enqueue(Sample.Upload("sitzung-1")));
        Assert.Equal(0, queue.Count(QueueState.Pending));
        Assert.Equal(1, queue.Count(QueueState.Done));
    }

    [Fact]
    public void Die_Nutzlast_kommt_unveraendert_zurueck()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using UploadQueue queue = new(temp.File("state.db"), clock);

        _ = queue.Enqueue(Sample.Upload("sitzung-1", start: 1_757_600_000));

        QueuedUpload leased = Assert.Single(queue.Lease(10));

        Assert.Equal("sitzung-1", leased.RemoteMaintenanceId);
        // Sekunden, keine Millisekunden. Ein Faktor 1000 an dieser Stelle ergaebe ein
        // Datum im Jahr 51667 statt eines Fehlers.
        Assert.Equal(1_757_600_000, leased.Payload.StartTime);
        Assert.Equal(1_757_600_600, leased.Payload.EndTime);
        Assert.Equal(1003, leased.Payload.TypeId);
        Assert.Equal("kunde-srv01", leased.Payload.DeviceName);
        Assert.Equal(QueueState.Sending, leased.State);
    }

    [Fact]
    public void Ein_zugeteilter_Eintrag_wird_kein_zweites_Mal_ausgegeben()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using UploadQueue queue = new(temp.File("state.db"), clock);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));

        Assert.Single(queue.Lease(10));
        Assert.Empty(queue.Lease(10));
    }

    [Fact]
    public void Zwei_gleichzeitige_Aufrufe_teilen_keinen_Eintrag_doppelt_zu()
    {
        using TempDirectory temp = new();
        string path = temp.File("state.db");

        using (UploadQueue filler = new(path))
        {
            for (int index = 0; index < 200; index++)
            {
                _ = filler.Enqueue(Sample.Upload($"sitzung-{index}"));
            }
        }

        // Zwei eigene Verbindungen auf dieselbe Datei - so wuerden auch zwei Prozesse
        // arbeiten. Die Instanzsperre hilft hier nicht; es traegt allein BEGIN IMMEDIATE.
        using UploadQueue first = new(path);
        using UploadQueue second = new(path);

        ConcurrentBag<string> leased = [];

        Parallel.For(0, 40, index =>
        {
            UploadQueue queue = index % 2 == 0 ? first : second;
            foreach (QueuedUpload item in queue.Lease(5))
            {
                leased.Add(item.RemoteMaintenanceId);
            }
        });

        string[] all = [.. leased];
        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(200, all.Length);
    }

    [Fact]
    public void Haengengebliebenes_Senden_faellt_nach_Ablauf_zurueck()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using UploadQueue queue = new(temp.File("state.db"), clock);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));
        Assert.Single(queue.Lease(10));

        // Noch innerhalb der Frist: Der Eintrag koennte einfach langsam sein.
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(0, queue.RequeueStuck(TimeSpan.FromMinutes(10)));
        Assert.Equal(QueueState.Sending, queue.Find("sitzung-1")!.State);

        // Danach: Der Prozess ist waehrend des Sendens verschwunden.
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(1, queue.RequeueStuck(TimeSpan.FromMinutes(10)));

        QueuedUpload back = queue.Find("sitzung-1")!;
        Assert.Equal(QueueState.Pending, back.State);
        Assert.Equal(1, back.Attempts);
        Assert.Null(back.LeasedAt);
        Assert.Contains("abgebrochen", back.LastError, StringComparison.Ordinal);
        // Mit Rueckstau, sonst liefe ein beim Senden abstuerzendes Werkzeug im Kreis.
        Assert.True(back.NextAttemptAt > clock.GetUtcNow());
    }

    [Fact]
    public void Vor_der_Faelligkeit_wird_nichts_ausgegeben()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using UploadQueue queue = new(temp.File("state.db"), clock);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));
        Assert.Single(queue.Lease(10));

        queue.MarkFailed("sitzung-1", "TANSS nicht erreichbar",
            clock.GetUtcNow() + TimeSpan.FromMinutes(15));

        Assert.Empty(queue.Lease(10));
        clock.Advance(TimeSpan.FromMinutes(16));

        QueuedUpload again = Assert.Single(queue.Lease(10));
        Assert.Equal(1, again.Attempts);
        Assert.Equal("TANSS nicht erreichbar", again.LastError);
    }

    [Fact]
    public void Ein_Fehler_den_kein_Warten_heilt_wird_aufgegeben()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using UploadQueue queue = new(temp.File("state.db"), clock);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));
        Assert.Single(queue.Lease(10));

        // 403 TYPE_DOESNT_EXIST: Der Fernwartungstyp ist in TANSS nicht angelegt. Ein
        // Wiederholungsversuch wuerde das nie aendern.
        queue.MarkFailed("sitzung-1", "403 TYPE_DOESNT_EXIST", nextAttempt: null);

        Assert.Equal(QueueState.Failed, queue.Find("sitzung-1")!.State);
        clock.Advance(TimeSpan.FromDays(7));
        Assert.Empty(queue.Lease(10));
        Assert.Equal(1, queue.Count(QueueState.Failed));
    }

    [Fact]
    public void Abgeschlossene_Eintraege_lassen_sich_erst_nach_der_Frist_raeumen()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using UploadQueue queue = new(temp.File("state.db"), clock);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));
        queue.MarkDone("sitzung-1");

        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(0, queue.PurgeCompleted(TimeSpan.FromDays(30)));

        clock.Advance(TimeSpan.FromDays(40));
        Assert.Equal(1, queue.PurgeCompleted(TimeSpan.FromDays(30)));
        Assert.Null(queue.Find("sitzung-1"));
    }

    [Fact]
    public void Die_Reihenfolge_richtet_sich_nach_der_Faelligkeit()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using UploadQueue queue = new(temp.File("state.db"), clock);

        _ = queue.Enqueue(Sample.Upload("spaet"));
        clock.Advance(TimeSpan.FromMinutes(5));
        _ = queue.Enqueue(Sample.Upload("frueh"));

        _ = queue.Lease(1);
        queue.MarkFailed("spaet", "Zeitueberschreitung",
            clock.GetUtcNow() + TimeSpan.FromHours(1));
        clock.Advance(TimeSpan.FromHours(2));

        QueuedUpload[] order = [.. queue.Lease(10)];
        Assert.Equal("frueh", order[0].RemoteMaintenanceId);
        Assert.Equal("spaet", order[1].RemoteMaintenanceId);
    }

    [Fact]
    public void Laufende_Sitzungen_ueberleben_einen_Neustart()
    {
        using TempDirectory temp = new();
        string path = temp.File("state.db");

        OpenSession running = new()
        {
            RemoteMaintenanceId = "sitzung-laeuft",
            MonitorKey = "rdp",
            RemoteSupportTypeId = 1001,
            StartedAt = Start,
            LastSeenAt = Start + TimeSpan.FromMinutes(12),
            ProcessId = 4711,
            Target = "10.20.30.40",
            // Der Bezeichner, aus dem die Geraetekennung fuer TANSS entsteht. Ohne ihn
            // buchte genau die unterbrochene Fernwartung ohne Firmenzuordnung.
            IdentityKey = "kunde-ts01.kunde.local",
            DeviceName = "kunde-ts01",
            UserName = "administrator",
            Comment = "Wartung Exchange",
            TicketId = 98765,
        };

        using (UploadQueue before = new(path))
        {
            before.SaveOpenSession(running);
        }

        // Absturz. Neuer Prozess, dieselbe Datei.
        using UploadQueue after = new(path);
        OpenSession restored = Assert.Single(after.LoadOpenSessions());

        Assert.Equal(running, restored);
        Assert.Equal("kunde-ts01.kunde.local", restored.IdentityKey);
        Assert.True(after.RemoveOpenSession("sitzung-laeuft"));
        Assert.False(after.RemoveOpenSession("sitzung-laeuft"));
        Assert.Empty(after.LoadOpenSessions());
    }

    [Fact]
    public void Ein_nachgelieferter_Bezeichner_wird_fortgeschrieben()
    {
        // Der Rueckwaertsaufloeser liefert den Namen oft erst Takte nach dem Beginn nach.
        // Wuerde die Spalte beim Fortschreiben stehen bleiben, ueberlebte der Neustart den
        // Platzhalter - und die Fernwartung buchte ohne Firmenzuordnung.
        using TempDirectory temp = new();
        using UploadQueue queue = new(temp.File("state.db"));

        OpenSession session = new()
        {
            RemoteMaintenanceId = "sitzung-aufloesung",
            MonitorKey = "rdp",
            RemoteSupportTypeId = 1001,
            StartedAt = Start,
            LastSeenAt = Start,
            IdentityKey = null,
        };

        queue.SaveOpenSession(session);
        Assert.Null(Assert.Single(queue.LoadOpenSessions()).IdentityKey);

        queue.SaveOpenSession(session with
        {
            LastSeenAt = Start + TimeSpan.FromMinutes(3),
            IdentityKey = "kunde-srv01",
        });

        Assert.Equal("kunde-srv01", Assert.Single(queue.LoadOpenSessions()).IdentityKey);
    }

    [Fact]
    public void Eine_laufende_Sitzung_wird_fortgeschrieben_statt_verdoppelt()
    {
        using TempDirectory temp = new();
        using UploadQueue queue = new(temp.File("state.db"));

        OpenSession first = new()
        {
            RemoteMaintenanceId = "sitzung-1",
            MonitorKey = "anydesk",
            RemoteSupportTypeId = 1002,
            StartedAt = Start,
            LastSeenAt = Start,
        };

        queue.SaveOpenSession(first);
        queue.SaveOpenSession(first with { LastSeenAt = Start + TimeSpan.FromMinutes(10) });

        OpenSession only = Assert.Single(queue.LoadOpenSessions());
        Assert.Equal(Start, only.StartedAt);
        Assert.Equal(Start + TimeSpan.FromMinutes(10), only.LastSeenAt);
    }

    [Fact]
    public void Eine_unlesbare_Nutzlast_haelt_die_Warteschlange_nicht_an()
    {
        // Der Kern der Sache: Die Warteschlange ist der Baustein, der verhindern soll, dass
        // eine Sitzung verlorengeht. Wenn eine einzige zerstoerte Zeile jeden Lease werfen
        // laesst, erreicht danach KEIN gesunder Eintrag mehr TANSS.
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        string path = temp.File("state.db");
        using UploadQueue queue = new(path, clock);

        for (int index = 1; index <= 4; index++)
        {
            Assert.True(queue.Enqueue(Sample.Upload($"sitzung-{index}")));
        }

        Raw.Execute(path,
            "UPDATE queue SET payload = '{kaputt' WHERE remote_maintenance_id = 'sitzung-2';");

        QueuedUpload[] leased = [.. queue.Lease(10)];

        Assert.Equal(3, leased.Length);
        Assert.DoesNotContain(leased, item => item.RemoteMaintenanceId == "sitzung-2");
        Assert.Equal(1, queue.Count(QueueState.Failed));

        // Die kaputte Zeile traegt ihren Grund und sagt, wie zu verfahren ist.
        Assert.Equal("failed", Raw.Text(path,
            "SELECT state FROM queue WHERE remote_maintenance_id = 'sitzung-2';"));
        string error = Raw.Text(path,
            "SELECT last_error FROM queue WHERE remote_maintenance_id = 'sitzung-2';")!;
        Assert.Contains("unlesbar", error, StringComparison.Ordinal);
        Assert.Contains("dedupliziert nicht", error, StringComparison.Ordinal);
        Assert.Contains("remoteSupports", error, StringComparison.Ordinal);

        // Und der naechste Durchlauf stolpert nicht erneut ueber dieselbe Zeile.
        Assert.Empty(queue.Lease(10));
        Assert.True(queue.Enqueue(Sample.Upload("sitzung-5")));
        Assert.Equal("sitzung-5", Assert.Single(queue.Lease(10)).RemoteMaintenanceId);
    }

    [Fact]
    public void Nach_der_unlesbaren_Zeile_wird_ausdruecklich_gefragt_nicht_geraten()
    {
        using TempDirectory temp = new();
        string path = temp.File("state.db");
        using UploadQueue queue = new(path);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));
        Raw.Execute(path, "UPDATE queue SET payload = '{kaputt';");

        // Find wird nach genau dieser Zeile gefragt - sie zu ueberspringen hiesse, „nicht
        // vorhanden“ zu antworten, obwohl sie dasteht.
        StateDatabaseException error =
            Assert.Throws<StateDatabaseException>(() => queue.Find("sitzung-1"));

        Assert.Contains("sitzung-1", error.Message, StringComparison.Ordinal);
        Assert.Contains("PUT /api/v1/remoteSupports", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Eine_unlesbare_Zeile_kostet_nicht_die_ganze_Liste()
    {
        using TempDirectory temp = new();
        string path = temp.File("state.db");
        using UploadQueue queue = new(path);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));
        _ = queue.Enqueue(Sample.Upload("sitzung-2"));
        _ = queue.Enqueue(Sample.Upload("sitzung-3"));

        Raw.Execute(path,
            "UPDATE queue SET payload = '{kaputt' WHERE remote_maintenance_id = 'sitzung-2';");

        QueueListing listed = queue.List(QueueState.Pending);

        Assert.Equal(2, listed.Items.Count);
        Assert.DoesNotContain(listed.Items, item => item.RemoteMaintenanceId == "sitzung-2");
    }

    [Fact]
    public void Die_Liste_verschweigt_die_uebersprungene_Zeile_nicht()
    {
        // Frueher stand „ausstehend: 4“ neben drei Eintraegen, ohne jeden Hinweis auf den
        // vierten. Die uebersprungene Zeile ist eine Fernwartung, die niemand hochlaedt -
        // sie darf nicht zwischen Liste und Zaehlung verschwinden.
        using TempDirectory temp = new();
        string path = temp.File("state.db");
        using UploadQueue queue = new(path);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));
        _ = queue.Enqueue(Sample.Upload("sitzung-2"));
        _ = queue.Enqueue(Sample.Upload("sitzung-3"));

        Raw.Execute(path,
            "UPDATE queue SET payload = '{kaputt' WHERE remote_maintenance_id = 'sitzung-2';");

        QueueListing listed = queue.List(QueueState.Pending);

        Assert.Equal(1, listed.Unreadable);
        Assert.Equal(queue.Count(QueueState.Pending), listed.Examined);
        Assert.NotNull(listed.UnreadableNotice);
        Assert.Contains("unlesbar", listed.UnreadableNotice!, StringComparison.Ordinal);
        // Der Satz sagt auch, was zu tun ist - und nicht nur, dass etwas fehlt.
        Assert.Contains("PUT /api/v1/remoteSupports", listed.UnreadableNotice!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Ohne_unlesbare_Zeile_gibt_es_nichts_zu_erklaeren()
    {
        using TempDirectory temp = new();
        using UploadQueue queue = new(temp.File("state.db"));

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));

        QueueListing listed = queue.List(QueueState.Pending);

        Assert.Equal(0, listed.Unreadable);
        Assert.Null(listed.UnreadableNotice);
        Assert.Equal(queue.Count(QueueState.Pending), listed.Examined);
    }

    [Fact]
    public void Eine_unlesbare_laufende_Sitzung_kostet_nicht_die_uebrigen()
    {
        // Das hier ist der erste Griff nach einem Neustart. Faellt er aus, ist der Beginn
        // JEDER laufenden Fernwartung unbekannt - rueckwirkend nicht mehr zu ermitteln.
        using TempDirectory temp = new();
        string path = temp.File("state.db");
        using UploadQueue queue = new(path);

        queue.SaveOpenSession(Session("sitzung-kaputt"));
        queue.SaveOpenSession(Session("sitzung-heil"));

        // Millisekunden statt Sekunden - so schriebe es ein fremdes Werkzeug oder ein
        // Umbau, der TanssTime umgeht. DateTimeOffset lehnt den Wert ab, und frueher nahm
        // diese eine Zeile jede andere laufende Sitzung mit.
        Raw.Execute(path,
            "UPDATE open_sessions SET started_at = 1757000000000 "
            + "WHERE remote_maintenance_id = 'sitzung-kaputt';");

        OpenSession restored = Assert.Single(queue.LoadOpenSessions());

        Assert.Equal("sitzung-heil", restored.RemoteMaintenanceId);
    }

    [Fact]
    public void Ein_zurueckgeholter_Eintrag_traegt_den_ungeklaerten_Ausgang()
    {
        // TANSS dedupliziert nicht (IDs 38584 und 38585). Ohne maschinenlesbares
        // Kennzeichen ist ein bereits angekommener Eintrag von einem nie gesendeten nicht
        // zu unterscheiden - und der Aufrufer kann die Existenzpruefung nicht festmachen.
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using UploadQueue queue = new(temp.File("state.db"), clock);

        _ = queue.Enqueue(Sample.Upload("sitzung-abgestuerzt"));
        _ = queue.Enqueue(Sample.Upload("sitzung-abgelehnt"));
        Assert.Equal(2, queue.Lease(10).Count);

        // Eindeutig abgelehnt: TANSS hat geantwortet, der Ausgang ist bekannt.
        queue.MarkFailed("sitzung-abgelehnt", "403 TYPE_DOESNT_EXIST",
            clock.GetUtcNow() + TimeSpan.FromMinutes(15));

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(1, queue.RequeueStuck(TimeSpan.FromMinutes(10)));

        QueuedUpload crashed = queue.Find("sitzung-abgestuerzt")!;
        QueuedUpload rejected = queue.Find("sitzung-abgelehnt")!;

        Assert.True(crashed.OutcomeUnknown);
        Assert.False(rejected.OutcomeUnknown);
        // Der Versuchszaehler taugt als Ersatz nicht: Er steht bei beiden auf 1.
        Assert.Equal(rejected.Attempts, crashed.Attempts);

        // Ein weiterer misslungener Versuch klaert den frueheren nicht auf ...
        queue.MarkFailed("sitzung-abgestuerzt", "TANSS nicht erreichbar",
            clock.GetUtcNow() + TimeSpan.FromMinutes(15));
        Assert.True(queue.Find("sitzung-abgestuerzt")!.OutcomeUnknown);

        // ... erst der Nachweis, dass die Fernwartung in TANSS steht.
        queue.MarkDone("sitzung-abgestuerzt");
        Assert.False(queue.Find("sitzung-abgestuerzt")!.OutcomeUnknown);
    }

    [Fact]
    public void Eine_nicht_gesetzte_Zuteilung_ergibt_keinen_Zeitpunkt_im_Jahr_1970()
    {
        // TanssTime ist der einzige Umrechner, und er liest die 0 als „nicht gesetzt“.
        // 1970 saehe dagegen aus wie eine Angabe - dieselbe stille Verfaelschung wie ein
        // Datum im Jahr 51667 aus Millisekunden.
        using TempDirectory temp = new();
        string path = temp.File("state.db");
        using UploadQueue queue = new(path);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));
        Raw.Execute(path, "UPDATE queue SET leased_at = 0, completed_at = 0;");

        QueuedUpload found = queue.Find("sitzung-1")!;

        Assert.Null(found.LeasedAt);
        Assert.Null(found.CompletedAt);
    }

    private static OpenSession Session(string id) => new()
    {
        RemoteMaintenanceId = id,
        MonitorKey = "rdp",
        RemoteSupportTypeId = 1001,
        StartedAt = Start,
        LastSeenAt = Start + TimeSpan.FromMinutes(5),
    };

    [Fact]
    public void Eine_Datenbank_aus_neuerer_Fassung_wird_nicht_geoeffnet()
    {
        using TempDirectory temp = new();
        string path = temp.File("state.db");

        using (StateDatabase database = new(path))
        {
            // Nur anlegen. Die Fassungsnummer setzt danach ein Fremdprozess - so, wie es
            // ein neuerer Programmstand taete.
        }

        SqliteConnectionStringBuilder builder = new() { DataSource = path, Pooling = false };
        using (SqliteConnection connection = new(builder.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            _ = command.ExecuteNonQuery();
        }

        StateDatabaseException error = Assert.Throws<StateDatabaseException>(() => new StateDatabase(path));
        Assert.Contains("neueren Programmfassung", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Der Fall, der „Jetzt senden“ wirkungslos aussehen liess: Der Eintrag steht als
    /// ausstehend da, wartet aber auf die Entscheidung des Technikers — und wird deshalb nicht
    /// zugeteilt.
    /// </summary>
    [Fact]
    public void Ein_wartender_Eintrag_wird_nicht_zugeteilt()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using UploadQueue queue = new(temp.File("state.db"), clock);

        Assert.True(queue.Enqueue(Sample.Upload("sitzung-1"), awaitDecision: true));

        Assert.Equal(1, queue.Count(QueueState.Pending));
        Assert.Empty(queue.Lease(10));
    }

    /// <summary>
    /// „Jetzt senden“ von Hand heisst jetzt: Das Freigeben hebt das Warten auf, und der
    /// nächste Zugriff bekommt den Eintrag.
    /// </summary>
    [Fact]
    public void Freigeben_zieht_einen_wartenden_Eintrag_sofort_vor()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using UploadQueue queue = new(temp.File("state.db"), clock);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"), awaitDecision: true);
        Assert.Empty(queue.Lease(10));

        Assert.True(queue.Release("sitzung-1"));
        Assert.False(queue.Find("sitzung-1")!.AwaitingDecision);

        QueuedUpload leased = Assert.Single(queue.Lease(10));
        Assert.Equal("sitzung-1", leased.RemoteMaintenanceId);
    }

    /// <summary>
    /// Auch der Rückstau nach einem Fehlversuch lässt sich von Hand vorziehen — sonst bliebe
    /// die Schaltfläche genau dann wirkungslos, wenn sie am dringendsten gebraucht wird.
    /// </summary>
    [Fact]
    public void Freigeben_wirkt_auch_auf_den_Rueckstau_nach_einem_Fehlversuch()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using UploadQueue queue = new(temp.File("state.db"), clock);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));
        _ = queue.Lease(1);
        queue.MarkFailed("sitzung-1", "Zeitueberschreitung",
                         clock.GetUtcNow() + TimeSpan.FromHours(1));

        Assert.Empty(queue.Lease(10));
        Assert.True(queue.Release("sitzung-1"));
        Assert.Single(queue.Lease(10));
    }

    /// <summary>
    /// Was nicht wartet, lässt sich auch nicht vorziehen. Der Rückgabewert ist die Grundlage
    /// der Meldung „n freigegeben“; er darf nichts zählen, was gar nichts war.
    /// </summary>
    [Fact]
    public void Freigeben_meldet_false_wenn_es_nichts_freizugeben_gibt()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using UploadQueue queue = new(temp.File("state.db"), clock);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));

        Assert.False(queue.Release("gibt-es-nicht"));

        // Faellig, aber nicht zurueckgehalten: Das Vorziehen setzt zwar dieselbe Spalte, meldet
        // aber trotzdem einen Treffer - deshalb prueft der Dienst die Faelligkeit vorher selbst.
        Assert.True(queue.Release("sitzung-1"));
    }
}
