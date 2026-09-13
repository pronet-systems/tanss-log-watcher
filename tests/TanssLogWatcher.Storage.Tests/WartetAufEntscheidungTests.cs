using TanssLogWatcher.Storage.Queue;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Die Zeile, die auf die Entscheidung des Technikers wartet — und die keine Uhr freigibt.
/// </summary>
/// <remarks>
/// <para><b>Was vorher hier stand und warum es fort ist.</b> An dieser Stelle standen zwei
/// Prüfungen über eine Schonfrist von fünf Minuten. Die Frist hat funktioniert — gemessen an
/// der Zustandsdatenbank dieses Arbeitsplatzes: Die eine Zeile, an der niemand den
/// Abschlussdialog bestätigt hat (01:37:01), trug volle 300 Sekunden und ging um 01:42:26
/// hinaus, also exakt nach fünf Minuten, und zwar mit der automatischen Beschreibung statt
/// mit Bericht, Firma und Ticket. Genau das ist der Befund des Technikers: „ich wurde nach
/// Beenden der Session nicht gefragt“ — die Sitzung war da schon gebucht.</para>
///
/// <para><b>Deshalb prüft diese Datei das Gegenteil:</b> dass eine frisch eingereihte Sitzung
/// auch nach Tagen nicht fällig wird, dass erst eine Entscheidung sie herauslässt, und dass
/// eine Zeile aus einer älteren Fassung davon nicht betroffen ist — sie hat keinen Dialog
/// mehr, auf den sie warten könnte, und muss hinausgehen.</para>
/// </remarks>
public sealed class WartetAufEntscheidungTests
{
    /// <summary>Das gemessene Sitzungsende vom 13.09.2026, 15:33:01.</summary>
    private static readonly DateTimeOffset Sitzungsende =
        new(2026, 9, 13, 15, 33, 1, TimeSpan.Zero);

    /// <summary>
    /// Der Kern des Umbaus: Kein Zeitablauf gibt die Zeile frei.
    /// </summary>
    /// <remarks>
    /// Sieben Tage sind hier kein runder Wert, sondern das Wochenende: Wer am Freitagabend
    /// die Fernwartung schliesst und den Dialog nicht mehr beantwortet, findet ihn am Montag
    /// unbeantwortet vor — und nicht eine gebuchte Fernwartung ohne Bericht.
    /// </remarks>
    [Fact]
    public void Vor_der_Entscheidung_wird_nichts_faellig_auch_nach_Tagen_nicht()
    {
        using TempDirectory temp = new();
        ManualTimeProvider uhr = new(Sitzungsende);
        using UploadQueue queue = new(temp.File("state.db"), uhr);

        Assert.True(queue.Enqueue(Sample.Upload("sitzung"), awaitDecision: true));

        QueuedUpload? zeile = queue.Find("sitzung");
        Assert.NotNull(zeile);
        Assert.True(zeile!.AwaitingDecision);

        Assert.Empty(queue.Lease(10));

        uhr.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(queue.Lease(10));

        uhr.Advance(TimeSpan.FromDays(7));
        Assert.Empty(queue.Lease(10));

        // Und sie ist auch nicht verschwunden: Sie steht noch da, zaehlt mit und ist beim
        // naechsten Start wieder vorzulegen.
        Assert.Equal(1, queue.Count(QueueState.Pending));
        Assert.True(queue.Find("sitzung")!.AwaitingDecision);
    }

    /// <summary>„Später“: Die Zeile wird freigegeben und geht mit dem nächsten Sendelauf.</summary>
    [Fact]
    public void Nach_Spaeter_ist_die_Zeile_sofort_faellig()
    {
        using TempDirectory temp = new();
        ManualTimeProvider uhr = new(Sitzungsende);
        using UploadQueue queue = new(temp.File("state.db"), uhr);

        _ = queue.Enqueue(Sample.Upload("sitzung"), awaitDecision: true);
        Assert.Empty(queue.Lease(10));

        uhr.Advance(TimeSpan.FromSeconds(4));
        Assert.True(queue.Release("sitzung"));

        QueuedUpload zugeteilt = Assert.Single(queue.Lease(10));
        Assert.Equal("sitzung", zugeteilt.RemoteMaintenanceId);
        Assert.False(zugeteilt.AwaitingDecision);
        Assert.Equal(QueueState.Sending, zugeteilt.State);
    }

    /// <summary>
    /// „In TANSS buchen“: Der Klick teilt genau diese eine Zeile zu, ohne auf Fälligkeit oder
    /// Warten zu achten — der Klick <b>ist</b> die Entscheidung.
    /// </summary>
    /// <remarks>
    /// Zugeteilt heisst hier dasselbe wie im Sendedienst: Zustand <c>sending</c>, quittiert
    /// wird mit <c>MarkDone</c> oder <c>MarkFailed</c>. Ein eigener Weg mit eigenem Zustand
    /// hätte eine zweite Wahrheit darüber geschaffen, was gerade unterwegs ist.
    /// </remarks>
    [Fact]
    public void Das_Buchen_aus_dem_Dialog_teilt_die_wartende_Zeile_zu()
    {
        using TempDirectory temp = new();
        ManualTimeProvider uhr = new(Sitzungsende);
        using UploadQueue queue = new(temp.File("state.db"), uhr);

        _ = queue.Enqueue(Sample.Upload("sitzung"), awaitDecision: true);

        QueuedUpload? zugeteilt = queue.LeaseOne("sitzung");

        Assert.NotNull(zugeteilt);
        Assert.Equal(QueueState.Sending, zugeteilt!.State);
        Assert.False(zugeteilt.AwaitingDecision);

        // Zweimal zuteilen geht nicht - sonst stuende dieselbe Fernwartung zweimal in TANSS,
        // und TANSS dedupliziert nicht.
        Assert.Null(queue.LeaseOne("sitzung"));
    }

    /// <summary>
    /// Scheitert das Senden aus dem Dialog, fällt die Zeile in den gewöhnlichen
    /// Wiederholungsweg — und wartet <b>nicht</b> wieder auf einen Dialog, den niemand mehr
    /// öffnet.
    /// </summary>
    [Fact]
    public void Nach_einem_Fehlversuch_wartet_die_Zeile_nicht_erneut_auf_den_Dialog()
    {
        using TempDirectory temp = new();
        ManualTimeProvider uhr = new(Sitzungsende);
        using UploadQueue queue = new(temp.File("state.db"), uhr);

        _ = queue.Enqueue(Sample.Upload("sitzung"), awaitDecision: true);
        _ = queue.LeaseOne("sitzung");

        queue.MarkFailed("sitzung", "Keine Verbindung zum Server.",
                         uhr.GetUtcNow() + TimeSpan.FromSeconds(15));

        QueuedUpload? zurueck = queue.Find("sitzung");
        Assert.NotNull(zurueck);
        Assert.Equal(QueueState.Pending, zurueck!.State);
        Assert.False(zurueck.AwaitingDecision);
        Assert.Equal(1, zurueck.Attempts);

        // Der Sendedienst holt sie sich, sobald der Rueckstau abgelaufen ist. Genau dafuer
        // gibt es die Warteschlange - fuer den Fehlerpfad.
        Assert.Empty(queue.Lease(10));
        uhr.Advance(TimeSpan.FromSeconds(15));
        Assert.Single(queue.Lease(10));
    }

    /// <summary>Ohne Angabe wartet nichts — so verhält sich die Kommandozeile.</summary>
    [Fact]
    public void Ohne_Angabe_ist_der_Eintrag_sofort_faellig()
    {
        using TempDirectory temp = new();
        ManualTimeProvider uhr = new(Sitzungsende);
        using UploadQueue queue = new(temp.File("state.db"), uhr);

        _ = queue.Enqueue(Sample.Upload("kommandozeile"));

        Assert.False(queue.Find("kommandozeile")!.AwaitingDecision);
        Assert.Single(queue.Lease(10));
    }

    /// <summary>
    /// Eine Zeile aus einer älteren Fassung wartet auf nichts.
    /// </summary>
    /// <remarks>
    /// <para>Der Fall der Überführung: In der Datenbank des Technikers stehen Zeilen, die vor
    /// dieser Fassung eingereiht wurden. Für sie geht kein Dialog mehr auf — bekämen sie das
    /// Kennzeichen, lägen sie für immer da, und die Arbeitszeit wäre erfasst, aber nie
    /// gebucht.</para>
    /// <para>Geprüft wird gegen eine Datei im Stand 5, wie sie heute auf dem Arbeitsplatz
    /// liegt: ohne die Spalte <c>awaiting_decision</c>.</para>
    /// </remarks>
    [Fact]
    public void Eine_Zeile_aus_einer_aelteren_Fassung_geht_nach_der_Ueberfuehrung_hinaus()
    {
        using TempDirectory temp = new();
        string pfad = temp.File("state.db");

        // Raw statt UploadQueue: Diesen Bestand legt die Anwendung selbst nicht mehr an.
        Raw.Execute(pfad, """
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
            INSERT INTO queue (remote_maintenance_id, payload, created_at, next_attempt_at)
            VALUES ('alt',
                    '{"typeId":1003,"employeeId":1,"startTime":1,"endTime":2,"remoteMaintenanceId":"alt"}',
                    1, 1);
            PRAGMA user_version = 5;
            """);

        ManualTimeProvider uhr = new(Sitzungsende);
        using UploadQueue queue = new(pfad, uhr);

        QueuedUpload? alt = queue.Find("alt");
        Assert.NotNull(alt);
        Assert.False(alt!.AwaitingDecision);
        Assert.Single(queue.Lease(10));
    }
}
