using System.Globalization;
using System.Runtime.Versioning;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Storage;
using TanssLogWatcher.Storage.History;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;

namespace TanssLogWatcher.App.Services;

/// <summary>
/// Sendet die fälligen Einträge der Warteschlange nach TANSS.
/// </summary>
/// <remarks>
/// <para><b>Die eine Regel, um die sich hier alles dreht: TANSS dedupliziert nicht.</b> Ein
/// zweiter POST mit derselben <c>remoteMaintenanceId</c> erzeugt nachweislich einen zweiten
/// Datensatz (11.09.2026, IDs 38584 und 38585), und eine Dublette ist nur per direktem
/// Datenbankzugriff wieder zu entfernen — die Löschroute ist ohne typgebundenes Token
/// unerreichbar. Vor jeder Wiederholung steht deshalb die Existenzprüfung.</para>
///
/// <para><b>„Unbekannt“ heisst nicht „nicht vorhanden“.</b> Scheitert die Existenzprüfung
/// selbst, dann ist gerade nicht geklärt, ob der Datensatz da ist. Hochzuladen wäre in diesem
/// Zustand genau der Fehler, gegen den die Prüfung aufgestellt wurde. Der Eintrag wird
/// zurückgestellt, und der Durchlauf läuft mit den übrigen weiter (Hausregel 5).</para>
///
/// <para><b>Im Zustand <see cref="RuntimeState.Degraded"/> ruht nur dieser Dienst.</b> Die
/// Beobachtung läuft weiter und die Warteschlange füllt sich — dafür ist sie da. Das Ruhen
/// geschieht dabei nicht über einen Schalter, sondern über den Rückstau der Warteschlange:
/// Jeder misslungene Versuch verschiebt den nächsten, und <see cref="IUploadQueue.Lease"/>
/// gibt nur Fälliges heraus. Ein eigener „Pausenzustand“ müsste wieder aufgehoben werden, und
/// niemand wüsste, wann.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UploadService : PeriodicService, IImmediateBooking
{
    /// <summary>Wie viele Einträge ein Durchgang höchstens sendet.</summary>
    private const int BatchSize = 25;

    /// <summary>
    /// Ab wann ein Eintrag auf <c>sending</c> als hängengeblieben gilt.
    /// </summary>
    /// <remarks>
    /// Grosszügig: Ein Upload dauert Sekunden, nicht Minuten. Fünf Minuten heissen also
    /// zuverlässig „der Prozess ist weg“ und nicht „er ist noch dran“ — und ein zu früh
    /// zurückgeholter Eintrag wäre der direkte Weg zur Dublette.
    /// </remarks>
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Wie oft nach abgelaufenen Aufbewahrungsfristen gesehen wird.
    /// </summary>
    /// <remarks>
    /// Stündlich und nicht bei jedem Takt: Eine Frist steht in Tagen, der Takt in Sekunden.
    /// Zweitausendachthundert Durchläufe am Tag löschten neunundzwanzigmal dasselbe Nichts.
    /// </remarks>
    private static readonly TimeSpan HousekeepingEvery = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    private DateTimeOffset? _lastHousekeeping;

    /// <summary>Baut den Dienst.</summary>
    /// <param name="context">Der Zugang zu Zustand und Zusammenbau.</param>
    public UploadService(IRuntimeContext context) : base(context)
    {
    }

    /// <summary>Ein einzelner Eintrag ist abgearbeitet.</summary>
    public event EventHandler<UploadReport>? EntryProcessed;

    /// <summary>Der Stand der Warteschlange hat sich geändert.</summary>
    public event EventHandler<QueueSnapshot>? QueueChanged;

    /// <inheritdoc />
    public override string Name => "Sendedienst";

    /// <inheritdoc />
    public override string Description =>
        "Sendet die fälligen Einträge der Warteschlange nach TANSS und fragt vor jeder "
        + "Wiederholung nach, ob die Fernwartung nicht längst dort steht.";

    /// <summary>Der zuletzt erhobene Stand der Warteschlange.</summary>
    public QueueSnapshot Queue { get; private set; } = QueueSnapshot.Unavailable;

    /// <inheritdoc />
    /// <remarks>
    /// Deutlich langsamer als die Beobachtung: Was fällig ist, entscheidet der Rückstau der
    /// Warteschlange und nicht dieser Takt. Häufiger nachzusehen brächte nichts und hielte die
    /// Zustandsdatenbank ohne Not beschäftigt.
    /// </remarks>
    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    protected override string NotConfiguredMessage =>
        "Ruht: Ohne Konfiguration ist keine TANSS-Instanz bekannt, an die gesendet werden "
        + "könnte. Es liegt auch nichts an — ohne Beobachtung entsteht nichts.";

    /// <inheritdoc />
    protected override string FaultMessage =>
        "Der letzte Sendelauf ist abgebrochen. Kein Eintrag geht dabei verloren: Was nicht "
        + "quittiert wurde, bleibt in der Warteschlange und wird beim nächsten Lauf erneut "
        + "versucht — mit vorgeschalteter Existenzprüfung, damit keine Dublette entsteht.";

    /// <summary>
    /// Sendet sofort, statt auf den nächsten Takt zu warten.
    /// </summary>
    /// <remarks>
    /// <para>Für die Schaltfläche „Jetzt senden“. Läuft über dieselbe Sperre wie der Takt: Zwei
    /// gleichzeitige Läufe teilten sich zwar die Einträge sauber auf, würden aber beide den
    /// Rückstau desselben misslungenen Eintrags fortschreiben.</para>
    ///
    /// <para><b>Von Hand heisst jetzt.</b> Zurückgehaltene Einträge werden vorher freigegeben —
    /// sonst tut diese Schaltfläche buchstäblich nichts, solange ein Rückstau nach einem
    /// Fehlversuch steht oder eine Zeile auf die Antwort des Abschlussdialogs wartet. Genau so
    /// war es: „1 ausstehend“ daneben, ein Klick, und die Meldung „Nichts fällig“ — nicht zu
    /// unterscheiden von einer kaputten Schaltfläche.</para>
    ///
    /// <para><b>Auch die wartenden Zeilen gehen dabei hinaus, und das ist kein Widerspruch zum
    /// fehlenden Zeitablauf:</b> Sie gehen nicht, weil eine Uhr abgelaufen wäre, sondern weil
    /// der Techniker es in diesem Augenblick verlangt hat. Sie tragen dann die automatische
    /// Beschreibung — dasselbe, was „Später“ im Dialog tut.</para>
    ///
    /// <para>Der Takt tut das <b>nicht</b>. Ohne ausdrückliches Verlangen wartet eine
    /// zurückgestellte Zeile weiter, und zwar unbegrenzt.</para>
    /// </remarks>
    /// <param name="ct">Abbruchmarke.</param>
    public async Task<UploadRunResult> FlushNowAsync(CancellationToken ct = default)
    {
        if (Context.Composition is not { } composition)
        {
            return UploadRunResult.None;
        }

        int released = ReleaseHeld(composition);
        UploadRunResult result = await FlushAsync(composition, ct).ConfigureAwait(false);

        return result with { Released = released };
    }

    /// <summary>
    /// Zieht alle zurückgehaltenen Einträge auf jetzt vor.
    /// </summary>
    /// <remarks>
    /// <para>Zwei Arten von Zurückhaltung, und beide gehören hierher: der Rückstau nach einem
    /// Fehlversuch (<c>NextAttemptAt</c> in der Zukunft) und die Zeile, die auf die Antwort des
    /// Abschlussdialogs wartet (<c>AwaitingDecision</c>). Die zweite ist der Grund, warum das
    /// hier überhaupt bleibt: Ohne sie wäre eine unbeantwortete Zeile über die Oberfläche gar
    /// nicht mehr loszuwerden — denn eine Uhr, die sie freigibt, gibt es nicht mehr.</para>
    /// <para>Hausregel 5: Misslingt es, ist das kein Grund, den Sendelauf ausfallen zu lassen —
    /// er nimmt dann eben nur das mit, was ohnehin fällig war.</para>
    /// </remarks>
    /// <param name="composition">Die Bausteine.</param>
    /// <returns>Wie viele Einträge vorgezogen wurden.</returns>
    private int ReleaseHeld(RuntimeComposition composition)
    {
        DateTimeOffset now = Context.Clock.GetUtcNow();
        int released = 0;

        try
        {
            foreach (QueuedUpload item in composition.Queue.List(QueueState.Pending).Items)
            {
                if ((item.AwaitingDecision || item.NextAttemptAt > now)
                    && composition.Queue.Release(item.RemoteMaintenanceId))
                {
                    released++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Der Lauf selbst meldet gleich, wenn die Warteschlange nicht lesbar ist.
            _ = ex;
        }

        return released;
    }

    /// <summary>
    /// Bucht genau einen wartenden Eintrag sofort — der Weg des Abschlussdialogs.
    /// </summary>
    /// <remarks>
    /// <para><b>Derselbe Sendeweg wie im Takt, und das ist der ganze Punkt.</b> Gebucht wird
    /// über <see cref="SendOneAsync"/>: dieselbe Existenzprüfung, dasselbe
    /// <c>MarkDone</c>/<c>MarkFailed</c>, derselbe Protokolleintrag, derselbe Verlaufsvermerk.
    /// Ein eigener „schneller Weg“ aus dem Dialog heraus hätte die Prüfung umgangen, die
    /// verhindert, dass TANSS eine Fernwartung zweimal führt — und TANSS dedupliziert
    /// nicht.</para>
    ///
    /// <para><b>Scheitert es, bleibt die Zeile stehen.</b> Genau dafür gibt es die
    /// Warteschlange: Der Sendedienst wiederholt sie wie jede andere, mit Rückstau und mit
    /// Existenzprüfung. Der Dialog behauptet dann keinen Erfolg, sondern sagt, dass die
    /// Sitzung gesichert ist und später hinausgeht.</para>
    ///
    /// <para>Dieselbe Sperre wie der Takt: Zwei gleichzeitige Läufe teilten sich zwar die
    /// Einträge sauber auf, würden aber beide den Rückstau desselben Eintrags
    /// fortschreiben.</para>
    /// </remarks>
    /// <param name="remoteMaintenanceId">Der Eintrag, üblicherweise die Sitzungskennung.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>
    /// Was mit dem Eintrag geschehen ist, oder <see langword="null"/>, wenn er gar nicht mehr
    /// wartete — dann ist er unterwegs, gesendet oder aufgegeben, und der Aufrufer darf keinen
    /// zweiten Versand auslösen.
    /// </returns>
    public async Task<UploadReport?> BookNowAsync(string remoteMaintenanceId,
                                                  CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteMaintenanceId);

        if (Context.Composition is not { } composition)
        {
            return null;
        }

        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);

        UploadReport report;
        try
        {
            QueuedUpload? item = composition.Queue.LeaseOne(remoteMaintenanceId);

            if (item is null)
            {
                return null;
            }

            try
            {
                report = await SendOneAsync(composition, item, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Wortgleich zum Takt: Der Eintrag darf nicht auf "unterwegs" stehenbleiben,
                // sonst fasst ihn erst RequeueStuck in fuenf Minuten wieder an.
                string reason = "Unerwarteter Fehler beim Senden: " + Redaction.Scrub(ex.Message);
                Defer(composition, item, reason);
                report = new UploadReport(item.RemoteMaintenanceId, UploadOutcome.Deferred,
                                          reason);
            }
        }
        finally
        {
            _ = _oneAtATime.Release();
        }

        // Erst nach der Sperre: Beides meldet an die Oberflaeche, und die Oberflaeche soll
        // nicht auf einen Sendelauf warten muessen.
        _ = RefreshQueue();
        Context.Notifier.Raise(EntryProcessed, this, report);

        return report;
    }

    /// <summary>Erhebt den Stand der Warteschlange und meldet ihn.</summary>
    /// <remarks>
    /// Auch für die Ansicht gedacht, die den Stand nach einer Handlung neu lesen will, ohne
    /// einen Sendelauf auszulösen.
    /// </remarks>
    public QueueSnapshot RefreshQueue()
    {
        if (Context.Composition is not { } composition)
        {
            return Publish(QueueSnapshot.Unavailable);
        }

        try
        {
            QueueListing pending = composition.Queue.List(QueueState.Pending);
            QueueListing failed = composition.Queue.List(QueueState.Failed);

            return Publish(new QueueSnapshot
            {
                Pending = composition.Queue.Count(QueueState.Pending),
                Sending = composition.Queue.Count(QueueState.Sending),
                Failed = composition.Queue.Count(QueueState.Failed),
                Done = composition.Queue.Count(QueueState.Done),
                Unreadable = pending.Unreadable + failed.Unreadable,
                PendingItems = pending.Items,
                FailedItems = failed.Items,
                UnreadableNotice = pending.UnreadableNotice ?? failed.UnreadableNotice,
                At = Context.Clock.GetLocalNow(),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Context.ReportDegraded(DegradedCause.StateDatabase,
                "Die Zustandsdatenbank liess sich nicht lesen. Üblichste Ursache: ein zweiter "
                + "Lauf des Werkzeugs hält die Datei, oder das Benutzerprofil liegt auf einem "
                + "gerade nicht erreichbaren Netzlaufwerk. Meldung: " + Redaction.Scrub(ex.Message),
                "Zweite Instanz beenden und die Anzeige neu laden. Solange das so bleibt, kann "
                + "keine Sitzung zwischengelagert werden — ein Netzfehler kostet dann die "
                + "Sitzung selbst.");

            return Publish(QueueSnapshot.Unavailable);
        }
    }

    /// <summary>Holt hängengebliebene Einträge zurück, bevor zum ersten Mal gesendet wird.</summary>
    /// <remarks>
    /// Sie kommen mit <see cref="QueuedUpload.OutcomeUnknown"/> zurück — das Kennzeichen, an dem
    /// gleich darauf die Existenzprüfung hängt. Ohne diesen Schritt blieben sie auf
    /// „unterwegs“ stehen und würden nie wieder angefasst.
    /// </remarks>
    protected override Task PrimeAsync(RuntimeComposition composition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(composition);

        try
        {
            _ = composition.Queue.RequeueStuck(StuckAfter);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Context.ReportDegraded(DegradedCause.StateDatabase,
                "Hängengebliebene Warteschlangeneinträge liessen sich nicht zurückholen. "
                + "Meldung: " + Redaction.Scrub(ex.Message),
                "Zweite Instanz beenden und die Anzeige neu laden. Einträge auf „unterwegs“ "
                + "bleiben sonst stehen und werden nie wieder versucht.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task<string> RunCycleAsync(RuntimeComposition composition,
                                                        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(composition);

        UploadRunResult result = await FlushAsync(composition, ct).ConfigureAwait(false);

        // Nichts faellig und trotzdem gestoert: Die Warteschlange allein kann die Stoerung
        // nicht aufheben, weil sie ohne Eintrag nie einen Aufruf macht. Ein billiger Griff
        // nach der Technikerliste beantwortet die Frage, ob Netz, Token und Recht wieder
        // zusammen tragen - und hebt den Zustand auf, sobald sie es tun.
        if (result.Attempted == 0 && Context.Status.State == RuntimeState.Degraded)
        {
            await ProbeAsync(composition, ct).ConfigureAwait(false);
        }

        // Zuletzt, damit das Aufraeumen nie einen Sendeversuch aufhaelt: Was hier geloescht
        // wird, hat seine Frist seit Stunden ueberschritten und kann die paar Sekunden warten.
        if (_lastHousekeeping is not { } last
            || Context.Clock.GetUtcNow() - last >= HousekeepingEvery)
        {
            Housekeep(composition);
        }

        return result.Summary;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _oneAtATime.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Ein vollständiger Sendelauf.</summary>
    private async Task<UploadRunResult> FlushAsync(RuntimeComposition composition,
                                                   CancellationToken ct)
    {
        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            int requeued;
            IReadOnlyList<QueuedUpload> leased;

            try
            {
                requeued = composition.Queue.RequeueStuck(StuckAfter);
                leased = composition.Queue.Lease(BatchSize);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Context.ReportDegraded(DegradedCause.StateDatabase,
                    "Die Warteschlange liess sich nicht lesen; es kann gerade nichts gesendet "
                    + "werden. Üblichste Ursache: ein zweiter Lauf des Werkzeugs hält "
                    + "state.db. Meldung: " + Redaction.Scrub(ex.Message),
                    "Zweite Instanz beenden. Die erfassten Sitzungen bleiben erhalten; gesendet "
                    + "wird, sobald die Datenbank wieder zu öffnen ist.");

                _ = Publish(QueueSnapshot.Unavailable);
                return UploadRunResult.None;
            }

            if (leased.Count == 0)
            {
                _ = RefreshQueue();
                return new UploadRunResult { Requeued = requeued };
            }

            List<UploadReport> reports = [];
            foreach (QueuedUpload item in leased)
            {
                ct.ThrowIfCancellationRequested();

                // Hausregel 5: Der Fehler eines einzelnen Vorgangs bricht niemals den Durchlauf
                // ab. Ohne diesen Riegel koennte ein einziger kaputter Eintrag jede weitere
                // Sitzung festhalten - also genau das verhindern, wozu die Warteschlange da ist.
                UploadReport report;
                try
                {
                    report = await SendOneAsync(composition, item, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    string reason = "Unerwarteter Fehler beim Senden: "
                        + Redaction.Scrub(ex.Message);
                    Defer(composition, item, reason);
                    report = new UploadReport(item.RemoteMaintenanceId, UploadOutcome.Deferred,
                                              reason);
                }

                reports.Add(report);
                Context.Notifier.Raise(EntryProcessed, this, report);
            }

            _ = RefreshQueue();

            return new UploadRunResult
            {
                Requeued = requeued,
                Attempted = reports.Count,
                Uploaded = reports.Count(r => r.Outcome == UploadOutcome.Uploaded),
                AlreadyPresent = reports.Count(r => r.Outcome == UploadOutcome.AlreadyPresent),
                Deferred = reports.Count(r => r.Outcome is UploadOutcome.Deferred
                                                        or UploadOutcome.OutcomeUnknown),
                GivenUp = reports.Count(r => r.Outcome == UploadOutcome.GivenUp),
                Reports = reports,
            };
        }
        finally
        {
            _ = _oneAtATime.Release();
        }
    }

    /// <summary>Ein einzelner Eintrag: prüfen, senden, vermerken.</summary>
    private async Task<UploadReport> SendOneAsync(RuntimeComposition composition,
                                                  QueuedUpload item, CancellationToken ct)
    {
        // Geprueft wird bei ungeklaertem Ausgang - und ebenso bei jedem Eintrag, der schon
        // einmal draussen war. Die Warteschlange kennzeichnet nur den Absturz zwischen
        // Zuteilung und Rueckmeldung; eine Zeitueberschreitung MITTEN im POST sieht sie nicht,
        // laesst aber genauso offen, ob TANSS den Datensatz angenommen hat. Die Abfrage kostet
        // einen Aufruf, die Dublette kostet eine falsche Rechnung beim Kunden - und ist nur
        // noch per direktem Datenbankzugriff wieder zu entfernen.
        if (item.OutcomeUnknown || item.Attempts > 0)
        {
            bool exists;
            try
            {
                exists = await composition.RemoteSupports.ExistsAsync(item.Payload, ct)
                    .ConfigureAwait(false);
            }
            catch (TanssException ex)
            {
                // „Unbekannt“, nicht „nicht vorhanden“: In diesem Zustand darf nicht
                // hochgeladen werden, sonst entsteht genau die Dublette, die TANSS nicht
                // abfaengt. Der Eintrag wird zurueckgestellt, der Durchlauf laeuft weiter.
                string reason =
                    "Die Existenzprüfung ist fehlgeschlagen; ob die Fernwartung schon in TANSS "
                    + "steht, ist damit ungeklärt. Es wird ausdrücklich nicht gesendet — TANSS "
                    + "dedupliziert nicht, und eine Dublette ist nur per direktem "
                    + "Datenbankzugriff wieder zu entfernen. Der Eintrag bleibt stehen und wird "
                    + "später erneut geprüft. Meldung: " + Redaction.Scrub(ex.Message);

                Defer(composition, item, reason);
                Degrade(ex, composition);
                return new UploadReport(item.RemoteMaintenanceId, UploadOutcome.OutcomeUnknown,
                                        reason);
            }

            if (exists)
            {
                const string reason =
                    "Steht bereits in TANSS; abgehakt statt ein zweites Mal gesendet.";

                composition.Queue.MarkDone(item.RemoteMaintenanceId);
                Log(composition, item, SessionOutcome.Skipped, reason);
                Context.ReportWorking();
                return new UploadReport(item.RemoteMaintenanceId, UploadOutcome.AlreadyPresent,
                                        reason);
            }
        }

        try
        {
            // CreateWithDiagnosticsAsync und nicht CreateAsync: Jenes wirft die Gegenprobe der
            // Attribution weg. Sie sagt, ob TANSS die Fernwartung wirklich dem erwarteten
            // Mitarbeiter zugeordnet hat - und eine Fernwartung, die auf einem fremden
            // Mitarbeiter landet, ist falsch gebuchte Arbeitszeit, die niemandem auffaellt.
            RemoteSupportCreateResult result = await composition.RemoteSupports
                .CreateWithDiagnosticsAsync(item.Payload, ct).ConfigureAwait(false);

            RemoteSupportRead created = result.Support;

            composition.Queue.MarkDone(item.RemoteMaintenanceId);

            string note = result.AttributionConfirmed
                ? "Hochgeladen."
                : "Hochgeladen, aber die Zuordnung zum Mitarbeiter ist unbestätigt: "
                  + (result.Warning ?? "TANSS hat sie im meta-Block nicht gemeldet.");

            Log(composition, item, SessionOutcome.Ok, note, created.Id);
            RecordBooked(composition, item, created.Id, note);
            Context.ReportWorking();

            return new UploadReport(item.RemoteMaintenanceId, UploadOutcome.Uploaded,
                result.AttributionConfirmed
                    ? string.Create(CultureInfo.CurrentCulture,
                        $"Hochgeladen; TANSS führt sie unter der Kennung {created.Id}.")
                    : string.Create(CultureInfo.CurrentCulture,
                        $"Hochgeladen unter der Kennung {created.Id} — die Zuordnung zum "
                        + $"Mitarbeiter hat TANSS aber nicht bestätigt. Nachzusehen ist, auf wen "
                        + $"die Fernwartung dort gebucht ist."),
                created.Id);
        }
        catch (TanssException ex)
        {
            Degrade(ex, composition);

            if (RetryPolicy.IsTransient(ex))
            {
                string reason = Redaction.Scrub(ex.Message)
                    + " Zurückgestellt; ein späterer Versuch kann das heilen. Die Sitzung ist "
                    + "nicht verloren — sie bleibt in der Warteschlange.";

                Defer(composition, item, reason);
                return new UploadReport(item.RemoteMaintenanceId, UploadOutcome.Deferred, reason);
            }

            // Kein Warten heilt das: ein Typ, den es in TANSS nicht gibt, ein nicht
            // lizenziertes Modul, ein abgelaufenes Token. Aufgeben heisst hier nicht
            // wegwerfen - die Zeile bleibt mit Grund stehen und ist von Hand zu klaeren.
            string giveUp = Redaction.Scrub(ex.Message)
                + " Dieser Fehler heilt nicht durch Warten; der Eintrag wird nicht mehr von "
                + "selbst versucht. Er bleibt mit diesem Grund stehen und ist nach dem Beheben "
                + "der Ursache von Hand nachzutragen — vorher prüfen, ob die Fernwartung nicht "
                + "doch schon in TANSS steht.";

            composition.Queue.MarkFailed(item.RemoteMaintenanceId, giveUp, nextAttempt: null);
            Log(composition, item, SessionOutcome.Error, giveUp);
            RecordFailed(composition, item, giveUp);
            return new UploadReport(item.RemoteMaintenanceId, UploadOutcome.GivenUp, giveUp);
        }
    }

    /// <summary>
    /// Ein billiger echter Aufruf, der die Störung aufheben kann.
    /// </summary>
    /// <remarks>
    /// <c>GET /api/tanss.x/v1/technicians</c> liest, ändert nichts und beantwortet drei Fragen
    /// auf einmal: Netz, Adresse und Token. Gelingt er, trägt die Einrichtung wieder.
    /// </remarks>
    private async Task ProbeAsync(RuntimeComposition composition, CancellationToken ct)
    {
        try
        {
            _ = await composition.Technicians.ListAsync(ct).ConfigureAwait(false);
            Context.ReportWorking();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeFailure.Degrade(Context, ex, composition.Config.Tanss.BaseUrl);
        }
    }

    /// <summary>
    /// Lässt die Aufbewahrungsfristen laufen.
    /// </summary>
    /// <remarks>
    /// <para><b>Hier und nicht in einem eigenen Dienst.</b> Dieser hat die Warteschlange
    /// ohnehin in der Hand, und er läuft, solange das Werkzeug läuft. Eine Frist, die an einer
    /// Ansicht hinge, liefe nur, solange jemand hinsieht — und eine Frist, die nur läuft, wenn
    /// jemand hinsieht, ist keine.</para>
    ///
    /// <para>Was gelöscht wird und welche Frist für was gilt, steht bei
    /// <see cref="StatePruner"/>. Hier steht nur, <b>wann</b>.</para>
    /// </remarks>
    private void Housekeep(RuntimeComposition composition)
    {
        _lastHousekeeping = Context.Clock.GetUtcNow();

        PruneResult result =
            new StatePruner(composition.Queue, composition.Log, HistoryRetentionOf(composition))
                .Run(SessionLog.RetentionOf(composition.Config));

        if (!result.DidAnything)
        {
            // Ein stuendlicher Eintrag „nichts zu tun“ waere genau die Art Rauschen, die das
            // Protokoll unbrauchbar macht, das er gerade aufgeraeumt hat.
            return;
        }

        try
        {
            _ = composition.Log.Append(new SessionLogEntry
            {
                Operation = "state.prune",
                Outcome = result.FailedSteps > 0 ? SessionOutcome.Error : SessionOutcome.Ok,
                Reason = result.Summary,
                Trigger = SessionTrigger.Retry,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Geloescht ist geloescht, auch wenn der Vermerk darueber misslingt.
        }
    }

    /// <summary>
    /// Der Verlauf samt seinen beiden Fristen — oder <c>null</c>, wenn er sich nicht öffnen
    /// lässt.
    /// </summary>
    /// <remarks>
    /// <para><b><c>history.enabled</c> wird hier ausdrücklich nicht geprüft.</b> Was ein
    /// früherer Lauf mit eingeschaltetem Verlauf hinterlassen hat, muss gelöscht und
    /// geschwärzt werden, wenn seine Frist abläuft. Würde das Abschalten auch das Aufräumen
    /// abschalten, hielte ein einziger Klick die vorhandenen Zeilen für immer — samt dem
    /// Klartext der Gegenstelle. Dieselbe Überlegung wie bei
    /// <see cref="RuntimeComposition.Recordings"/>.</para>
    ///
    /// <para>Hausregel 5: Lässt sich die Datenbank gerade nicht öffnen, räumen Warteschlange
    /// und Protokoll trotzdem auf — der Verlauf nimmt an diesem Durchlauf dann eben nicht
    /// teil, und das Ergebnis sagt das, statt eine Null zu melden.</para>
    /// </remarks>
    private static HistoryRetention? HistoryRetentionOf(RuntimeComposition composition)
    {
        try
        {
            return new HistoryRetention(
                composition.History,
                TimeSpan.FromDays(composition.Config.History.RetentionDays),
                SessionHistoryStore.PlainTextRetentionOf(composition.Config));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Trägt die von TANSS vergebene Kennung in den Verlauf ein.
    /// </summary>
    /// <remarks>
    /// <para>Erst hier steht fest, dass die Sitzung wirklich gebucht ist — der Beobachter
    /// konnte sie nur einreihen. Ohne diesen Schritt zeigte der Verlauf auf Dauer
    /// „eingereiht“, obwohl die Fernwartung längst in TANSS steht, und der Techniker legte
    /// sie ein zweites Mal an.</para>
    ///
    /// <para>Abgeriegelt wie der Protokolleintrag (Hausregel 5): Der Upload ist geschehen und
    /// wird durch einen misslungenen Vermerk nicht ungeschehen.</para>
    /// </remarks>
    private static void RecordBooked(RuntimeComposition composition, QueuedUpload item,
                                     int tanssSupportId, string reason)
    {
        if (!composition.Config.History.Enabled)
        {
            return;
        }

        try
        {
            _ = composition.History.MarkBooked(item.RemoteMaintenanceId, tanssSupportId, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Ein nicht geschriebener Verlaufsvermerk darf keinen Upload kosten.
        }
    }

    /// <summary>
    /// Hält im Verlauf fest, dass der Eintrag endgültig aufgegeben wurde.
    /// </summary>
    /// <remarks>
    /// <b>Nur beim Aufgeben, nicht beim Zurückstellen.</b> Ein zurückgestellter Eintrag ist
    /// weiterhin unterwegs; ihn als gescheitert zu führen hiesse, dem Techniker eine Lücke zu
    /// zeigen, die sich beim nächsten Sendelauf von selbst schliesst.
    /// </remarks>
    private static void RecordFailed(RuntimeComposition composition, QueuedUpload item,
                                     string reason)
    {
        if (!composition.Config.History.Enabled)
        {
            return;
        }

        try
        {
            _ = composition.History.MarkFailed(item.RemoteMaintenanceId, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: siehe RecordBooked.
        }
    }

    /// <summary>Stellt einen Eintrag mit Rückstau zurück.</summary>
    private void Defer(RuntimeComposition composition, QueuedUpload item, string reason)
    {
        try
        {
            composition.Queue.MarkFailed(item.RemoteMaintenanceId, reason,
                Backoff.NextAttemptAfter(Context.Clock.GetLocalNow(), item.Attempts + 1));
            Log(composition, item, SessionOutcome.Deferred, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Laesst sich der Rueckstau nicht vermerken, bleibt der Eintrag auf
            // "unterwegs" stehen - RequeueStuck holt ihn beim naechsten Lauf zurueck, dann mit
            // ungeklaertem Ausgang und damit mit Existenzpruefung. Nichts geht verloren.
        }
    }

    /// <summary>
    /// Meldet einen Fehlschlag als Störung.
    /// </summary>
    /// <remarks>
    /// Eingeordnet wird in <see cref="RuntimeFailure"/> und nicht hier: Derselbe Fehler muss vom
    /// Sendedienst und vom Verbindungstest gleich benannt werden, sonst flackert der
    /// Betriebszustand zwischen zwei Anweisungen für dieselbe Ursache.
    /// </remarks>
    private void Degrade(Exception error, RuntimeComposition composition) =>
        RuntimeFailure.Degrade(Context, error, composition.Config.Tanss.BaseUrl);

    /// <summary>Übernimmt und meldet einen neuen Stand der Warteschlange.</summary>
    private QueueSnapshot Publish(QueueSnapshot snapshot)
    {
        Queue = snapshot;
        Context.Notifier.Raise(QueueChanged, this, snapshot);
        return snapshot;
    }

    /// <summary>
    /// Schreibt einen Eintrag ins Änderungsprotokoll.
    /// </summary>
    /// <remarks>
    /// Der Begründungstext läuft in <see cref="SessionLog"/> ohnehin durch
    /// <see cref="Redaction"/>; deutsche Prosa bleibt dabei stehen, Geheimnisse nicht.
    /// </remarks>
    private static void Log(RuntimeComposition composition, QueuedUpload item,
                            SessionOutcome outcome, string reason, int? tanssSupportId = null)
    {
        try
        {
            _ = composition.Log.Append(new SessionLogEntry
            {
                Operation = "queue.flush",
                Outcome = outcome,
                Reason = reason,
                Trigger = SessionTrigger.Retry,
                RemoteMaintenanceId = item.RemoteMaintenanceId,
                TanssSupportId = tanssSupportId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Ein nicht geschriebener Protokolleintrag darf keinen Upload kosten.
        }
    }
}
