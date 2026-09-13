using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.Services;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Läuft die Aufbewahrungsfrist im Betrieb — und nicht nur, wenn ein Test sie selbst anstösst?
/// </summary>
/// <remarks>
/// <para><b>Das ist die Frage, an der es vorher scheiterte.</b> Beide Löschschritte waren
/// gebaut und geprüft; aufgerufen hat sie im Produktionscode niemand. Ein Test auf
/// <see cref="TanssLogWatcher.Storage.StatePruner"/> allein hätte das nie bemerkt — er ruft ja
/// selbst auf. Deshalb läuft hier der echte Dienst gegen eine echte Datenbank, und niemand
/// stösst etwas an ausser <c>StartAsync</c>.</para>
///
/// <para>Der Sendedienst spricht dabei mit niemandem: Die Warteschlange enthält nur einen
/// erledigten Eintrag, <see cref="IUploadQueue.Lease"/> gibt nichts heraus, und der
/// Betriebszustand ist nicht gestört — damit entfällt auch der Griff nach der Technikerliste.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UploadServiceRetentionTests
{
    [Fact]
    public async Task Der_Sendedienst_raeumt_ab_was_seine_Frist_ueberschritten_hat()
    {
        using TempDirectory temp = new();
        string state = temp.File("state.db");

        // Erst anlegen, dann zurueckdatieren, dann den Dienst darauf loslassen. Zurueckdatiert
        // wird an der Anwendung vorbei, weil sie ihre Zeitstempel selbst setzt - dreissig Tage
        // zu warten ist keine Pruefmethode.
        using (RuntimeComposition setup = new(Sample.Config(), NullLoggerFactory.Instance, state))
        {
            _ = setup.Queue.Enqueue(Sample.Upload("sitzung-alt"));
            setup.Queue.MarkDone("sitzung-alt");

            _ = setup.Log.Append(new SessionLogEntry
            {
                RemoteMaintenanceId = "sitzung-alt",
                Operation = "queue.flush",
                Outcome = SessionOutcome.Ok,
                Trigger = SessionTrigger.Retry,
                Reason = "Hochgeladen.",
            });
        }

        long old = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeSeconds();
        Backdate(state, old);

        using RuntimeComposition composition =
            new(Sample.Config(), NullLoggerFactory.Instance, state);

        TestRuntimeContext context = new(composition);
        using UploadService service = new(context);

        await service.StartAsync();
        await WaitForCycle(service);
        await service.StopAsync();

        Assert.Null(composition.Queue.Find("sitzung-alt"));

        // Uebrig bleibt genau der Vermerk ueber das Aufraeumen selbst - er ist gerade eben
        // entstanden und faellt damit nicht unter seine eigene Frist.
        SessionLogEntry entry = Assert.Single(composition.Log.Recent());
        Assert.Equal("state.prune", entry.Operation);
        Assert.Contains("Warteschlangeneinträge", entry.Reason, StringComparison.Ordinal);
    }

    /// <summary>Wartet auf den ersten abgeschlossenen Takt.</summary>
    /// <remarks>
    /// Der Takt läuft auf einem eigenen Strang; ohne dieses Warten stünde die Behauptung im
    /// Test, bevor der Dienst überhaupt gearbeitet hat. Die Schranke ist grosszügig und die
    /// Meldung sagt, was fehlte — ein Test, der ohne Begründung nach zehn Sekunden aufgibt,
    /// ist schlimmer als keiner.
    /// </remarks>
    private static async Task WaitForCycle(UploadService service)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < until)
        {
            if (service.Activity.Cycles >= 1)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Der Sendedienst hat in dreissig Sekunden keinen Takt abgeschlossen. "
            + "Stand: " + service.Activity.Message);
    }

    /// <summary>
    /// Datiert alles in der Zustandsdatenbank zurück.
    /// </summary>
    /// <remarks>
    /// An der Anwendung vorbei und ausdrücklich auf der geschlossenen Datei: Die Anwendung
    /// setzt ihre Zeitstempel selbst, und eine Uhr zum Vorstellen reicht sie nicht bis in die
    /// Warteschlange durch.
    /// </remarks>
    private static void Backdate(string path, long moment)
    {
        using SqliteConnection connection =
            new(new SqliteConnectionStringBuilder { DataSource = path }.ToString());

        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE queue SET created_at = $moment, completed_at = $moment;
            UPDATE session_log SET ts = $moment;
            """;
        _ = command.Parameters.AddWithValue("$moment", moment);
        _ = command.ExecuteNonQuery();
    }
}

/// <summary>
/// Die Laufzeit, wie ein Hintergrunddienst sie sieht.
/// </summary>
/// <remarks>
/// Nachgebildet ist hier allein die <b>Umgebung</b> — Zusammenbau, Zustand, Uhr. Alles, worum
/// es im Test geht, ist echt: echte Datenbank, echte Warteschlange, echtes Protokoll, echter
/// Dienst.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class TestRuntimeContext(RuntimeComposition composition) : IRuntimeContext
{
    public RuntimeComposition? Composition { get; } = composition;

    public AppStatus Status { get; private set; } = new()
    {
        State = RuntimeState.Working,
        Headline = "Läuft.",
        Reason = "Für den Test angenommen.",
        Advice = "Nichts zu tun.",
    };

    public RuntimeNotifier Notifier { get; } = new(null);

    public TimeProvider Clock { get; } = TimeProvider.System;

    public void ReportDegraded(DegradedCause cause, string reason, string advice) =>
        Status = new AppStatus
        {
            State = RuntimeState.Degraded,
            Headline = "Gestört.",
            Reason = reason,
            Advice = advice,
            Cause = cause,
        };

    public void ReportWorking() => Status = new AppStatus
    {
        State = RuntimeState.Working,
        Headline = "Läuft.",
        Reason = "Ein echter Aufruf ist geglückt.",
        Advice = "Nichts zu tun.",
    };
}
