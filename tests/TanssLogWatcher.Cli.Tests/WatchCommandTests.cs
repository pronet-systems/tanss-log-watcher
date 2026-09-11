using TanssLogWatcher.Cli.Commands;
using TanssLogWatcher.Monitoring;
using TanssLogWatcher.Monitoring.Model;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Queue;
using Xunit;

namespace TanssLogWatcher.Cli.Tests;

/// <summary>
/// Die Beobachtung.
/// </summary>
/// <remarks>
/// Geprüft wird je Takt und nicht über die Uhr: Ein Takt ist die Einheit, in der dieser Befehl
/// arbeitet, und ein Test, der dafür sekundenlang warten müsste, würde nicht geschrieben — und
/// damit bliebe die Zusage ungeprüft, dass eine beendete Sitzung wirklich in der Warteschlange
/// landet.
/// </remarks>
public sealed class WatchCommandTests
{
    [Fact]
    public void Eine_beendete_Sitzung_geht_in_die_Warteschlange()
    {
        MutableWindowSource windows = new();
        FakeUploadQueue queue = new();
        WatchInputs inputs = Inputs(windows, queue, typeId: 1002);

        windows.Windows.Add(new WindowInfo("srv01 - Remotedesktopverbindung", 4711, 42));
        using StringWriter writer = new();

        WatchCycleResult first = WatchCommand.RunCycle(inputs, dryRun: false, writer);
        Assert.Equal(1, first.Started);

        // Das Fenster verschwindet: Damit endet die Sitzung.
        windows.Windows.Clear();
        WatchCycleResult second = WatchCommand.RunCycle(inputs, dryRun: false, writer);

        Assert.Equal(1, second.Enqueued);
        Assert.Equal(1, queue.Count(QueueState.Pending));

        QueuedUpload entry = queue.List(QueueState.Pending).Items[0];
        Assert.Equal(1002, entry.Payload.TypeId);
        Assert.Equal(42, entry.Payload.EmployeeId);
        Assert.Contains("srv01", entry.Payload.Comment, StringComparison.Ordinal);
    }

    [Fact]
    public void Im_Trockenlauf_wird_nichts_eingereiht()
    {
        MutableWindowSource windows = new();
        FakeUploadQueue queue = new();
        WatchInputs inputs = Inputs(windows, queue, typeId: 1002);

        windows.Windows.Add(new WindowInfo("srv01 - Remotedesktopverbindung", 4711, 42));
        using StringWriter writer = new();

        _ = WatchCommand.RunCycle(inputs, dryRun: true, writer);
        windows.Windows.Clear();
        WatchCycleResult second = WatchCommand.RunCycle(inputs, dryRun: true, writer);

        Assert.Equal(0, second.Enqueued);
        Assert.Equal(0, queue.Count(QueueState.Pending));
        Assert.Empty(queue.LoadOpenSessions());
        Assert.Contains("Trockenlauf", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Ohne_Zuordnung_wird_nichts_gebucht()
    {
        // Ein Werkzeug, das ungefragt Fenstertitel von Kundenrechnern in ein Ticketsystem
        // schreibt, waere schon beim ersten Start ein Datenschutzvorfall.
        MutableWindowSource windows = new();
        FakeUploadQueue queue = new();
        WatchInputs inputs = Inputs(windows, queue, MonitoringSetting.NotMonitored);

        windows.Windows.Add(new WindowInfo("srv01 - Remotedesktopverbindung", 4711, 42));
        using StringWriter writer = new();

        _ = WatchCommand.RunCycle(inputs, dryRun: false, writer);
        windows.Windows.Clear();
        WatchCycleResult second = WatchCommand.RunCycle(inputs, dryRun: false, writer);

        Assert.Equal(0, second.Enqueued);
        Assert.Equal(0, queue.Count(QueueState.Pending));
    }

    [Fact]
    public async Task Ohne_Zuordnung_ist_der_Trockenlauf_der_Standard()
    {
        MutableWindowSource windows = new();
        FakeUploadQueue queue = new();
        WatchInputs inputs = Inputs(windows, queue, MonitoringSetting.NotMonitored);

        // Vorab abgebrochen: Geprueft wird die Betriebsart, nicht die Schleife.
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await using StringWriter writer = new();
        int code = await WatchCommand.RunAsync(inputs, dryRunRequested: false, writer,
                                               cancelled.Token);

        Assert.Equal(ExitCode.Healthy, code);
        Assert.Contains("TROCKENLAUF", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("remote_support_type_id", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Beim_Beenden_werden_laufende_Sitzungen_gesichert_statt_verworfen()
    {
        MutableWindowSource windows = new();
        FakeUploadQueue queue = new();
        WatchInputs inputs = Inputs(windows, queue, typeId: 1002);

        windows.Windows.Add(new WindowInfo("srv01 - Remotedesktopverbindung", 4711, 42));

        await using StringWriter writer = new();
        _ = WatchCommand.RunCycle(inputs, dryRun: false, writer);

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        _ = await WatchCommand.RunAsync(inputs, dryRunRequested: false, writer, cancelled.Token);

        // Die laufende Sitzung ist beim Beenden abgeschlossen und eingereiht worden - der
        // Mangel der Vorlage, der bares Geld kostete.
        Assert.Equal(1, queue.Count(QueueState.Pending));
        Assert.Empty(queue.LoadOpenSessions());
    }

    private static WatchInputs Inputs(IWindowSource windows, IUploadQueue queue, int typeId)
    {
        MonitoringSetting setting = new()
        {
            Key = "mstsc",
            RemoteSupportTypeId = typeId,
        };

        FakeProcessSource processes = new()
        {
            Processes = [new ProcessInfo(4711, 900, "mstsc", DateTimeOffset.UnixEpoch)],
        };

        DestinationResolver resolver = new(new FakeTcpConnectionSource(),
                                           new FakeHostNameResolver());

        return new WatchInputs
        {
            Config = new AppConfig
            {
                Tanss = new TanssSection
                {
                    BaseUrl = "https://tanss.example.de/backend",
                    EmployeeId = 42,
                },
                Monitoring = [new MonitoringEntry { Key = "mstsc", RemoteSupportTypeId = 1002 }],
            },
            Engine = new SessionEngine(windows, processes, resolver),
            Queue = queue,
            Settings = [setting],
        };
    }
}
