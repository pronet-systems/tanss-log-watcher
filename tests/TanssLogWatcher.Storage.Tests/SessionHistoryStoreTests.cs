using TanssLogWatcher.Monitoring;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.History;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Recordings;
using TanssLogWatcher.Storage.Secrets;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Der Verlauf abgeschlossener Sitzungen: was er festhält, was er nach der Frist hergibt und
/// was er trotz Frist stehen lässt.
/// </summary>
/// <remarks>
/// Durchweg echtes SQLite in einem Wegwerfverzeichnis. Genau die Eigenschaften, um die es
/// hier geht — der Primärschlüssel als Riegel gegen die zweite Zeile, die Oder-Bedingung des
/// Aufräumens, die Unterabfrage auf <c>recordings</c> —, hat eine Attrappe nicht; ein Test
/// dagegen prüfte nur die Attrappe.
/// </remarks>
public sealed class SessionHistoryStoreTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    [Fact]
    public void Eine_abgeschlossene_Sitzung_ist_danach_wiederzufinden()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using SessionHistoryStore store = Store(temp, clock, redact: false);

        DateTimeOffset? deleteAfter = store.Record(Entry("sitzung-1") with
        {
            ProfileKey = "mstsc",
            ProfileName = "Microsoft Remotedesktop",
            Destination = "kunde-srv01",
            RemoteSupportTypeId = 1002,
            TicketId = 4711,
            Disposition = HistoryDisposition.Enqueued,
            Reason = "Eingereiht, wartet auf den nächsten Sendelauf",
        });

        Assert.Equal(Start + Retention, deleteAfter);

        SessionHistoryEntry entry = Assert.Single(store.Recent(50, onlyWithRecording: false).Items);

        Assert.Equal("sitzung-1", entry.SessionId);
        Assert.Equal(Start, entry.StartedAt);
        Assert.Equal(Start + TimeSpan.FromMinutes(20), entry.EndedAt);
        Assert.Equal(TimeSpan.FromMinutes(20), entry.Duration);
        Assert.Equal("Microsoft Remotedesktop", entry.ProfileName);
        Assert.Equal("kunde-srv01", entry.Destination);
        Assert.Equal("kunde-srv01", entry.DestinationLabel);
        Assert.Equal(1002, entry.RemoteSupportTypeId);
        Assert.Equal(4711, entry.TicketId);
        Assert.Equal(HistoryDisposition.Enqueued, entry.Disposition);
        Assert.Equal(HistoryOrigin.Live, entry.Origin);
        Assert.Equal(Start + Retention, entry.DeleteAfter);
        Assert.Equal(Start, entry.CreatedAt);
        Assert.Null(entry.RedactedAt);
        Assert.Equal(RecordingPresence.None, entry.Recording.Presence);
    }

    /// <summary>
    /// Nach einem Neustart schliesst der Beobachter eine unterbrochene Sitzung ein zweites
    /// Mal ab. Das darf keine zweite Zeile geben — der Primärschlüssel ist der Riegel.
    /// </summary>
    [Fact]
    public void Zweimal_abschliessen_ergibt_eine_Zeile_und_schreibt_sie_fort()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using SessionHistoryStore store = Store(temp, clock, redact: false);

        _ = store.Record(Entry("sitzung-1") with
        {
            ProfileName = "Microsoft Remotedesktop",
            Disposition = HistoryDisposition.Enqueued,
            Reason = "Eingereiht",
        });

        _ = store.Record(Entry("sitzung-1") with
        {
            EndedAt = Start + TimeSpan.FromMinutes(45),
            // Der zweite Abschluss weiss den Profilnamen nicht mehr. Er darf ihn deshalb
            // nicht loeschen.
            ProfileName = string.Empty,
            Disposition = HistoryDisposition.Failed,
            Reason = "Versand endgültig misslungen",
        });

        Assert.Equal(1, store.Count());

        SessionHistoryEntry entry = Assert.Single(store.Recent(50, onlyWithRecording: false).Items);

        Assert.Equal(Start + TimeSpan.FromMinutes(45), entry.EndedAt);
        Assert.Equal(HistoryDisposition.Failed, entry.Disposition);
        Assert.Equal("Versand endgültig misslungen", entry.Reason);
        Assert.Equal("Microsoft Remotedesktop", entry.ProfileName);
    }

    [Fact]
    public void Buchen_schreibt_die_TANSS_Kennung_in_die_Zeile()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using SessionHistoryStore store = Store(temp, clock, redact: false);

        _ = store.Record(Entry("sitzung-1"));

        Assert.True(store.MarkBooked("sitzung-1", 38584, "HTTP 201, in TANSS angelegt"));

        SessionHistoryEntry entry = Assert.Single(store.Recent(50, onlyWithRecording: false).Items);

        Assert.Equal(HistoryDisposition.Booked, entry.Disposition);
        Assert.True(entry.IsBooked);
        Assert.Equal(38584, entry.TanssSupportId);
        Assert.Equal("HTTP 201, in TANSS angelegt", entry.Reason);
    }

    [Fact]
    public void Eine_unbekannte_Sitzung_zu_buchen_meldet_sich_statt_still_zu_bleiben()
    {
        using TempDirectory temp = new();
        using SessionHistoryStore store = Store(temp, new ManualTimeProvider(Start), redact: false);

        Assert.False(store.MarkBooked("gibt-es-nicht", 1, "Nichts zu finden"));
        Assert.Null(store.Find("gibt-es-nicht"));
    }

    /// <summary>
    /// Stünde nach dem erfolgreichen Versand wieder „eingereiht“, legte der Techniker die
    /// Fernwartung ein zweites Mal an.
    /// </summary>
    [Fact]
    public void Eine_gebuchte_Zeile_wird_nicht_zurueckgestuft()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using SessionHistoryStore store = Store(temp, clock, redact: false);

        _ = store.Record(Entry("sitzung-1"));
        Assert.True(store.MarkBooked("sitzung-1", 38584, "In TANSS angelegt"));

        Assert.Null(store.Record(Entry("sitzung-1") with
        {
            Disposition = HistoryDisposition.Enqueued,
            Reason = "Später Abschluss nach Neustart",
        }));
        Assert.False(store.MarkDiscarded("sitzung-1", "Versehentlich verworfen"));

        SessionHistoryEntry entry = Assert.Single(store.Recent(50, onlyWithRecording: false).Items);

        Assert.Equal(HistoryDisposition.Booked, entry.Disposition);
        Assert.Equal(38584, entry.TanssSupportId);
    }

    [Fact]
    public void Verwerfen_hinterlaesst_eine_Zeile_statt_einer_Luecke()
    {
        using TempDirectory temp = new();
        using SessionHistoryStore store = Store(temp, new ManualTimeProvider(Start), redact: false);

        _ = store.Record(Entry("sitzung-1"));

        Assert.True(store.MarkDiscarded("sitzung-1", "Vom Techniker im Abschlussdialog verworfen"));

        SessionHistoryEntry entry = Assert.Single(store.Recent(50, onlyWithRecording: false).Items);

        Assert.Equal(HistoryDisposition.Discarded, entry.Disposition);
        Assert.Equal("Vom Techniker im Abschlussdialog verworfen", entry.Reason);
    }

    // === Die Gegenstelle ===

    [Fact]
    public void Schwaerzen_nimmt_den_Klartext_und_laesst_die_Zeile()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint print = KeyedFingerprint.CreateRandom();
        using SessionHistoryStore store = Store(temp, clock, redact: false, print);

        _ = store.Record(Entry("sitzung-1") with { Destination = "kunde-srv01" });

        clock.Advance(TimeSpan.FromDays(31));

        Assert.Equal(1, store.Redact(TimeSpan.FromDays(30)));
        Assert.Equal(1, store.Count());

        SessionHistoryEntry entry = Assert.Single(store.Recent(50, onlyWithRecording: false).Items);

        Assert.Null(entry.Destination);
        Assert.Equal(print.Of("kunde-srv01"), entry.DestinationFingerprint);
        Assert.Equal(Start + TimeSpan.FromDays(31), entry.RedactedAt);
        Assert.True(entry.IsDestinationRedacted);
        Assert.False(entry.IsDestinationUnknown);
        Assert.Equal("Gegenstelle geschwärzt", entry.DestinationLabel);

        // Ein zweiter Lauf hat nichts mehr zu tun - und schreibt deshalb auch keinen neuen
        // Zeitpunkt ueber den ersten.
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, store.Redact(TimeSpan.FromDays(30)));
        Assert.Equal(Start + TimeSpan.FromDays(31),
                     store.Find("sitzung-1")!.RedactedAt);
    }

    [Fact]
    public void Vor_Ablauf_der_Klartextfrist_bleibt_die_Gegenstelle_stehen()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using SessionHistoryStore store = Store(temp, clock, redact: false);

        _ = store.Record(Entry("sitzung-1") with { Destination = "kunde-srv01" });

        clock.Advance(TimeSpan.FromDays(29));

        Assert.Equal(0, store.Redact(TimeSpan.FromDays(30)));
        Assert.Equal("kunde-srv01", store.Find("sitzung-1")!.Destination);
    }

    /// <summary>
    /// Steht die Schwärzung, darf der Klartext gar nicht erst auf die Platte — und die
    /// Anzeige muss trotzdem „geschwärzt“ und nicht „nicht ermittelt“ sagen können.
    /// </summary>
    [Fact]
    public void Bei_eingeschalteter_Schwaerzung_steht_der_Klartext_nie_in_der_Datenbank()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint print = KeyedFingerprint.CreateRandom();
        using SessionHistoryStore store = Store(temp, clock, redact: true, print);

        Assert.True(store.RedactsDestination);
        _ = store.Record(Entry("sitzung-1") with { Destination = "kunde-srv01" });

        Assert.Null(Raw.Text(temp.File("state.db"),
            "SELECT destination FROM session_history WHERE remote_maintenance_id = 'sitzung-1';"));

        SessionHistoryEntry entry = store.Find("sitzung-1")!;

        Assert.Equal(print.Of("kunde-srv01"), entry.DestinationFingerprint);
        Assert.Equal(Start, entry.RedactedAt);
        Assert.True(entry.IsDestinationRedacted);
        Assert.Equal("Gegenstelle geschwärzt", entry.DestinationLabel);
    }

    /// <summary>
    /// Acht Hexziffern sind keine Gegenstelle. Der Abdruck des Platzhalters belegt gerade,
    /// dass nie ein Titel aufgelöst wurde — er gehört auf <c>null</c>.
    /// </summary>
    [Fact]
    public void Der_Platzhalter_wird_nicht_zur_Gegenstelle()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint print = KeyedFingerprint.CreateRandom();
        using SessionHistoryStore store = Store(temp, clock, redact: true, print);

        _ = store.Record(Entry("sitzung-1") with
        {
            Destination = SessionConstants.NoDestinationTitle,
        });

        // Und derselbe Riegel, wenn der Nachtrag nur den Abdruck aus dem Protokoll mitbringt.
        _ = store.Record(Entry("sitzung-2") with
        {
            Origin = HistoryOrigin.Backfill,
            DestinationFingerprint = print.Of(SessionConstants.NoDestinationTitle),
        });

        foreach (string id in new[] { "sitzung-1", "sitzung-2" })
        {
            SessionHistoryEntry entry = store.Find(id)!;

            Assert.Null(entry.Destination);
            Assert.Null(entry.DestinationFingerprint);
            Assert.Null(entry.RedactedAt);
            Assert.True(entry.IsDestinationUnknown);
            Assert.Equal("Gegenstelle nicht ermittelt", entry.DestinationLabel);
        }
    }

    /// <summary>
    /// Ein echter Abdruck aus dem Nachtrag bleibt erhalten — er beantwortet weiter, ob zwei
    /// Sitzungen zur selben Gegenstelle gingen.
    /// </summary>
    [Fact]
    public void Ein_nachgetragener_Abdruck_bleibt_erhalten_und_gilt_als_geschwaerzt()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint print = KeyedFingerprint.CreateRandom();
        using SessionHistoryStore store = Store(temp, clock, redact: true, print);

        _ = store.Record(Entry("sitzung-1") with
        {
            Origin = HistoryOrigin.Backfill,
            DestinationFingerprint = print.Of("kunde-srv01"),
        });

        SessionHistoryEntry entry = store.Find("sitzung-1")!;

        Assert.Equal(HistoryOrigin.Backfill, entry.Origin);
        Assert.Equal(print.Of("kunde-srv01"), entry.DestinationFingerprint);
        Assert.True(entry.IsDestinationRedacted);
    }

    /// <summary>
    /// Derselbe Schlüssel wie im Änderungsprotokoll — sonst trüge dieselbe Gegenstelle zwei
    /// verschiedene Abdrücke und die einzige Frage, für die der Abdruck da ist, wäre nicht
    /// mehr zu beantworten.
    /// </summary>
    [Fact]
    public void Der_Abdruck_ist_derselbe_wie_im_Aenderungsprotokoll()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        KeyedFingerprint print = KeyedFingerprint.CreateRandom();

        using StateDatabase database = new(temp.File("state.db"));
        using SessionLog log = new(database, clock, redactWindowTitles: true, print);
        using SessionHistoryStore store =
            new(database, Retention, redactDestination: true, timeProvider: clock,
                fingerprint: print);

        _ = log.Append(new SessionLogEntry
        {
            RemoteMaintenanceId = "sitzung-1",
            Operation = "watch/deferred",
            Outcome = SessionOutcome.Deferred,
            Trigger = SessionTrigger.Watcher,
            Reason = "Sitzung erkannt",
            WindowTitle = "kunde-srv01",
        });
        _ = store.Record(Entry("sitzung-1") with { Destination = "kunde-srv01" });

        string abdruck = store.Find("sitzung-1")!.DestinationFingerprint!;
        SessionLogEntry logged = Assert.Single(log.ForSession("sitzung-1"));

        Assert.Contains(abdruck, logged.WindowTitle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die Vorgabe folgt <c>logging.redact_window_titles</c> und kehrt die Entscheidung des
    /// Technikers nicht still um.
    /// </summary>
    [Fact]
    public void Aus_der_Konfiguration_folgt_die_Schwaerzung_dem_Protokoll()
    {
        using TempDirectory temp = new();
        using StateDatabase database = new(temp.File("state.db"));

        AppConfig geschwaerzt = Sample.Config() with
        {
            Logging = new LoggingSection { RedactWindowTitles = true },
        };
        AppConfig klartext = Sample.Config() with
        {
            Logging = new LoggingSection { RedactWindowTitles = false },
        };

        using (SessionHistoryStore store = SessionHistoryStore.FromConfig(geschwaerzt, database))
        {
            Assert.True(store.RedactsDestination);
        }

        using (SessionHistoryStore store = SessionHistoryStore.FromConfig(klartext, database))
        {
            Assert.False(store.RedactsDestination);
        }

        Assert.Equal(TimeSpan.FromDays(HistorySection.DefaultPlainTextDays),
                     SessionHistoryStore.PlainTextRetentionOf(geschwaerzt));
    }

    // === Aufbewahrung ===

    [Fact]
    public void Vor_Ablauf_der_Frist_bleibt_die_Zeile()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using SessionHistoryStore store = Store(temp, clock, redact: false);

        _ = store.Record(Entry("sitzung-1"));

        clock.Advance(TimeSpan.FromDays(89));

        Assert.Equal(0, store.Prune(Retention));
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void Nach_Ablauf_der_Frist_geht_die_Zeile()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using SessionHistoryStore store = Store(temp, clock, redact: false);

        _ = store.Record(Entry("sitzung-1"));

        clock.Advance(TimeSpan.FromDays(91));

        Assert.Equal(1, store.Prune(Retention));
        Assert.Equal(0, store.Count());
    }

    /// <summary>
    /// Ein Versprechen darf jederzeit enger gefasst werden. Ohne die Oder-Bedingung im
    /// Aufräumen wirkte eine verkürzte Frist erst auf das, was danach entsteht.
    /// </summary>
    [Fact]
    public void Eine_verkuerzte_Frist_wirkt_auf_bestehende_Zeilen()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using SessionHistoryStore store = Store(temp, clock, redact: false);

        Assert.Equal(Start + Retention, store.Record(Entry("sitzung-1")));

        clock.Advance(TimeSpan.FromDays(40));

        // Der festgeschriebene Loeschzeitpunkt liegt noch weit vorn; massgeblich ist die
        // heute eingestellte, kuerzere Frist.
        Assert.Equal(1, store.Prune(TimeSpan.FromDays(30)));
        Assert.Equal(0, store.Count());
    }

    /// <summary>Und die Gegenprobe: Ein gegebenes Versprechen wird nicht gedehnt.</summary>
    [Fact]
    public void Eine_verlaengerte_Frist_haelt_eine_faellige_Zeile_nicht_zurueck()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using StateDatabase database = new(temp.File("state.db"));
        using SessionHistoryStore store =
            new(database, TimeSpan.FromDays(30), redactDestination: false, timeProvider: clock,
                fingerprint: KeyedFingerprint.CreateRandom());

        _ = store.Record(Entry("sitzung-1"));

        clock.Advance(TimeSpan.FromDays(31));

        Assert.Equal(1, store.Prune(TimeSpan.FromDays(365)));
    }

    /// <summary>
    /// Sonst läge eine Videodatei da, zu der niemand mehr sagen kann, wessen Sitzung sie
    /// zeigt.
    /// </summary>
    [Fact]
    public void Eine_Zeile_mit_liegender_Aufzeichnung_bleibt_ueber_die_Frist_hinaus_stehen()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using StateDatabase database = new(temp.File("state.db"));
        using RecordingStore recordings = new(database, clock);
        using SessionHistoryStore store =
            new(database, Retention, redactDestination: false, timeProvider: clock,
                fingerprint: KeyedFingerprint.CreateRandom());

        long recording = recordings.Begin("sitzung-1", "sitzung.mp4", 1, TimeSpan.FromDays(365));
        _ = recordings.Finish(recording, TimeSpan.FromMinutes(18), bytes: 1_448_036);
        _ = store.Record(Entry("sitzung-1"));

        clock.Advance(TimeSpan.FromDays(91));

        Assert.Equal(0, store.Prune(Retention));
        Assert.Equal(1, store.Count());

        // Erst wenn die Aufzeichnung fort ist, geht auch die Zeile.
        Assert.True(recordings.MarkPurged(recording, "Aufbewahrungsfrist abgelaufen"));

        Assert.Equal(1, store.Prune(Retention));
        Assert.Equal(0, store.Count());
    }

    // === Die Aufzeichnung, mitgelesen ===

    /// <summary>
    /// Gemessen trägt eine Sitzung dieser Datenbank drei Dateien mit drei verschiedenen
    /// Löschzeitpunkten. Wer nur den ersten Abschnitt läse, spielte vier von fünfzehn
    /// Sekunden.
    /// </summary>
    [Fact]
    public void Die_Zusammenfassung_liest_alle_Abschnitte_einer_Sitzung()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using StateDatabase database = new(temp.File("state.db"));
        using RecordingStore recordings = new(database, clock);
        using SessionHistoryStore store =
            new(database, Retention, redactDestination: false, timeProvider: clock,
                fingerprint: KeyedFingerprint.CreateRandom());

        _ = store.Record(Entry("sitzung-1"));
        Finish(recordings, clock, "sitzung-1", "teil-01.mp4", 1, TimeSpan.FromSeconds(4), 70_323);
        Finish(recordings, clock, "sitzung-1", "teil-02.mp4", 2, TimeSpan.FromSeconds(1), 31_019);
        Finish(recordings, clock, "sitzung-1", "teil-03.mp4", 3, TimeSpan.FromSeconds(10), 1_758_887);

        SessionRecordingSummary recording = store.Find("sitzung-1")!.Recording;

        Assert.Equal(3, recording.SegmentCount);
        Assert.Equal(3, recording.KeptCount);
        Assert.Equal(TimeSpan.FromSeconds(15), recording.Recorded);
        Assert.Equal(TimeSpan.FromSeconds(15), recording.PlayableRecorded);
        Assert.Equal(70_323 + 31_019 + 1_758_887, recording.Bytes);
        Assert.Equal(RecordingPresence.Present, recording.Presence);
        Assert.Equal("teil-01.mp4", recording.PlayableRelativePath);
        Assert.True(recording.HasFurtherPlayableSegments);
        Assert.Equal([1, 2, 3], recording.Segments.Select(segment => segment.Number));

        // Drei Abschnitte, drei Loeschzeitpunkte - der naechste ist der frueheste.
        Assert.Equal(Start + TimeSpan.FromDays(30), recording.NextDeleteAfter);
    }

    /// <summary>
    /// Der Fall, den der Abspielknopf sonst still verschluckt: Abschnitt 1 ist gelöscht, 2
    /// und 3 liegen noch. Herausgegeben wird der Pfad, dessen Zustand geprüft wurde.
    /// </summary>
    [Fact]
    public void Nach_einem_Teil_Loeschlauf_zeigt_der_Abspielpfad_auf_einen_liegenden_Abschnitt()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using StateDatabase database = new(temp.File("state.db"));
        using RecordingStore recordings = new(database, clock);
        using SessionHistoryStore store =
            new(database, Retention, redactDestination: false, timeProvider: clock,
                fingerprint: KeyedFingerprint.CreateRandom());

        _ = store.Record(Entry("sitzung-1"));
        long erster = Finish(recordings, clock, "sitzung-1", "teil-01.mp4", 1,
                             TimeSpan.FromSeconds(4), 70_323);
        _ = Finish(recordings, clock, "sitzung-1", "teil-02.mp4", 2,
                   TimeSpan.FromSeconds(1), 31_019);
        _ = Finish(recordings, clock, "sitzung-1", "teil-03.mp4", 3,
                   TimeSpan.FromSeconds(10), 1_758_887);

        Assert.True(recordings.MarkPurged(erster, "Aufbewahrungsfrist abgelaufen"));

        SessionRecordingSummary recording = store.Find("sitzung-1")!.Recording;

        Assert.Equal(1, recording.PurgedCount);
        Assert.Equal(2, recording.KeptCount);
        Assert.Equal("teil-02.mp4", recording.PlayableRelativePath);
        Assert.Equal(2, recording.PlayableSegment!.Number);
        Assert.True(recording.IsPlayable);
        Assert.Equal(RecordingPresence.Present, recording.Presence);
        // Aufgezeichnet wurden fuenfzehn Sekunden, abspielbar sind noch elf - und die
        // Anzeige kann den Unterschied sagen, statt ihn zu verschweigen.
        Assert.Equal(TimeSpan.FromSeconds(15), recording.Recorded);
        Assert.Equal(TimeSpan.FromSeconds(11), recording.PlayableRecorded);
        Assert.Equal("Aufbewahrungsfrist abgelaufen", recording.LastDeleteReason);
    }

    /// <summary>
    /// Sind alle Abschnitte gelöscht, bleibt die Zeile — und sagt, dass es sie gab. Ein
    /// laufender Abschnitt gilt dabei nicht als abspielbar: Seine Datei wird gerade
    /// geschrieben.
    /// </summary>
    [Fact]
    public void Eine_geloeschte_Aufzeichnung_bleibt_als_Grabstein_sichtbar()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using StateDatabase database = new(temp.File("state.db"));
        using RecordingStore recordings = new(database, clock);
        using SessionHistoryStore store =
            new(database, Retention, redactDestination: false, timeProvider: clock,
                fingerprint: KeyedFingerprint.CreateRandom());

        _ = store.Record(Entry("sitzung-1"));
        long id = Finish(recordings, clock, "sitzung-1", "sitzung.mp4", 1,
                         TimeSpan.FromMinutes(18), 1_448_036);
        Assert.True(recordings.MarkPurged(id, "Aufbewahrungsfrist abgelaufen"));

        _ = store.Record(Entry("sitzung-2"));
        _ = recordings.Begin("sitzung-2", "sitzung.mp4", 1, TimeSpan.FromDays(30));

        SessionRecordingSummary geloescht = store.Find("sitzung-1")!.Recording;
        Assert.Equal(RecordingPresence.Purged, geloescht.Presence);
        Assert.False(geloescht.IsPlayable);
        Assert.Null(geloescht.PlayableRelativePath);
        Assert.Null(geloescht.NextDeleteAfter);
        Assert.Equal(TimeSpan.FromMinutes(18), geloescht.Recorded);

        SessionRecordingSummary laeuft = store.Find("sitzung-2")!.Recording;
        Assert.Equal(RecordingPresence.Running, laeuft.Presence);
        Assert.False(laeuft.IsPlayable);
    }

    // === Die Seite ===

    [Fact]
    public void Die_Seite_liefert_die_juengsten_zuerst_und_sagt_wie_viele_es_gibt()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using SessionHistoryStore store = Store(temp, clock, redact: false);

        for (int i = 0; i < 5; i++)
        {
            _ = store.Record(Entry("sitzung-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)) with
            {
                StartedAt = Start + TimeSpan.FromHours(i),
                EndedAt = Start + TimeSpan.FromHours(i) + TimeSpan.FromMinutes(10),
            });
        }

        SessionHistoryPage page = store.Recent(2, onlyWithRecording: false);

        Assert.Equal(5, page.Total);
        Assert.True(page.HasMore);
        Assert.Equal(["sitzung-4", "sitzung-3"], page.Items.Select(item => item.SessionId));

        SessionHistoryPage alle = store.Recent(50, onlyWithRecording: false);

        Assert.Equal(5, alle.Total);
        Assert.False(alle.HasMore);
    }

    /// <summary>
    /// Gefiltert wird nach dem Eintrag in der Buchführung, nicht nach der Datei: Sonst
    /// verschwände mit der gelöschten Aufzeichnung auch der Nachweis, dass es sie gab.
    /// </summary>
    [Fact]
    public void Der_Filter_behaelt_auch_Sitzungen_mit_bereits_geloeschter_Aufzeichnung()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using StateDatabase database = new(temp.File("state.db"));
        using RecordingStore recordings = new(database, clock);
        using SessionHistoryStore store =
            new(database, Retention, redactDestination: false, timeProvider: clock,
                fingerprint: KeyedFingerprint.CreateRandom());

        _ = store.Record(Entry("ohne-video"));
        _ = store.Record(Entry("mit-video") with { StartedAt = Start + TimeSpan.FromHours(1) });
        long id = Finish(recordings, clock, "mit-video", "sitzung.mp4", 1,
                         TimeSpan.FromMinutes(2), 1000);
        Assert.True(recordings.MarkPurged(id, "Aufbewahrungsfrist abgelaufen"));

        SessionHistoryPage page = store.Recent(50, onlyWithRecording: true);

        Assert.Equal(1, page.Total);
        Assert.False(page.HasMore);
        Assert.Equal("mit-video", Assert.Single(page.Items).SessionId);
        Assert.Equal(2, store.Recent(50, onlyWithRecording: false).Total);
    }

    [Fact]
    public void Eine_Seite_ohne_Zeilen_behauptet_nichts()
    {
        using TempDirectory temp = new();
        using SessionHistoryStore store = Store(temp, new ManualTimeProvider(Start), redact: false);

        SessionHistoryPage page = store.Recent(50, onlyWithRecording: false);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void Eine_Seitengroesse_unter_eins_wird_abgewiesen()
    {
        using TempDirectory temp = new();
        using SessionHistoryStore store = Store(temp, new ManualTimeProvider(Start), redact: false);

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => store.Recent(0, onlyWithRecording: false));
    }

    private static SessionHistoryStore Store(TempDirectory temp, TimeProvider clock, bool redact,
                                             KeyedFingerprint? print = null) =>
        new(temp.File("state.db"), Retention, redact, clock,
            print ?? KeyedFingerprint.CreateRandom());

    /// <summary>
    /// Eine Sitzung ohne Gegenstelle, wie sie der Beobachter beim Abschluss reicht.
    /// </summary>
    /// <remarks>
    /// <c>DeleteAfter</c> ist im Datensatz verlangt, wird vom Speicher aber selbst gesetzt;
    /// der Wert hier ist deshalb bewusst leer und keine Behauptung.
    /// </remarks>
    private static SessionHistoryEntry Entry(string id) => new()
    {
        SessionId = id,
        StartedAt = Start,
        EndedAt = Start + TimeSpan.FromMinutes(20),
        Disposition = HistoryDisposition.Enqueued,
        DeleteAfter = default,
    };

    private static long Finish(RecordingStore recordings, ManualTimeProvider clock,
                               string session, string path, int segment, TimeSpan recorded,
                               long bytes)
    {
        long id = recordings.Begin(session, path, segment, TimeSpan.FromDays(30));
        // Die Uhr laeuft zwischen den Abschnitten weiter - nur so tragen sie verschiedene
        // Loeschzeitpunkte, wie gemessen.
        clock.Advance(recorded);
        _ = recordings.Finish(id, recorded, bytes);
        return id;
    }
}
