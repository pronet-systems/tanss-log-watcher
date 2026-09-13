using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Die Aufbewahrungsfrist auf <c>state.db</c>.
/// </summary>
/// <remarks>
/// Echtes SQLite, echte Dateien, echte Uhr zum Vorstellen. Nachgebildet wird hier gar nichts:
/// Ob eine Zeile nach dreissig Tagen verschwindet, entscheidet das <c>DELETE</c> und der
/// Zeitstempel darin, und beides ist nur an der Datenbank selbst zu prüfen.
/// </remarks>
public sealed class StatePrunerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    [Fact]
    public void Nach_der_Frist_gehen_erledigter_Eintrag_und_Protokollzeile()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using StateDatabase database = new(temp.File("state.db"));
        using UploadQueue queue = new(database, clock);
        using SessionLog log = new(database, clock);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));
        queue.MarkDone("sitzung-1");

        _ = log.Append(new SessionLogEntry
        {
            RemoteMaintenanceId = "sitzung-1",
            Operation = "queue.flush",
            Outcome = SessionOutcome.Ok,
            Trigger = SessionTrigger.Retry,
            Reason = "Hochgeladen.",
        });

        clock.Advance(TimeSpan.FromDays(40));

        PruneResult result = new StatePruner(queue, log).Run(Retention);

        Assert.Equal(1, result.QueueEntries);
        Assert.Equal(1, result.LogEntries);
        Assert.Equal(0, result.FailedSteps);
        Assert.True(result.DidAnything);

        // Die Nutzlast traegt die Gegenstelle im Klartext - sie ist der eigentliche Grund,
        // warum der erledigte Eintrag nicht ewig liegen bleiben darf.
        Assert.Null(queue.Find("sitzung-1"));
        Assert.Empty(log.Recent());
    }

    [Fact]
    public void Vor_der_Frist_bleibt_alles_stehen()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using StateDatabase database = new(temp.File("state.db"));
        using UploadQueue queue = new(database, clock);
        using SessionLog log = new(database, clock);

        _ = queue.Enqueue(Sample.Upload("sitzung-1"));
        queue.MarkDone("sitzung-1");

        _ = log.Append(new SessionLogEntry
        {
            Operation = "queue.flush",
            Outcome = SessionOutcome.Ok,
            Trigger = SessionTrigger.Retry,
            Reason = "Hochgeladen.",
        });

        clock.Advance(TimeSpan.FromDays(29));

        PruneResult result = new StatePruner(queue, log).Run(Retention);

        Assert.False(result.DidAnything);
        Assert.Equal("Nichts fällig.", result.Summary);
        Assert.NotNull(queue.Find("sitzung-1"));
        Assert.Single(log.Recent());
    }

    /// <summary>
    /// Der Fall, der wehtäte: eine seit Wochen wartende Sitzung.
    /// </summary>
    /// <remarks>
    /// Steht TANSS lange still, wird ein Eintrag beliebig alt, ohne je erledigt zu sein. Ihn
    /// mit der Frist fortzuräumen hiesse, genau die Arbeitszeit zu löschen, für deren
    /// Aufbewahrung die Warteschlange da ist.
    /// </remarks>
    [Fact]
    public void Ein_wartender_Eintrag_ueberlebt_jede_Frist()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using StateDatabase database = new(temp.File("state.db"));
        using UploadQueue queue = new(database, clock);
        using SessionLog log = new(database, clock);

        _ = queue.Enqueue(Sample.Upload("wartet-seit-langem"));
        _ = queue.Enqueue(Sample.Upload("aufgegeben"));
        queue.MarkFailed("aufgegeben", "Der Fernwartungstyp gibt es in TANSS nicht.",
                         nextAttempt: null);

        clock.Advance(TimeSpan.FromDays(400));

        PruneResult result = new StatePruner(queue, log).Run(Retention);

        Assert.Equal(0, result.QueueEntries);
        Assert.Equal(1, queue.Count(QueueState.Pending));
        Assert.Equal(1, queue.Count(QueueState.Failed));
        Assert.NotNull(queue.Find("wartet-seit-langem"));
        Assert.NotNull(queue.Find("aufgegeben"));
    }

    /// <summary>
    /// Hausregel 5 zwischen den beiden Schritten.
    /// </summary>
    /// <remarks>
    /// Der Fehlschlag ist hier kein Erfundener: Eine geschlossene Warteschlange ist genau das,
    /// was beim geordneten Beenden entsteht, während anderes noch läuft. Sie wirft
    /// <see cref="ObjectDisposedException"/> — und das Protokoll auf der zweiten Datenbank
    /// hat damit nichts zu tun und wird trotzdem aufgeräumt.
    /// </remarks>
    [Fact]
    public void Der_Fehlschlag_des_einen_Schritts_kostet_den_anderen_nicht()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using SessionLog log = new(temp.File("log.db"), clock);
        UploadQueue queue = new(temp.File("queue.db"), clock);

        _ = log.Append(new SessionLogEntry
        {
            Operation = "queue.flush",
            Outcome = SessionOutcome.Ok,
            Trigger = SessionTrigger.Retry,
            Reason = "Hochgeladen.",
        });

        clock.Advance(TimeSpan.FromDays(40));
        queue.Dispose();

        PruneResult result = new StatePruner(queue, log).Run(Retention);

        Assert.Equal(1, result.FailedSteps);
        Assert.Equal(0, result.QueueEntries);
        Assert.Equal(1, result.LogEntries);
        Assert.Empty(log.Recent());
        Assert.Contains("misslang", result.Summary, StringComparison.Ordinal);
    }
}
