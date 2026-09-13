using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.Services;
using TanssLogWatcher.App.ViewModels;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Der Abschlussdialog: dass er aufgeht, dass aus ihm heraus gebucht wird und dass ohne
/// Antwort nichts hinausgeht.
/// </summary>
/// <remarks>
/// <para><b>Der Befund, aus dem diese Datei entstanden ist.</b> Der Techniker meldete, nach dem
/// Ende einer Sitzung sei er nicht gefragt worden — weder nach Ticket noch nach Kunde oder
/// Gerät. In der Zustandsdatenbank stand die Sitzung als gebucht: Sie war nach fünf Minuten
/// Schonfrist von selbst hinausgegangen, mit der automatischen Beschreibung.</para>
///
/// <para>Geprüft wird deshalb dreierlei: dass ein misslungener Dialog die Schlange nicht
/// dauerhaft verriegelt (sonst kostete er unbemerkt jede weitere Sitzung), dass „In TANSS
/// buchen“ wirklich sendet und das Ergebnis stehen lässt, und dass eine unbeantwortete Sitzung
/// liegen bleibt statt hinauszugehen.</para>
///
/// <para>Echt ist hier alles bis auf zwei Orte: Konfiguration und Zustandsdatenbank liegen in
/// einem Wegwerfordner statt im Benutzerprofil, und gesendet wird gegen eine Attrappe statt
/// gegen TANSS — ein Test, der bucht, bucht sonst wirklich.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AbschlussdialogTests
{
    // --- Die Schlange der Dialoge ---------------------------------------------

    /// <summary>
    /// Der Riegel bleibt nach einem misslungenen Bau <b>nicht</b> stehen.
    /// </summary>
    /// <remarks>
    /// <para>Das ist der teuerste stille Fehler, den dieses Werkzeug haben kann: Ginge nach
    /// einem einzigen misslungenen Fensterbau nie wieder ein Abschlussdialog auf, verlöre jede
    /// weitere Sitzung ihren Bericht — und niemandem fiele es auf, weil nichts zu sehen ist.
    /// Der Riegel stand vorher in der ersten Zeile der Anzeigemethode, also <b>vor</b> dem
    /// Bau.</para>
    /// <para>Ob das je geschehen ist, ist unbelegt geblieben: Der Weg dorthin läuft über
    /// <c>RuntimeNotifier</c>, und der schluckt jede Ausnahme ohne Protokollzeile.</para>
    /// </remarks>
    [Fact]
    public void Ein_misslungener_Dialog_verriegelt_die_Schlange_nicht()
    {
        List<string> gezeigt = [];
        List<string> gescheitert = [];

        DialogGate gate = new(
            show: (closed, _) =>
            {
                // Nachgestellt ist der Fall, der in diesem Haus schon vorkam: eine
                // XamlParseException aus dem Bau des Fensters heraus.
                if (closed.Session.SessionId == "kaputt")
                {
                    throw new InvalidOperationException("XamlParseException nachgestellt.");
                }

                gezeigt.Add(closed.Session.SessionId);
            },
            failed: (closed, _) => gescheitert.Add(closed.Session.SessionId));

        gate.Enqueue(Geschlossen("kaputt"));

        Assert.Equal(["kaputt"], gescheitert);
        Assert.False(gate.IsOpen, "Der Riegel steht, obwohl gar kein Fenster aufging.");

        // Und der naechste geht auf. Ohne die Behebung endete es hier: Der Riegel stuende, und
        // diese Sitzung saehe der Techniker nie.
        gate.Enqueue(Geschlossen("heil"));

        Assert.Equal(["heil"], gezeigt);
        Assert.True(gate.IsOpen);
    }

    /// <summary>
    /// Einer nach dem anderen — und nach dem Schliessen kommt der nächste von selbst.
    /// </summary>
    /// <remarks>
    /// Enden zwei Sitzungen im selben Takt (beim Abmelden der Normalfall), stapelten sich sonst
    /// zwei Fenster übereinander, von denen der Techniker nur das oberste sieht.
    /// </remarks>
    [Fact]
    public void Es_geht_immer_nur_ein_Dialog_auf_und_danach_der_naechste()
    {
        List<string> gezeigt = [];
        Action? schliessen = null;

        DialogGate gate = new((closed, onClosed) =>
        {
            gezeigt.Add(closed.Session.SessionId);
            schliessen = onClosed;
        });

        gate.Enqueue(Geschlossen("erste"));
        gate.Enqueue(Geschlossen("zweite"));

        Assert.Equal(["erste"], gezeigt);
        Assert.Equal(1, gate.Waiting);

        schliessen!();

        Assert.Equal(["erste", "zweite"], gezeigt);
        Assert.Equal(0, gate.Waiting);
    }

    // --- Buchen aus dem Dialog heraus -----------------------------------------

    /// <summary>
    /// „In TANSS buchen“ sendet sofort — und das Ergebnis bleibt im Fenster stehen.
    /// </summary>
    /// <remarks>
    /// <para>Vorher schrieb der Knopf nur den Bericht in die wartende Zeile und schloss das
    /// Fenster; gesendet wurde irgendwann später vom Takt. Der Techniker sah nie, ob es
    /// geklappt hat.</para>
    /// <para>Dass das Fenster offen bleibt, ist kein Schönheitswunsch: Es ist gemessen bis
    /// unter den Bildschirmrand gewandert, seine Fussleiste lag im Streifen der Taskleiste, und
    /// es reisst sich den Vordergrund — ein einziger beiläufiger Klick erledigte es damit
    /// spurlos.</para>
    /// </remarks>
    [Fact]
    public async Task Buchen_sendet_sofort_und_laesst_das_Ergebnis_stehen()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out RuntimeComposition composition,
                                      out SessionSnapshot sitzung);

        Assert.True(composition.Queue.Enqueue(Sample.Upload(sitzung.SessionId),
                                              awaitDecision: true));

        FakeBuchung buchung = new(composition.Queue, 38614);
        SaveSessionViewModel model = Abschluss(host, sitzung, buchung);

        bool fertig = false;
        model.Finished += (_, _) => fertig = true;

        model.Comment = "Drucker neu eingerichtet.";
        await model.SaveCommand.ExecuteAsync(null);

        Assert.Equal(sitzung.SessionId, buchung.Gebucht);
        Assert.True(model.IsBooked);
        Assert.Contains("38614", model.Result, StringComparison.Ordinal);
        Assert.False(model.CanEdit, "Nach dem Buchen darf sich der Bericht nicht mehr ändern.");
        Assert.False(fertig, "Das Fenster hat sich von selbst geschlossen.");

        // Der Bericht ist VOR dem Senden in die Zeile gegangen - waere es umgekehrt, verlöre
        // ein Abbruch genau den Text, auf den es ankommt.
        QueuedUpload? zeile = composition.Queue.Find(sitzung.SessionId);
        Assert.NotNull(zeile);
        Assert.Equal(QueueState.Done, zeile!.State);
        Assert.Equal("Drucker neu eingerichtet.", zeile.Payload.Comment);

        SessionLogEntry gebucht = Assert.Single(
            composition.Log.ForSession(sitzung.SessionId),
            entry => entry.Operation == "dialog.booked");

        Assert.Equal(SessionOutcome.Ok, gebucht.Outcome);
        Assert.Equal(38614, gebucht.TanssSupportId);
    }

    /// <summary>
    /// Scheitert das Senden, bleibt die Zeile stehen — und der Dialog behauptet keinen Erfolg.
    /// </summary>
    /// <remarks>
    /// Das ist der einzige Zweck der Warteschlange: der Fehlerpfad. Keine Verbindung, TANSS
    /// antwortet nicht — die Sitzung ist trotzdem gesichert und wird wiederholt.
    /// </remarks>
    [Fact]
    public async Task Ein_misslungenes_Senden_laesst_die_Zeile_stehen_und_sagt_es()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out RuntimeComposition composition,
                                      out SessionSnapshot sitzung);

        _ = composition.Queue.Enqueue(Sample.Upload(sitzung.SessionId), awaitDecision: true);

        FakeBuchung buchung = new(composition.Queue, tanssId: null);
        SaveSessionViewModel model = Abschluss(host, sitzung, buchung);

        model.Comment = "Serverdienst neu gestartet.";
        await model.SaveCommand.ExecuteAsync(null);

        Assert.False(model.IsBooked);
        Assert.Contains("Warteschlange", model.Result, StringComparison.Ordinal);

        QueuedUpload? zeile = composition.Queue.Find(sitzung.SessionId);
        Assert.NotNull(zeile);
        Assert.Equal(QueueState.Pending, zeile!.State);
        Assert.Equal("Serverdienst neu gestartet.", zeile.Payload.Comment);

        // Und sie wartet nicht mehr auf einen Dialog, sondern auf den naechsten Sendeversuch -
        // sonst laege sie fuer immer da, obwohl der Techniker laengst entschieden hat.
        Assert.False(zeile.AwaitingDecision);
    }

    /// <summary>
    /// „Später“ <b>parkt</b> eine vorhandene Zeile — sie geht nicht hinaus.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Befund, aus dem dieser Fall in seiner heutigen Form entstand.</b> Vorher gab
    /// „Später“ die Zeile frei. Damit war sie sofort fällig und ging im nächsten Takt hinaus —
    /// gemessen eine Sekunde nach dem Klick, mit der automatischen Beschreibung. Der Techniker
    /// sah in der Warteschlange nichts, weil nichts liegenblieb, und meldete sie als
    /// verschwunden. Eine Schaltfläche namens „Später“, die sofort sendet, sagt das Gegenteil
    /// dessen, was sie tut.</para>
    /// <para>Geprüft wird deshalb beides: dass das Kennzeichen steht <b>und</b> dass ein
    /// Sendelauf die Zeile nicht anfasst. Das Kennzeichen allein wäre nur eine Behauptung über
    /// den Sendedienst.</para>
    /// </remarks>
    [Fact]
    public void Spaeter_parkt_die_Zeile()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out RuntimeComposition composition,
                                      out SessionSnapshot sitzung);

        _ = composition.Queue.Enqueue(Sample.Upload(sitzung.SessionId), awaitDecision: true);
        Assert.Empty(composition.Queue.Lease(10));

        SaveSessionViewModel model = Abschluss(host, sitzung, new FakeBuchung(composition.Queue,
                                                                             tanssId: null));
        bool fertig = false;
        model.Finished += (_, _) => fertig = true;

        model.LaterCommand.Execute(null);

        Assert.True(fertig, "Der Dialog hat sich nach „Später“ nicht geschlossen.");

        // Sie wartet weiter - und der Sendedienst kommt nicht an sie heran.
        Assert.True(composition.Queue.Find(sitzung.SessionId)!.AwaitingDecision);
        Assert.Empty(composition.Queue.Lease(10));

        Assert.Contains(composition.Log.ForSession(sitzung.SessionId),
                        entry => entry.Operation == "dialog.later");
    }

    /// <summary>
    /// Wer gar nicht antwortet, verliert nichts: Die Sitzung bleibt liegen und wird beim
    /// nächsten Start erneut vorgelegt.
    /// </summary>
    /// <remarks>
    /// <para><b>Die Antwort auf die Frage, die der Wegfall der Frist offenlässt.</b> Gewählt
    /// ist „beim nächsten Start erneut vorlegen“: Das verliert keine Arbeitszeit und bucht
    /// nichts ungefragt. Der andere Weg — beim Beenden hinausschicken, was noch wartet — hätte
    /// am Freitagabend jede Sitzung ohne Bericht gebucht und den häufigsten Fall, den Absturz,
    /// gar nicht erfasst.</para>
    /// </remarks>
    [Fact]
    public void Eine_unbeantwortete_Sitzung_wird_beim_naechsten_Start_erneut_vorgelegt()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out RuntimeComposition composition,
                                      out SessionSnapshot sitzung);

        _ = composition.Queue.Enqueue(Sample.Upload(sitzung.SessionId), awaitDecision: true);

        SessionWatcherService watcher = new(host);

        List<SessionClosed> vorgelegt = [];
        watcher.SessionEnded += (_, closed) => vorgelegt.Add(closed);

        watcher.OfferPendingDecisions(composition);

        SessionClosed wieder = Assert.Single(vorgelegt);
        Assert.Equal(sitzung.SessionId, wieder.Session.SessionId);

        // Vorgelegt, nicht gebucht: Die Zeile steht unveraendert da und wartet weiter.
        QueuedUpload? zeile = composition.Queue.Find(sitzung.SessionId);
        Assert.NotNull(zeile);
        Assert.True(zeile!.AwaitingDecision);
        Assert.Equal(QueueState.Pending, zeile.State);
        Assert.Empty(composition.Queue.Lease(10));

        Assert.Contains(composition.Log.ForSession(sitzung.SessionId),
                        entry => entry.Operation == "dialog.reoffer");
    }

    // --- Werkzeug --------------------------------------------------------------

    /// <summary>Eine abgeschlossene Sitzung, wie sie in die Dialogschlange geht.</summary>
    private static SessionClosed Geschlossen(string id) =>
        new(Sample.Session(window: 0) with { SessionId = id, EndedAt = DateTimeOffset.Now },
            SessionDisposition.Enqueued, "Eingereiht.");

    /// <summary>Baut eine echte Laufzeit auf einer Wegwerfdatenbank.</summary>
    private static AppHost Laufzeit(TempDirectory temp, out RuntimeComposition composition,
                                    out SessionSnapshot sitzung)
    {
        ConfigStore store = new(temp.File("config.json"));
        store.Save(Sample.Config());

        AppHost host = new(store, NullLoggerFactory.Instance, new RuntimeNotifier(null),
                           TimeProvider.System, temp.File("state.db"));

        Assert.True(host.Reload(), "Die Konfiguration liess sich nicht laden.");

        composition = Assert.IsType<RuntimeComposition>(host.Composition);
        sitzung = Sample.Session(window: 0) with { EndedAt = DateTimeOffset.Now };

        return host;
    }

    private static SaveSessionViewModel Abschluss(AppHost host, SessionSnapshot sitzung,
                                                  IImmediateBooking buchung) =>
        new(host, new SessionClosed(sitzung, SessionDisposition.Enqueued, string.Empty),
            "Fernwartung", color: null, technician: null, booking: buchung);

    /// <summary>
    /// Ein Sendeweg ohne TANSS.
    /// </summary>
    /// <remarks>
    /// <b>Er tut an der Warteschlange, was der echte tut</b> — zuteilen und quittieren. Eine
    /// Attrappe, die bloss eine Erfolgsmeldung zurückgäbe, liesse die Zeile in Wahrheit wartend
    /// stehen, und der Test bestätigte eine Buchung, die es nicht gab.
    /// </remarks>
    private sealed class FakeBuchung(IUploadQueue queue, int? tanssId) : IImmediateBooking
    {
        /// <summary>Welche Kennung gebucht werden sollte.</summary>
        public string? Gebucht { get; private set; }

        public Task<UploadReport?> BookNowAsync(string remoteMaintenanceId,
                                                CancellationToken ct = default)
        {
            Gebucht = remoteMaintenanceId;

            if (queue.LeaseOne(remoteMaintenanceId) is not { } item)
            {
                return Task.FromResult<UploadReport?>(null);
            }

            if (tanssId is not { } id)
            {
                queue.MarkFailed(remoteMaintenanceId,
                    "Keine Verbindung zum Server.",
                    DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15));

                return Task.FromResult<UploadReport?>(new UploadReport(remoteMaintenanceId,
                    UploadOutcome.Deferred,
                    "Keine Verbindung zum Server. Zurückgestellt; ein späterer Versuch kann "
                    + "das heilen."));
            }

            queue.MarkDone(remoteMaintenanceId);

            return Task.FromResult<UploadReport?>(new UploadReport(remoteMaintenanceId,
                UploadOutcome.Uploaded,
                $"Hochgeladen; TANSS führt sie unter der Kennung {id}.", id));
        }
    }
}
