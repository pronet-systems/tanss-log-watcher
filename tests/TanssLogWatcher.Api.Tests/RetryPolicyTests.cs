using System.Net;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Api.Tests.Fakes;
using Xunit;

namespace TanssLogWatcher.Api.Tests;

/// <summary>
/// Prüft die Wiederholung — und vor allem, dass sie schreibende Aufrufe in Ruhe lässt.
/// </summary>
public sealed class RetryPolicyTests
{
    [Fact]
    public async Task Fuenfhundert_Wird_Fuenfmal_Versucht()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.InternalServerError, TestEnvironment.Error("BOOM")));
        using TanssClient client = TestEnvironment.Client(handler);

        TanssException error = await Assert.ThrowsAsync<TanssException>(
            () => client.GetAsync<List<Ticket>>(TanssRoutes.OwnTickets));

        Assert.Equal(5, handler.Calls);
        Assert.Equal(500, error.Status);
    }

    [Fact]
    public async Task Netzfehler_Wird_Ebenfalls_Wiederholt()
    {
        FailingHandler handler = new(() => new HttpRequestException("Verbindung abgebrochen."));
        using TanssClient client = TestEnvironment.Client(handler);

        await Assert.ThrowsAsync<TanssUnreachableException>(
            () => client.GetAsync<List<Ticket>>(TanssRoutes.OwnTickets));

        Assert.Equal(5, handler.Calls);
    }

    [Fact]
    public async Task Ein_Spaeterer_Versuch_Darf_Gelingen()
    {
        RecordingHandler handler = new(request => request.Attempt < 3
            ? RecordingHandler.Respond(HttpStatusCode.BadGateway, TestEnvironment.Error("BOOM"))
            : RecordingHandler.Respond(HttpStatusCode.OK, TestEnvironment.Envelope("""[{"id":7}]""")));
        using TanssClient client = TestEnvironment.Client(handler);

        List<Ticket>? tickets = await client.GetAsync<List<Ticket>>(TanssRoutes.OwnTickets);

        Assert.Equal(3, handler.Calls);
        Assert.NotNull(tickets);
        Assert.Equal(7, tickets[0].Id);
    }

    [Fact]
    public async Task Schreibende_Aufrufe_Werden_Niemals_Wiederholt()
    {
        // TANSS dedupliziert nicht: eine Wiederholung erzeugte eine zweite Fernwartung.
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.InternalServerError, TestEnvironment.Error("BOOM")));
        using TanssClient client = TestEnvironment.Client(handler);

        await Assert.ThrowsAsync<TanssException>(
            () => client.PostAsync<RemoteSupportRead>(TanssRoutes.RemoteSupportsCreate,
                                                      new { typeId = 1003 }));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Auch_Eine_Zeitueberschreitung_Beim_Schreiben_Wird_Nicht_Wiederholt()
    {
        FailingHandler handler = new(() => new TaskCanceledException("Zeitgrenze."));
        using TanssClient client = TestEnvironment.Client(handler);

        await Assert.ThrowsAsync<TanssUnreachableException>(
            () => client.PostAsync<RemoteSupportRead>(TanssRoutes.RemoteSupportsCreate,
                                                      new { typeId = 1003 }));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Loeschen_Wird_Nicht_Wiederholt()
    {
        RecordingHandler handler = new(_ => RecordingHandler.Respond(
            HttpStatusCode.ServiceUnavailable, TestEnvironment.Error("BOOM")));
        using TanssClient client = TestEnvironment.Client(handler);

        await Assert.ThrowsAsync<TanssException>(() => client.DeleteAsync(TanssRoutes.Timers));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Vierhundertdrei_Wird_Nicht_Wiederholt()
    {
        // Eine 403 erledigt sich nicht von selbst; Wiederholen kostet nur Zeit.
        RecordingHandler handler = new(_ => RecordingHandler.Empty(HttpStatusCode.Forbidden));
        using TanssClient client = TestEnvironment.Client(handler);

        await Assert.ThrowsAsync<TanssAuthException>(
            () => client.GetAsync<List<Ticket>>(TanssRoutes.OwnTickets));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Wartezeiten_Wachsen_Exponentiell_Und_Bleiben_Unter_Dem_Deckel()
    {
        List<TimeSpan> waited = [];
        RetryPolicy policy = new()
        {
            MaxAttempts = 8,
            Jitter = static () => 1d,
            Sleep = (delay, _) =>
            {
                waited.Add(delay);
                return Task.CompletedTask;
            },
        };

        await Assert.ThrowsAsync<TanssException>(() => policy.ExecuteReadAsync<int>(
            (_, _) => throw new TanssException("kaputt", status: 503)));

        Assert.Equal(7, waited.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), waited[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), waited[1]);
        Assert.Equal(TimeSpan.FromSeconds(4), waited[2]);
        Assert.All(waited, delay => Assert.True(delay <= TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(60), waited[^1]);
    }

    [Fact]
    public void Streuung_Halbiert_Die_Wartezeit_Hoechstens()
    {
        RetryPolicy policy = new() { Jitter = static () => 0d };

        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(1), policy.DelayFor(2));
    }

    [Fact]
    public void Ueberlauf_Bei_Vielen_Versuchen_Gibt_Es_Nicht()
    {
        RetryPolicy policy = new() { Jitter = static () => 1d };

        Assert.Equal(TimeSpan.FromSeconds(60), policy.DelayFor(200));
    }

    [Fact]
    public void Nur_Netzfehler_Und_Fuenfhundert_Gelten_Als_Voruebergehend()
    {
        Assert.True(RetryPolicy.IsTransient(new TanssUnreachableException("weg")));
        Assert.True(RetryPolicy.IsTransient(new TanssException("kaputt", status: 500)));
        Assert.True(RetryPolicy.IsTransient(new TanssException("kaputt", status: 503)));
        Assert.False(RetryPolicy.IsTransient(new TanssException("kaputt", status: 400)));
        Assert.False(RetryPolicy.IsTransient(new TanssAuthException("verboten", 403)));
        Assert.False(RetryPolicy.IsTransient(new TanssNotFoundException("weg", 404)));
        Assert.False(RetryPolicy.IsTransient(new TanssRemoteSupportTypeException("typ", 403)));
        Assert.False(RetryPolicy.IsTransient(new TanssModuleNotLicensedException("lizenz", 403)));
    }

    [Fact]
    public async Task Ohne_Wiederholung_Bleibt_Es_Bei_Einem_Versuch()
    {
        int calls = 0;

        await Assert.ThrowsAsync<TanssException>(() => RetryPolicy.None.ExecuteReadAsync<int>(
            (_, _) =>
            {
                calls++;
                throw new TanssException("kaputt", status: 500);
            }));

        Assert.Equal(1, calls);
    }
}
