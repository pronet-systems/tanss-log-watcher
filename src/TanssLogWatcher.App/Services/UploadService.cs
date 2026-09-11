using System.Globalization;
using System.Runtime.Versioning;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;
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
public sealed class UploadService : PeriodicService
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

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

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
    /// Für die Schaltfläche „Jetzt senden“. Läuft über dieselbe Sperre wie der Takt: Zwei
    /// gleichzeitige Läufe teilten sich zwar die Einträge sauber auf, würden aber beide den
    /// Rückstau desselben misslungenen Eintrags fortschreiben.
    /// </remarks>
    /// <param name="ct">Abbruchmarke.</param>
    public async Task<UploadRunResult> FlushNowAsync(CancellationToken ct = default)
    {
        if (Context.Composition is not { } composition)
        {
            return UploadRunResult.None;
        }

        return await FlushAsync(composition, ct).ConfigureAwait(false);
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
            RemoteSupportRead created = await composition.RemoteSupports
                .CreateAsync(item.Payload, ct).ConfigureAwait(false);

            composition.Queue.MarkDone(item.RemoteMaintenanceId);
            Log(composition, item, SessionOutcome.Ok, "Hochgeladen.", created.Id);
            Context.ReportWorking();

            return new UploadReport(item.RemoteMaintenanceId, UploadOutcome.Uploaded,
                string.Create(CultureInfo.CurrentCulture,
                    $"Hochgeladen; TANSS führt sie unter der Kennung {created.Id}."),
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
