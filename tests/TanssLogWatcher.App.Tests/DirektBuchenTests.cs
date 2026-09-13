using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.Services;
using TanssLogWatcher.App.ViewModels;
using TanssLogWatcher.Monitoring.Model;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Die Warteschlange ist der Fehlerpfad — und nur der.
/// </summary>
/// <remarks>
/// <para><b>Die Anweisung, die diese Datei prüft, lautet wörtlich:</b> „wir schreiben nur in die
/// warteschlange, wenn aus irgendwelchen gründen die buchung fehlgeschlagen ist. ansonsten wird
/// direkt gebucht und der dialog fragt nach den informationen.“ Vorher entstand bei <i>jeder</i>
/// beendeten Sitzung sofort eine Zeile; die Warteschlange zeigte damit den Normalbetrieb statt
/// der Störungen, und „ausstehend: 7“ hiess nicht, dass etwas hing.</para>
///
/// <para><b>Was das kostet, steht ausdrücklich mit im Prüfstand:</b> Wer den Dialog schliesst,
/// ohne zu antworten, bekommt seine Sitzung eingereiht — sie darf nicht verschwinden, und sie
/// darf auch nicht ungefragt hinausgehen. Beides ist hier ein eigener Fall.</para>
///
/// <para>Echt ist alles bis auf zwei Orte: Konfiguration und Zustandsdatenbank liegen in einem
/// Wegwerfordner statt im Benutzerprofil, und gesendet wird gegen eine Attrappe statt gegen
/// TANSS — ein Test, der bucht, bucht sonst wirklich.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DirektBuchenTests
{
    // --- Das Sitzungsende ------------------------------------------------------

    /// <summary>
    /// Beim Sitzungsende steht <b>nichts</b> in der Warteschlange.
    /// </summary>
    /// <remarks>
    /// Der Kern des Umbaus. Vorher reihte der Beobachter jede beendete Sitzung ein und meldete
    /// <c>Enqueued</c>; der Dialog ergänzte danach die Zeile. Jetzt hält der Dialog die Sitzung,
    /// und die Meldung sagt das auch — ohne diese Meldung ginge das Fenster gar nicht erst auf.
    /// </remarks>
    [Fact]
    public void Beim_Sitzungsende_entsteht_keine_Warteschlangenzeile()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out RuntimeComposition composition);

        SessionWatcherService beobachtung = new(host);

        List<SessionClosed> gemeldet = [];
        beobachtung.SessionEnded += (_, closed) => gemeldet.Add(closed);

        WatchedSession sitzung = Beendet();

        bool eingereiht = beobachtung.Finish(composition, sitzung, DateTimeOffset.Now,
                                             SessionTrigger.Watcher);

        Assert.False(eingereiht, "Das Sitzungsende hat eingereiht.");
        Assert.Null(composition.Queue.Find(sitzung.SessionId.ToString()));
        Assert.Equal(0, composition.Queue.Count(QueueState.Pending));

        // Und der Dialog geht trotzdem auf: Daran haengt die ganze Buchung.
        SessionClosed closed = Assert.Single(gemeldet);
        Assert.Equal(SessionDisposition.Held, closed.Disposition);
        Assert.True(SaveSessionViewModel.CanAnswer(closed.Disposition),
                    "Der Abschlussdialog ginge zu dieser Meldung nicht auf.");
    }

    /// <summary>
    /// Beim geordneten Beenden wird eingereiht — wartend, nicht sendebereit.
    /// </summary>
    /// <remarks>
    /// Die Antwort auf „was geschieht mit einer Sitzung, die niemand mehr beantworten kann?“.
    /// Ein Dialog hätte jetzt kein Fenster mehr; verloren gehen darf sie trotzdem nicht, und
    /// ungefragt hinausgehen erst recht nicht. Also: eingereiht, aber auf Entscheidung wartend.
    /// </remarks>
    [Fact]
    public void Beim_geordneten_Beenden_wird_wartend_eingereiht()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out RuntimeComposition composition);

        SessionWatcherService beobachtung = new(host);
        WatchedSession sitzung = Beendet();

        Assert.True(beobachtung.Finish(composition, sitzung, DateTimeOffset.Now,
                                       SessionTrigger.Shutdown));

        QueuedUpload? zeile = composition.Queue.Find(sitzung.SessionId.ToString());

        Assert.NotNull(zeile);
        Assert.True(zeile!.AwaitingDecision, "Sie ginge beim nächsten Takt ungefragt hinaus.");
        Assert.Empty(composition.Queue.Lease(10));
    }

    // --- Buchen ohne Zeile -----------------------------------------------------

    /// <summary>
    /// „In TANSS buchen“ sendet direkt — und hinterlässt keine Warteschlangenzeile.
    /// </summary>
    /// <remarks>
    /// Gelingt die Buchung, hat die Sitzung die Warteschlange nie gesehen. Geprüft wird
    /// ausserdem, dass der Vermerk der laufenden Sitzung fällt: Bliebe er stehen, legte der
    /// nächste Start dieselbe, längst gebuchte Sitzung erneut vor.
    /// </remarks>
    [Fact]
    public async Task Buchen_ohne_Zeile_sendet_direkt_und_laesst_die_Warteschlange_leer()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out RuntimeComposition composition);

        SessionSnapshot sitzung = Sample.Session(window: 0) with { EndedAt = DateTimeOffset.Now };
        MerkeLaufend(composition, sitzung);

        FakeFernwartungen tanss = new(38614);
        using SaveSessionViewModel model = Dialog(host, sitzung, tanss);

        model.Comment = "Drucker neu eingerichtet.";
        await model.SaveCommand.ExecuteAsync(null);

        Assert.True(model.IsBooked);
        Assert.Contains("38614", model.Result, StringComparison.Ordinal);

        RemoteSupportWrite gesendet = Assert.Single(tanss.Gesendet);
        Assert.Equal(sitzung.SessionId, gesendet.RemoteMaintenanceId);
        Assert.Equal("Drucker neu eingerichtet.", gesendet.Comment);
        Assert.Equal(1003, gesendet.TypeId);

        // Der Kern: nichts in der Warteschlange - weder wartend noch abgehakt.
        Assert.Null(composition.Queue.Find(sitzung.SessionId));
        Assert.Empty(composition.Queue.LoadOpenSessions());

        SessionLogEntry gebucht = Assert.Single(
            composition.Log.ForSession(sitzung.SessionId),
            entry => entry.Operation == "dialog.booked");

        Assert.Equal(38614, gebucht.TanssSupportId);
    }

    /// <summary>
    /// Scheitert das Buchen an der Leitung, steht die Sitzung danach in der Warteschlange.
    /// </summary>
    /// <remarks>
    /// <para>Der einzige Weg, auf dem im Regelfall etwas dort hineinkommt — und der Bericht
    /// geht mit: Er ist getippt worden, und ein zweiter Versuch ohne ihn wäre der Verlust, den
    /// dieses Fenster verhindern soll.</para>
    /// <para>Dass die Zeile sofort einen Fehlversuch trägt, ist kein Beiwerk: Daran macht der
    /// Sendedienst fest, ob er vor der Wiederholung nachfragen muss, ob die Fernwartung nicht
    /// doch schon in TANSS steht. TANSS dedupliziert nicht.</para>
    /// </remarks>
    [Fact]
    public async Task Ein_gescheitertes_Buchen_reiht_die_Sitzung_ein()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out RuntimeComposition composition);

        SessionSnapshot sitzung = Sample.Session(window: 0) with { EndedAt = DateTimeOffset.Now };
        MerkeLaufend(composition, sitzung);

        FakeFernwartungen tanss = new(new TanssUnreachableException("Keine Verbindung."));
        using SaveSessionViewModel model = Dialog(host, sitzung, tanss);

        model.Comment = "Serverdienst neu gestartet.";
        await model.SaveCommand.ExecuteAsync(null);

        Assert.False(model.IsBooked);
        Assert.Contains("Warteschlange", model.Result, StringComparison.Ordinal);

        QueuedUpload? zeile = composition.Queue.Find(sitzung.SessionId);

        Assert.NotNull(zeile);
        Assert.Equal(QueueState.Pending, zeile!.State);
        Assert.Equal("Serverdienst neu gestartet.", zeile.Payload.Comment);

        // Sie wartet auf den Sendedienst und nicht auf eine Entscheidung - die ist gefallen.
        Assert.False(zeile.AwaitingDecision);

        // Der Fehlversuch steht: Ohne ihn ginge die Zeile beim naechsten Takt OHNE
        // Existenzpruefung hinaus - und der abgebrochene POST koennte angekommen sein.
        Assert.True(zeile.Attempts > 0, "Der Sendedienst würde ungeprüft wiederholen.");

        // Ein zweiter Versuch ist erlaubt, und der Vermerk der laufenden Sitzung ist fort:
        // gesichert ist sie jetzt in der Warteschlange.
        Assert.True(model.CanEdit);
        Assert.Empty(composition.Queue.LoadOpenSessions());
    }

    /// <summary>
    /// Was nicht heilt, wird trotzdem aufbewahrt — und der Dialog sagt, dass Warten nichts nützt.
    /// </summary>
    /// <remarks>
    /// Ein Fernwartungstyp, den es in TANSS nicht gibt, wiederholt sich nicht von selbst gesund.
    /// Wegwerfen wäre trotzdem falsch: Die Sitzung ist Arbeitszeit. Sie bleibt mit ihrem Grund
    /// stehen und ist von Hand zu klären.
    /// </remarks>
    [Fact]
    public async Task Ein_Fehler_der_nicht_heilt_bleibt_mit_Grund_in_der_Warteschlange()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out RuntimeComposition composition);

        SessionSnapshot sitzung = Sample.Session(window: 0) with { EndedAt = DateTimeOffset.Now };

        FakeFernwartungen tanss = new(
            new TanssRemoteSupportTypeException("Diesen Fernwartungstyp gibt es nicht."));

        using SaveSessionViewModel model = Dialog(host, sitzung, tanss);

        await model.SaveCommand.ExecuteAsync(null);

        Assert.False(model.IsBooked);
        Assert.Contains("heilt nicht", model.Result, StringComparison.Ordinal);

        QueuedUpload? zeile = composition.Queue.Find(sitzung.SessionId);

        Assert.NotNull(zeile);
        Assert.Equal(QueueState.Failed, zeile!.State);
        Assert.Empty(composition.Queue.Lease(10));

        Assert.Contains(composition.Log.ForSession(sitzung.SessionId),
                        entry => entry.Operation == "dialog.failed");
    }

    // --- Die beiden anderen Ausgänge ------------------------------------------

    /// <summary>
    /// „Später“ reiht ein und <b>parkt</b> — ohne Bericht, ohne Ticket, ohne Versand.
    /// </summary>
    /// <remarks>
    /// <para>Der getippte Text geht ausdrücklich nicht mit: Parken heisst „noch nicht
    /// entschieden“, und die Zeile trägt die automatische Beschreibung. Nachtragen lässt er
    /// sich über „Ändern“ in der Warteschlange.</para>
    /// <para><b>Und sie geht nicht hinaus.</b> Vorher war das anders — die Zeile wurde
    /// freigegeben und im nächsten Takt gesendet, gemessen eine Sekunde nach dem Klick. Der
    /// leere Sendelauf am Ende ist deshalb der wichtigste Teil dieses Falles.</para>
    /// </remarks>
    [Fact]
    public void Spaeter_reiht_ein_und_parkt()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out RuntimeComposition composition);

        SessionSnapshot sitzung = Sample.Session(window: 0) with { EndedAt = DateTimeOffset.Now };
        MerkeLaufend(composition, sitzung);

        using SaveSessionViewModel model = Dialog(host, sitzung, new FakeFernwartungen(1));

        bool fertig = false;
        model.Finished += (_, _) => fertig = true;

        model.Comment = "Dieser Text geht ausdrücklich NICHT mit.";
        model.LaterCommand.Execute(null);

        Assert.True(fertig, "Der Dialog hat sich nach „Später“ nicht geschlossen.");

        QueuedUpload? zeile = composition.Queue.Find(sitzung.SessionId);

        Assert.NotNull(zeile);
        Assert.Equal(sitzung.Comment, zeile!.Payload.Comment);
        Assert.Empty(composition.Queue.LoadOpenSessions());

        // Der Kern: Sie wartet auf eine Entscheidung, und ein Sendelauf laesst sie liegen.
        Assert.True(zeile.AwaitingDecision);
        Assert.Empty(composition.Queue.Lease(10));

        Assert.Contains(composition.Log.ForSession(sitzung.SessionId),
                        entry => entry.Operation == "dialog.later");
    }

    /// <summary>
    /// Wer das Fenster ohne Antwort schliesst, verliert die Sitzung nicht.
    /// </summary>
    /// <remarks>
    /// Die Antwort auf Punkt 6: Beim geordneten Beenden schliesst WPF jedes Fenster, und genau
    /// dann läuft dieser Weg. Eingereiht wird <b>wartend</b> — ungefragt gebucht wird nichts,
    /// und beim nächsten Start wird der Dialog erneut vorgelegt.
    /// </remarks>
    [Fact]
    public void Ein_unbeantworteter_Dialog_reiht_beim_Schliessen_wartend_ein()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out RuntimeComposition composition);

        SessionSnapshot sitzung = Sample.Session(window: 0) with { EndedAt = DateTimeOffset.Now };
        MerkeLaufend(composition, sitzung);

        SaveSessionViewModel model = Dialog(host, sitzung, new FakeFernwartungen(1));

        // Das tut das Fenster beim Schliessen, gleich ob per Kreuz oder beim Beenden.
        model.Dispose();

        QueuedUpload? zeile = composition.Queue.Find(sitzung.SessionId);

        Assert.NotNull(zeile);
        Assert.True(zeile!.AwaitingDecision, "Sie ginge ungefragt hinaus.");
        Assert.Empty(composition.Queue.Lease(10));
        Assert.Empty(composition.Queue.LoadOpenSessions());

        Assert.Contains(composition.Log.ForSession(sitzung.SessionId),
                        entry => entry.Operation == "dialog.unanswered");
    }

    /// <summary>
    /// Verwerfen hat nichts zu entfernen — und hinterlässt trotzdem seine Spur.
    /// </summary>
    /// <remarks>
    /// Es ist der einzige Ausgang, der Arbeitszeit verschwinden lässt. Ohne Zeile in der
    /// Warteschlange und ohne Datensatz in TANSS ist der Protokolleintrag alles, was von der
    /// Sitzung bleibt. Der Vermerk der laufenden Sitzung muss dabei fallen, sonst legte der
    /// nächste Start genau die verworfene Sitzung erneut vor.
    /// </remarks>
    [Fact]
    public void Verwerfen_ohne_Zeile_hinterlaesst_den_Protokolleintrag()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out RuntimeComposition composition);

        SessionSnapshot sitzung = Sample.Session(window: 0) with { EndedAt = DateTimeOffset.Now };
        MerkeLaufend(composition, sitzung);

        using SaveSessionViewModel model = Dialog(host, sitzung, new FakeFernwartungen(1));

        bool fertig = false;
        model.Finished += (_, _) => fertig = true;

        model.DiscardCommand.Execute(null);

        Assert.True(fertig);
        Assert.Null(composition.Queue.Find(sitzung.SessionId));
        Assert.Empty(composition.Queue.LoadOpenSessions());

        SessionLogEntry eintrag = Assert.Single(
            composition.Log.ForSession(sitzung.SessionId),
            entry => entry.Operation == "queue.discard");

        Assert.Contains("keiner Warteschlange", eintrag.Reason, StringComparison.Ordinal);
    }

    // --- Werkzeug --------------------------------------------------------------

    /// <summary>Baut eine echte Laufzeit auf einer Wegwerfdatenbank.</summary>
    private static AppHost Laufzeit(TempDirectory temp, out RuntimeComposition composition)
    {
        ConfigStore store = new(temp.File("config.json"));
        store.Save(Sample.Config());

        AppHost host = new(store, NullLoggerFactory.Instance, new RuntimeNotifier(null),
                           TimeProvider.System, temp.File("state.db"));

        Assert.True(host.Reload(), "Die Konfiguration liess sich nicht laden.");

        composition = Assert.IsType<RuntimeComposition>(host.Composition);
        return host;
    }

    /// <summary>Der Abschlussdialog zu einer Sitzung, die niemand eingereiht hat.</summary>
    private static SaveSessionViewModel Dialog(AppHost host, SessionSnapshot sitzung,
                                               IRemoteSupportRepository tanss) =>
        new(host, new SessionClosed(sitzung, SessionDisposition.Held, string.Empty),
            "Fernwartung", color: null, technician: null, booking: null, supports: tanss);

    /// <summary>
    /// Vermerkt die Sitzung als laufend — so, wie der Beobachter es in jedem Takt tut.
    /// </summary>
    /// <remarks>
    /// Dieser Vermerk ist seit dem Umbau die Sicherung, solange der Dialog offen steht. Die
    /// Fälle prüfen deshalb mit, dass er fällt, sobald die Sitzung anderswo steht — bliebe er
    /// liegen, legte der nächste Start dieselbe Sitzung ein zweites Mal vor.
    /// </remarks>
    private static void MerkeLaufend(RuntimeComposition composition, SessionSnapshot sitzung) =>
        composition.Queue.SaveOpenSession(new OpenSession
        {
            RemoteMaintenanceId = sitzung.SessionId,
            MonitorKey = sitzung.ProfileKey,
            RemoteSupportTypeId = sitzung.RemoteSupportTypeId,
            StartedAt = sitzung.StartedAt,
            LastSeenAt = sitzung.EndedAt ?? sitzung.StartedAt,
            Target = sitzung.Destination,
            DeviceName = sitzung.Destination,
            Comment = sitzung.Comment,
        });

    /// <summary>Eine beendete Sitzung, wie die Zustandsmaschine sie meldet.</summary>
    /// <remarks>
    /// Echt gebaut und nicht nachgestellt: <c>WatchedSession</c> vergibt ihre Kennung selbst,
    /// und genau diese Kennung ist es, die später in der Warteschlange stehen würde.
    /// </remarks>
    private static WatchedSession Beendet() =>
        new(new SessionCandidate(ProcessId: 4711, ProcessName: "mstsc", ProfileKey: "mstsc",
                                 Destination: "srv-test01", IdentityKey: "srv-test01",
                                 WindowHandle: 0, StartedAt: DateTimeOffset.Now.AddMinutes(-20)),
            DateTimeOffset.Now.AddMinutes(-20), ignoreTitleChanges: false);

    /// <summary>
    /// TANSS ohne TANSS.
    /// </summary>
    /// <remarks>
    /// <b>Sie merkt sich, was gesendet wurde</b> — nicht nur, dass gesendet wurde. Der ganze
    /// Sinn des direkten Weges ist, dass Bericht, Ticket und Firma wirklich in der Nutzlast
    /// stehen; eine Attrappe, die nur eine Erfolgsmeldung zurückgäbe, prüfte das nicht.
    /// </remarks>
    private sealed class FakeFernwartungen : IRemoteSupportRepository
    {
        private readonly int _id;
        private readonly TanssException? _fehler;

        /// <summary>Eine Attrappe, die anlegt.</summary>
        public FakeFernwartungen(int id) => _id = id;

        /// <summary>Eine Attrappe, die abweist.</summary>
        public FakeFernwartungen(TanssException fehler) => _fehler = fehler;

        /// <summary>Was wirklich hinausgegangen ist.</summary>
        public List<RemoteSupportWrite> Gesendet { get; } = [];

        public Task<RemoteSupportRead> CreateAsync(RemoteSupportWrite item,
                                                   CancellationToken ct = default) =>
            throw new NotSupportedException(
                "Der Dialog benutzt CreateWithDiagnosticsAsync: Nur jenes liefert die Gegenprobe "
                + "der Mitarbeiterzuordnung mit. Ein Weg hierher wäre ein Fehler.");

        public Task<RemoteSupportCreateResult> CreateWithDiagnosticsAsync(
            RemoteSupportWrite item, CancellationToken ct = default)
        {
            if (_fehler is not null)
            {
                throw _fehler;
            }

            Gesendet.Add(item);

            return Task.FromResult(new RemoteSupportCreateResult(
                new RemoteSupportRead
                {
                    Id = _id,
                    RemoteMaintenanceId = item.RemoteMaintenanceId,
                },
                AttributionConfirmed: true,
                Warning: null));
        }

        public Task<bool> ExistsAsync(string remoteMaintenanceId, DateTimeOffset around,
                                      CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> ExistsAsync(string remoteMaintenanceId, DateTimeOffset sessionStart,
                                      DateTimeOffset sessionEnd, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> ExistsAsync(RemoteSupportWrite session, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<RemoteSupportRead>> ListAsync(
            Timeframe timeframe, string? text = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RemoteSupportRead>>([]);

        public Task<IReadOnlyList<RemoteSupportSystem>> ListSystemsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RemoteSupportSystem>>([]);
    }
}
