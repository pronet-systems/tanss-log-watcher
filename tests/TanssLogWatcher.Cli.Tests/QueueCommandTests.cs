using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Cli.Commands;
using TanssLogWatcher.Storage.Queue;
using Xunit;

namespace TanssLogWatcher.Cli.Tests;

/// <summary>
/// Der Sendelauf der Warteschlange.
/// </summary>
/// <remarks>
/// <b>TANSS dedupliziert nicht.</b> Ein zweiter POST mit derselben <c>remoteMaintenanceId</c>
/// erzeugt nachweislich einen zweiten Datensatz (11.09.2026, IDs 38584 und 38585). Die Zusage
/// dieses Befehls — vor jeder Wiederholung erst fragen, und bei ungeklärter Antwort gar nicht
/// senden — ist deshalb die wichtigste im ganzen Werkzeug.
/// </remarks>
public sealed class QueueCommandTests
{
    [Fact]
    public async Task Bei_ungeklaertem_Ausgang_wird_erst_gefragt_und_dann_nicht_gesendet()
    {
        FakeUploadQueue queue = new();
        queue.Seed(Unknown("38584"));

        FakeRemoteSupportRepository remote = new() { Exists = true };

        (int code, string output) = await FlushAsync(queue, remote);

        Assert.Equal(1, remote.ExistsCalls);
        Assert.Equal(0, remote.CreateCalls);
        Assert.Equal(QueueState.Done, queue.Peek("38584")!.State);
        Assert.Contains("steht bereits in TANSS", output, StringComparison.Ordinal);
        Assert.Equal(ExitCode.Healthy, code);
    }

    [Fact]
    public async Task Bei_ungeklaertem_Ausgang_und_fehlendem_Datensatz_wird_gesendet()
    {
        FakeUploadQueue queue = new();
        queue.Seed(Unknown("38585"));

        FakeRemoteSupportRepository remote = new() { Exists = false };

        (int code, _) = await FlushAsync(queue, remote);

        Assert.Equal(1, remote.ExistsCalls);
        Assert.Equal(1, remote.CreateCalls);
        Assert.Equal(QueueState.Done, queue.Peek("38585")!.State);
        Assert.Equal(ExitCode.Healthy, code);
    }

    [Fact]
    public async Task Fehlgeschlagene_Existenzpruefung_heisst_unbekannt_und_nicht_nicht_vorhanden()
    {
        // Der teure Irrtum: Wirft die Pruefung, ist gerade NICHT geklaert, ob der Datensatz
        // da ist. Zu senden waere genau die Dublette, gegen die die Pruefung aufgestellt wurde.
        FakeUploadQueue queue = new();
        queue.Seed(Unknown("38586"));

        FakeRemoteSupportRepository remote = new()
        {
            ExistsFails = new TanssException("Unerwartete Antwortform."),
        };

        (int code, string output) = await FlushAsync(queue, remote);

        Assert.Equal(0, remote.CreateCalls);
        Assert.Equal(QueueState.Pending, queue.Peek("38586")!.State);
        Assert.True(queue.Peek("38586")!.OutcomeUnknown);
        Assert.Contains("zurückgestellt", output, StringComparison.Ordinal);
        Assert.Equal(ExitCode.Warning, code);
    }

    [Fact]
    public async Task Ein_erster_Versand_fragt_nicht_erst_nach()
    {
        // Ein Eintrag, der nie draussen war, kann nicht angekommen sein. Die Abfrage waere ein
        // Aufruf ohne Erkenntnis.
        FakeUploadQueue queue = new();
        _ = queue.Enqueue(Payloads.For("neu-1"));

        FakeRemoteSupportRepository remote = new();

        (int code, _) = await FlushAsync(queue, remote);

        Assert.Equal(0, remote.ExistsCalls);
        Assert.Equal(1, remote.CreateCalls);
        Assert.Equal(ExitCode.Healthy, code);
    }

    [Fact]
    public async Task Ein_zweiter_Versuch_fragt_auch_ohne_Kennzeichen_nach()
    {
        // Die Warteschlange kennzeichnet nur den Absturz zwischen Zuteilung und Rueckmeldung.
        // Eine Zeitueberschreitung MITTEN im POST sieht sie nicht - sie laesst aber genauso
        // offen, ob TANSS angenommen hat.
        FakeUploadQueue queue = new();
        queue.Seed(Unknown("38587") with { OutcomeUnknown = false, Attempts = 1 });

        FakeRemoteSupportRepository remote = new() { Exists = true };

        _ = await FlushAsync(queue, remote);

        Assert.Equal(1, remote.ExistsCalls);
        Assert.Equal(0, remote.CreateCalls);
    }

    [Fact]
    public async Task Ein_nicht_heilbarer_Fehler_wird_aufgegeben_statt_ewig_wiederholt()
    {
        FakeUploadQueue queue = new();
        _ = queue.Enqueue(Payloads.For("typ-fehlt"));

        FakeRemoteSupportRepository remote = new()
        {
            CreateFails = new TanssRemoteSupportTypeException("TYPE_DOESNT_EXIST", 403),
        };

        (int code, string output) = await FlushAsync(queue, remote);

        Assert.Equal(QueueState.Failed, queue.Peek("typ-fehlt")!.State);
        Assert.Contains("aufgegeben", output, StringComparison.Ordinal);
        Assert.Equal(ExitCode.Warning, code);
    }

    [Fact]
    public async Task Ein_voruebergehender_Fehler_wird_zurueckgestellt()
    {
        FakeUploadQueue queue = new();
        _ = queue.Enqueue(Payloads.For("kein-netz"));

        FakeRemoteSupportRepository remote = new()
        {
            CreateFails = new TanssUnreachableException("Kein Netz."),
        };

        (int code, _) = await FlushAsync(queue, remote);

        Assert.Equal(QueueState.Pending, queue.Peek("kein-netz")!.State);
        Assert.Equal(ExitCode.Warning, code);
    }

    [Fact]
    public async Task Ein_kaputter_Eintrag_bricht_den_Durchlauf_nicht_ab()
    {
        // Hausregel 5. Ohne diesen Riegel hielte ein einziger kaputter Eintrag jede weitere
        // Sitzung fest - also genau das, was die Warteschlange verhindern soll.
        FakeUploadQueue queue = new();
        _ = queue.Enqueue(Payloads.For("erster"));
        _ = queue.Enqueue(Payloads.For("zweiter"));

        ThrowingOnceRepository remote = new();

        _ = await FlushAsync(queue, remote);

        Assert.Equal(2, remote.Attempted);
        Assert.Equal(QueueState.Done, queue.Peek("zweiter")!.State);
    }

    [Fact]
    public async Task Unlesbare_Zeilen_werden_in_der_Anzeige_erklaert()
    {
        FakeUploadQueue queue = new() { Unreadable = 2 };

        await using StringWriter writer = new();
        int code = await QueueCommand.RunAsync(new QueueInputs
        {
            Queue = queue,
            RemoteSupports = new FakeRemoteSupportRepository(),
        }, flush: false, writer);

        Assert.Contains("unlesbar", writer.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCode.Warning, code);
    }

    /// <summary>Ein Eintrag, dessen Versand ohne Rückmeldung abgebrochen ist.</summary>
    private static QueuedUpload Unknown(string id) => new()
    {
        RemoteMaintenanceId = id,
        Payload = Payloads.For(id),
        State = QueueState.Pending,
        Attempts = 1,
        OutcomeUnknown = true,
        CreatedAt = DateTimeOffset.UnixEpoch,
        NextAttemptAt = DateTimeOffset.UnixEpoch,
    };

    private static async Task<(int Code, string Output)> FlushAsync(
        IUploadQueue queue, IRemoteSupportRepository remote)
    {
        await using StringWriter writer = new();
        int code = await QueueCommand.RunAsync(new QueueInputs
        {
            Queue = queue,
            RemoteSupports = remote,
        }, flush: true, writer);

        return (code, writer.ToString());
    }

    /// <summary>Scheitert beim ersten Eintrag mit etwas Unerwartetem, danach gelingt alles.</summary>
    private sealed class ThrowingOnceRepository : IRemoteSupportRepository
    {
        public int Attempted { get; private set; }

        public Task<RemoteSupportRead> CreateAsync(RemoteSupportWrite item,
                                                   CancellationToken ct = default)
        {
            Attempted++;
            return Attempted == 1
                ? throw new InvalidOperationException("Etwas ganz Unerwartetes.")
                : Task.FromResult(new RemoteSupportRead { Id = 1, RemoteMaintenanceId = item.RemoteMaintenanceId });
        }

        public Task<bool> ExistsAsync(string remoteMaintenanceId, DateTimeOffset around,
                                      CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> ExistsAsync(string remoteMaintenanceId, DateTimeOffset sessionStart,
                                      DateTimeOffset sessionEnd, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> ExistsAsync(RemoteSupportWrite session, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<RemoteSupportRead>> ListAsync(Timeframe timeframe,
                                                                string? text = null,
                                                                CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RemoteSupportRead>>([]);

        public Task<IReadOnlyList<RemoteSupportSystem>> ListSystemsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RemoteSupportSystem>>([]);
    }
}
