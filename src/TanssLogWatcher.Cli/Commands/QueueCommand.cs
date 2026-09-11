using System.Globalization;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Cli.Output;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;

namespace TanssLogWatcher.Cli.Commands;

/// <summary>Was <c>queue</c> braucht.</summary>
public sealed record QueueInputs
{
    /// <summary>Die Warteschlange.</summary>
    public required IUploadQueue Queue { get; init; }

    /// <summary>Fernwartungen anlegen — und vor einer Wiederholung nachsehen.</summary>
    public required IRemoteSupportRepository RemoteSupports { get; init; }

    /// <summary>Das Änderungsprotokoll; <c>null</c> schaltet es ab.</summary>
    public SessionLog? Log { get; init; }

    /// <summary>Die Uhr; für Tests einsetzbar.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Wie viele Einträge ein Durchgang höchstens sendet.</summary>
    public int BatchSize { get; init; } = 25;

    /// <summary>
    /// Ab wann ein Eintrag auf <c>sending</c> als hängengeblieben gilt.
    /// </summary>
    /// <remarks>
    /// Grosszügig: Ein Upload dauert Sekunden, nicht Minuten. Fünf Minuten heissen also
    /// zuverlässig „der Prozess ist weg“ und nicht „er ist noch dran“ — und ein zu früh
    /// zurückgeholter Eintrag wäre der direkte Weg zur Dublette.
    /// </remarks>
    public TimeSpan StuckAfter { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Zeigt die Warteschlange und sendet auf Wunsch die fälligen Einträge.
/// </summary>
/// <remarks>
/// <para><b>Die eine Regel, um die sich hier alles dreht: TANSS dedupliziert nicht.</b> Ein
/// zweiter POST mit derselben <c>remoteMaintenanceId</c> erzeugt nachweislich einen zweiten
/// Datensatz (11.09.2026, IDs 38584 und 38585). Deshalb wird vor jeder Wiederholung erst
/// gefragt, ob die Fernwartung nicht längst drinsteht.</para>
///
/// <para><b>„Unbekannt“ heisst nicht „nicht vorhanden“.</b> Wirft die Existenzprüfung, weil
/// TANSS eine unerwartete Antwortform liefert, dann ist gerade nicht geklärt, ob der Datensatz
/// da ist. Hochzuladen wäre in diesem Zustand genau der Fehler, gegen den die Prüfung
/// aufgestellt wurde. Der Eintrag wird deshalb zurückgestellt — und nach Hausregel 5 läuft der
/// Durchgang mit den übrigen weiter.</para>
/// </remarks>
public static class QueueCommand
{
    /// <summary>Führt den Befehl aus.</summary>
    /// <param name="inputs">Die Bausteine.</param>
    /// <param name="flush">Fällige Einträge senden statt nur anzeigen.</param>
    /// <param name="writer">Wohin die Ausgabe geht.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>0 wenn nichts anliegt, 1 wenn etwas Aufmerksamkeit verlangt.</returns>
    public static async Task<int> RunAsync(QueueInputs inputs, bool flush, TextWriter writer,
                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(writer);

        int result = ExitCode.Healthy;

        if (flush)
        {
            result = ExitCode.Worse(result,
                await FlushAsync(inputs, writer, ct).ConfigureAwait(false));
            writer.WriteLine();
        }

        return ExitCode.Worse(result, Show(inputs.Queue, writer));
    }

    /// <summary>Zeigt die Warteschlange nach Zuständen.</summary>
    private static int Show(IUploadQueue queue, TextWriter writer)
    {
        writer.WriteLine("Warteschlange");
        writer.WriteLine();

        int result = ExitCode.Healthy;

        foreach (QueueState state in new[]
                 { QueueState.Pending, QueueState.Sending, QueueState.Failed })
        {
            // Count zaehlt ueber SQL und sieht jede Zeile; List ueberspringt die unlesbaren.
            // Beide Zahlen stehen nebeneinander, weil sie nur bis zur Obergrenze uebereinstimmen.
            int total = queue.Count(state);
            QueueListing listing = queue.List(state);

            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{Title(state)}: {total} — angezeigt {listing.Items.Count}, "
                + $"geprüfte Zeilen {listing.Examined}"));

            foreach (QueuedUpload item in listing.Items)
            {
                WriteEntry(writer, item);
            }

            if (listing.UnreadableNotice is { } notice)
            {
                foreach (string line in Report.Wrap(notice, 92))
                {
                    writer.WriteLine("  " + line);
                }

                result = ExitCode.Worse(result, ExitCode.Warning);
            }

            if (state == QueueState.Failed && total > 0)
            {
                result = ExitCode.Worse(result, ExitCode.Warning);
            }

            writer.WriteLine();
        }

        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Abgeschlossen und noch aufbewahrt: {queue.Count(QueueState.Done)}. Diese Zeilen "
            + $"bleiben mit Absicht stehen — sie sind der Beleg, dass die Fernwartung bei TANSS "
            + $"steht, und verhindern eine zweite Aufnahme derselben Sitzung."));

        return result;
    }

    /// <summary>Eine Zeile je Eintrag, mit Zustand, Versuchen, nächstem Versuch und Grund.</summary>
    private static void WriteEntry(TextWriter writer, QueuedUpload item)
    {
        string unknown = item.OutcomeUnknown ? " [Ausgang ungeklärt]" : string.Empty;

        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {item.RemoteMaintenanceId}  Typ {item.Payload.TypeId}  "
            + $"Versuche {item.Attempts}  nächster Versuch {Report.Moment(item.NextAttemptAt)}"
            + $"{unknown}"));

        if (!string.IsNullOrWhiteSpace(item.Payload.Comment))
        {
            writer.WriteLine("      " + Report.Ellipsis(item.Payload.Comment, 88));
        }

        if (item.LastError is { Length: > 0 } error)
        {
            foreach (string line in Report.Wrap(error, 86))
            {
                writer.WriteLine("      ! " + line);
            }
        }
    }

    /// <summary>
    /// Sendet die fälligen Einträge.
    /// </summary>
    /// <remarks>
    /// Zuerst werden hängengebliebene Einträge zurückgeholt. Sie kommen mit
    /// <see cref="QueuedUpload.OutcomeUnknown"/> zurück — das Kennzeichen, an dem gleich darauf
    /// die Existenzprüfung hängt.
    /// </remarks>
    private static async Task<int> FlushAsync(QueueInputs inputs, TextWriter writer,
                                              CancellationToken ct)
    {
        int requeued = inputs.Queue.RequeueStuck(inputs.StuckAfter);
        if (requeued > 0)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{requeued} Eintrag/Einträge standen auf „unterwegs“, ohne dass je eine "
                + $"Rückmeldung kam (Absturz oder Neustart). Sie sind zurückgeholt; ihr Ausgang "
                + $"gilt als ungeklärt und wird vor dem Senden geprüft."));
        }

        IReadOnlyList<QueuedUpload> leased = inputs.Queue.Lease(inputs.BatchSize);
        if (leased.Count == 0)
        {
            writer.WriteLine("Nichts fällig.");
            return ExitCode.Healthy;
        }

        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{leased.Count} fällige Einträge werden gesendet."));

        int result = ExitCode.Healthy;
        foreach (QueuedUpload item in leased)
        {
            ct.ThrowIfCancellationRequested();

            // Hausregel 5: Der Fehler eines einzelnen Vorgangs bricht niemals den Durchlauf ab.
            // Ohne diesen Riegel koennte ein einziger kaputter Eintrag jede weitere Sitzung in
            // der Warteschlange festhalten - also genau das verhindern, wozu sie da ist.
            try
            {
                result = ExitCode.Worse(result,
                    await SendOneAsync(inputs, item, writer, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Defer(inputs, item,
                    "Unerwarteter Fehler beim Senden: " + Redaction.Scrub(ex.Message));
                writer.WriteLine($"  {item.RemoteMaintenanceId}: zurückgestellt (unerwarteter "
                    + "Fehler). " + Redaction.Scrub(ex.Message));
                result = ExitCode.Worse(result, ExitCode.Warning);
            }
        }

        return result;
    }

    /// <summary>Ein einzelner Eintrag: prüfen, senden, vermerken.</summary>
    private static async Task<int> SendOneAsync(QueueInputs inputs, QueuedUpload item,
                                                TextWriter writer, CancellationToken ct)
    {
        // Geprueft wird bei ungeklaertem Ausgang - und ebenso bei jedem Eintrag, der schon
        // einmal draussen war. Die Warteschlange kennzeichnet nur den Absturz zwischen
        // Zuteilung und Rueckmeldung; eine Zeitueberschreitung MITTEN im POST sieht sie nicht,
        // laesst aber genauso offen, ob TANSS den Datensatz angenommen hat. Die Abfrage kostet
        // einen Aufruf, die Dublette kostet eine falsche Rechnung beim Kunden.
        if (item.OutcomeUnknown || item.Attempts > 0)
        {
            bool exists;
            try
            {
                exists = await inputs.RemoteSupports.ExistsAsync(item.Payload, ct)
                    .ConfigureAwait(false);
            }
            catch (TanssException ex)
            {
                // „Unbekannt“, nicht „nicht vorhanden“: In diesem Zustand darf nicht
                // hochgeladen werden, sonst entsteht genau die Dublette, die TANSS nicht
                // abfaengt. Der Eintrag wird zurueckgestellt, der Durchlauf laeuft weiter.
                Defer(inputs, item,
                    "Die Existenzprüfung ist fehlgeschlagen; ob die Fernwartung schon in TANSS "
                    + "steht, ist damit ungeklärt. Es wird nicht gesendet — TANSS dedupliziert "
                    + "nicht. Meldung: " + Redaction.Scrub(ex.Message));

                writer.WriteLine($"  {item.RemoteMaintenanceId}: zurückgestellt — Existenzprüfung "
                    + "fehlgeschlagen, Ausgang ungeklärt.");
                return ExitCode.Warning;
            }

            if (exists)
            {
                inputs.Queue.MarkDone(item.RemoteMaintenanceId);
                Log(inputs, item, SessionOutcome.Skipped,
                    "Bereits in TANSS vorhanden; nicht erneut gesendet.");
                writer.WriteLine($"  {item.RemoteMaintenanceId}: steht bereits in TANSS — "
                    + "abgehakt, nicht erneut gesendet.");
                return ExitCode.Healthy;
            }
        }

        try
        {
            RemoteSupportRead created = await inputs.RemoteSupports
                .CreateAsync(item.Payload, ct).ConfigureAwait(false);

            inputs.Queue.MarkDone(item.RemoteMaintenanceId);
            Log(inputs, item, SessionOutcome.Ok, "Hochgeladen.", created.Id);
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {item.RemoteMaintenanceId}: hochgeladen (TANSS-ID {created.Id})."));
            return ExitCode.Healthy;
        }
        catch (TanssException ex)
        {
            if (RetryPolicy.IsTransient(ex))
            {
                Defer(inputs, item, Redaction.Scrub(ex.Message));
                writer.WriteLine($"  {item.RemoteMaintenanceId}: zurückgestellt — "
                    + Redaction.Scrub(ex.Message));
                return ExitCode.Warning;
            }

            // Kein Warten heilt das: ein Typ, den es in TANSS nicht gibt, ein nicht
            // lizenziertes Modul, ein abgelaufenes Token. Aufgeben heisst hier nicht
            // wegwerfen - die Zeile bleibt mit Grund stehen und ist von Hand zu klaeren.
            inputs.Queue.MarkFailed(item.RemoteMaintenanceId,
                Redaction.Scrub(ex.Message) + " Dieser Fehler heilt nicht durch Warten; der "
                + "Eintrag wird nicht mehr von selbst versucht.", nextAttempt: null);
            Log(inputs, item, SessionOutcome.Error, Redaction.Scrub(ex.Message));
            writer.WriteLine($"  {item.RemoteMaintenanceId}: aufgegeben — "
                + Redaction.Scrub(ex.Message));
            return ExitCode.Warning;
        }
    }

    /// <summary>Stellt einen Eintrag mit Rückstau zurück.</summary>
    private static void Defer(QueueInputs inputs, QueuedUpload item, string reason)
    {
        inputs.Queue.MarkFailed(item.RemoteMaintenanceId, reason,
            Backoff.NextAttemptAfter(inputs.Clock.GetUtcNow(), item.Attempts + 1));
        Log(inputs, item, SessionOutcome.Deferred, reason);
    }

    /// <summary>
    /// Schreibt einen Eintrag ins Änderungsprotokoll, falls eines geführt wird.
    /// </summary>
    /// <remarks>
    /// Der Begründungstext läuft in <see cref="SessionLog"/> ohnehin durch
    /// <see cref="Redaction"/>; deutsche Prosa bleibt dabei stehen, Geheimnisse nicht.
    /// </remarks>
    private static void Log(QueueInputs inputs, QueuedUpload item, SessionOutcome outcome,
                            string reason, int? tanssSupportId = null)
    {
        if (inputs.Log is not { } log)
        {
            return;
        }

        _ = log.Append(new SessionLogEntry
        {
            Operation = "queue.flush",
            Outcome = outcome,
            Reason = reason,
            Trigger = SessionTrigger.Retry,
            RemoteMaintenanceId = item.RemoteMaintenanceId,
            TanssSupportId = tanssSupportId,
        });
    }

    /// <summary>Die Überschrift zu einem Zustand.</summary>
    private static string Title(QueueState state) => state switch
    {
        QueueState.Pending => "Ausstehend",
        QueueState.Sending => "Unterwegs",
        QueueState.Failed => "Aufgegeben",
        _ => "Abgeschlossen",
    };
}
