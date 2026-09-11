using TanssLogWatcher.Storage.Logging;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

public sealed class SessionLogTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ein_Eintrag_haelt_fest_warum_etwas_geschah()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using SessionLog log = new(temp.File("state.db"), clock);

        long id = log.Append(new SessionLogEntry
        {
            RemoteMaintenanceId = "sitzung-1",
            Operation = "upload",
            Outcome = SessionOutcome.Ok,
            Trigger = SessionTrigger.Retry,
            Reason = "Zweiter Versuch nach Zeitüberschreitung; Existenzprüfung war negativ",
            HttpStatus = 201,
            DurationMs = 843,
            TanssSupportId = 38584,
        });

        Assert.True(id > 0);

        SessionLogEntry entry = Assert.Single(log.ForSession("sitzung-1"));

        Assert.Equal(id, entry.Id);
        Assert.Equal(Start, entry.Timestamp);
        Assert.Equal("upload", entry.Operation);
        Assert.Equal(SessionOutcome.Ok, entry.Outcome);
        Assert.Equal(SessionTrigger.Retry, entry.Trigger);
        Assert.Equal(201, entry.HttpStatus);
        Assert.Equal(843, entry.DurationMs);
        Assert.Equal(38584, entry.TanssSupportId);
        Assert.Contains("Existenzprüfung", entry.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_Trockenlauf_liest_sich_nicht_wie_ein_Erfolg()
    {
        using TempDirectory temp = new();
        using SessionLog log = new(temp.File("state.db"));

        _ = log.Append(new SessionLogEntry
        {
            RemoteMaintenanceId = "sitzung-1",
            Operation = "upload",
            Outcome = SessionOutcome.DryRun,
            Trigger = SessionTrigger.Manual,
            Reason = "Trockenlauf eingeschaltet, nichts an TANSS gesendet",
        });

        SessionLogEntry entry = Assert.Single(log.ForSession("sitzung-1"));

        Assert.Equal(SessionOutcome.DryRun, entry.Outcome);
        Assert.NotEqual(SessionOutcome.Ok, entry.Outcome);
        Assert.Equal("dry_run", SessionLog.ToText(entry.Outcome));
    }

    [Fact]
    public void Ein_Token_in_einer_weitergereichten_Meldung_wird_geschwaerzt()
    {
        using TempDirectory temp = new();
        using SessionLog log = new(temp.File("state.db"));

        _ = log.Append(new SessionLogEntry
        {
            RemoteMaintenanceId = "sitzung-1",
            Operation = "upload",
            Outcome = SessionOutcome.Error,
            Trigger = SessionTrigger.Watcher,
            Reason = "403 mit apiToken Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.Sig1234",
            Detail = "Kopfzeile: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.Sig1234",
        });

        SessionLogEntry entry = Assert.Single(log.ForSession("sitzung-1"));

        Assert.DoesNotContain("eyJ", entry.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", entry.Detail!, StringComparison.Ordinal);
        Assert.Contains("403", entry.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Die_Geschichte_einer_Sitzung_steht_in_ihrer_Reihenfolge()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using SessionLog log = new(temp.File("state.db"), clock);

        _ = log.Append(Entry("session.opened", SessionOutcome.Ok, SessionTrigger.Watcher));
        clock.Advance(TimeSpan.FromMinutes(20));
        _ = log.Append(Entry("session.closed", SessionOutcome.Deferred, SessionTrigger.Watcher));
        clock.Advance(TimeSpan.FromMinutes(1));
        _ = log.Append(Entry("upload", SessionOutcome.Ok, SessionTrigger.Retry));

        IReadOnlyList<SessionLogEntry> history = log.ForSession("sitzung-1");

        Assert.Equal(3, history.Count);
        Assert.Equal("session.opened", history[0].Operation);
        Assert.Equal("upload", history[2].Operation);
        Assert.Equal(Start + TimeSpan.FromMinutes(21), history[2].Timestamp);
    }

    [Fact]
    public void Die_juengsten_Eintraege_stehen_vorn()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using SessionLog log = new(temp.File("state.db"), clock);

        _ = log.Append(Entry("alt", SessionOutcome.Ok, SessionTrigger.Startup));
        clock.Advance(TimeSpan.FromHours(1));
        _ = log.Append(Entry("neu", SessionOutcome.Ok, SessionTrigger.Startup));

        IReadOnlyList<SessionLogEntry> recent = log.Recent(1);

        Assert.Equal("neu", Assert.Single(recent).Operation);
    }

    [Fact]
    public void Aeltere_Eintraege_werden_nach_der_Aufbewahrungsfrist_entfernt()
    {
        using TempDirectory temp = new();
        ManualTimeProvider clock = new(Start);
        using SessionLog log = new(temp.File("state.db"), clock);

        _ = log.Append(Entry("alt", SessionOutcome.Ok, SessionTrigger.Watcher));
        clock.Advance(TimeSpan.FromDays(31));
        _ = log.Append(Entry("neu", SessionOutcome.Ok, SessionTrigger.Watcher));

        Assert.Equal(1, log.Prune(TimeSpan.FromDays(30)));

        SessionLogEntry left = Assert.Single(log.Recent(100));
        Assert.Equal("neu", left.Operation);
    }

    [Fact]
    public void Warteschlange_und_Protokoll_teilen_sich_eine_Datenbank()
    {
        using TempDirectory temp = new();
        using StateDatabase database = new(temp.File("state.db"));
        using Queue.UploadQueue queue = new(database);
        using SessionLog log = new(database);

        Assert.True(queue.Enqueue(Sample.Upload("sitzung-1")));
        _ = log.Append(Entry("session.closed", SessionOutcome.Deferred, SessionTrigger.Watcher));

        Assert.Equal(1, queue.Count(Queue.QueueState.Pending));
        Assert.Single(log.ForSession("sitzung-1"));
    }

    private static SessionLogEntry Entry(string operation, SessionOutcome outcome,
                                         SessionTrigger trigger) => new()
    {
        RemoteMaintenanceId = "sitzung-1",
        Operation = operation,
        Outcome = outcome,
        Trigger = trigger,
        Reason = "Prüffall",
    };
}
