using System.Globalization;
using TanssLogWatcher.Storage.History;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;

namespace TanssLogWatcher.Storage;

/// <summary>
/// Löscht in <c>state.db</c>, was seine Aufbewahrungsfrist überschritten hat: erledigte
/// Warteschlangeneinträge, Protokollzeilen und Verlaufszeilen — und schwärzt im Verlauf die
/// Gegenstelle, sobald deren kürzere Frist abgelaufen ist.
/// </summary>
/// <remarks>
/// <para><b>Das Gegenstück zu <see cref="Recordings.RecordingCleaner"/>, und aus demselben
/// Grund eine eigene Klasse:</b> Eine Löschfrist, die in einem Dienst versteckt ist, lässt sich
/// nur prüfen, indem man den Dienst laufen lässt. Genau so ist sie hier lange Zeit gar nicht
/// gelaufen — beide Aufrufe waren gebaut und geprüft, aber im Betrieb rief sie niemand.</para>
///
/// <para><b>Eine Frist für beide Tabellen.</b> <c>logging.retention_days</c> beantwortet die
/// Frage, wie lange dieses Werkzeug vergangene Sitzungen auf dem Rechner des Technikers behält.
/// Der erledigte Warteschlangeneintrag ist davon ausgerechnet der Teil, der die Gegenstelle im
/// Klartext trägt — die Nutzlast geht so nach TANSS hinaus und kennt keine Schwärzung. Ihn
/// länger zu behalten als das Protokoll hiesse, die Zusage an der unangenehmsten Stelle nicht
/// einzuhalten.</para>
///
/// <para><b>Warum der erledigte Eintrag überhaupt stehen bleibt:</b> Er ist der Riegel gegen
/// die Dublette. <see cref="IUploadQueue.Enqueue"/> nimmt eine bereits bekannte Kennung nicht
/// zweimal auf; stürzt das Werkzeug zwischen dem Hochladen und dem Austragen der laufenden
/// Sitzung ab, verhindert genau dieser Riegel, dass die Wiederherstellung dieselbe Fernwartung
/// ein zweites Mal einreiht. Dieses Fenster ist Sekunden lang — der kleinste zulässige Wert der
/// Frist ist ein Tag, und damit liegt zwischen beidem der Faktor Zehntausend.</para>
///
/// <para><b>Der Verlauf hat eigene Fristen und läuft deshalb neben, nicht unter der
/// gemeinsamen.</b> <c>history.retention_days</c> darf länger sein als
/// <c>logging.retention_days</c> — der Verlauf ist ja gerade das, was die gelöschte
/// Warteschlangenzeile überdauern soll —, und <c>history.plain_text_days</c> ist noch einmal
/// kürzer: Danach steht die Zeile weiter da, aber ohne den Klartext der Gegenstelle. Eine
/// Schwärzung, die nur beim Schreiben neuer Zeilen griffe, wäre keine Frist; sie gehört
/// deshalb in denselben Takt wie das Löschen.</para>
///
/// <para><b>Die Schritte hängen nicht aneinander</b> (Hausregel 5). Es sind verschiedene
/// Tabellen und verschiedene Gründe, warum eine davon gerade nicht zu schreiben ist; der
/// Fehlschlag des einen ist kein Grund, die übrigen ausfallen zu lassen.</para>
/// </remarks>
public sealed class StatePruner
{
    private readonly IUploadQueue _queue;
    private readonly SessionLog _log;
    private readonly HistoryRetention? _history;

    /// <summary>Baut den Aufräumer.</summary>
    /// <param name="queue">Die Warteschlange.</param>
    /// <param name="log">Das Änderungsprotokoll.</param>
    /// <param name="history">
    /// Der Verlauf samt seinen beiden eigenen Fristen, oder <c>null</c>. <c>null</c> heisst
    /// ausdrücklich <b>nicht</b> „nichts war fällig“, sondern „er hat an diesem Durchlauf
    /// nicht teilgenommen“ — das Ergebnis führt seine beiden Zahlen dann als nicht ermittelt.
    /// </param>
    public StatePruner(IUploadQueue queue, SessionLog log, HistoryRetention? history = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(log);

        _queue = queue;
        _log = log;
        _history = history;
    }

    /// <summary>Ein Durchlauf.</summary>
    /// <param name="retention">
    /// Die eingestellte Aufbewahrungsdauer für Warteschlange und Protokoll
    /// (<c>logging.retention_days</c>). Die Fristen des Verlaufs stehen am mitgegebenen
    /// <see cref="HistoryRetention"/> und nicht hier — es sind andere Zahlen.
    /// </param>
    /// <returns>Was der Durchlauf getan hat.</returns>
    public PruneResult Run(TimeSpan retention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        (int queued, bool queueFailed) = Step(() => _queue.PurgeCompleted(retention));
        (int logged, bool logFailed) = Step(() => _log.Prune(retention));

        int failed = (queueFailed ? 1 : 0) + (logFailed ? 1 : 0);

        if (_history is not { } history)
        {
            return new PruneResult(retention, queued, logged, null, null, failed, Steps: 2);
        }

        // Erst schwaerzen, dann loeschen. Umgekehrt waere es ein paar Schreibvorgaenge
        // billiger - aber misslingt dann das Loeschen, bliebe der Klartext der Gegenstelle
        // ueber seine eigene, kuerzere Frist hinaus stehen. Das ist der Wert, der hier zaehlt.
        (int redacted, bool redactFailed) =
            Step(() => history.Store.Redact(history.PlainText));
        (int removed, bool pruneFailed) =
            Step(() => history.Store.Prune(history.Retention));

        failed += (redactFailed ? 1 : 0) + (pruneFailed ? 1 : 0);

        return new PruneResult(retention, queued, logged, removed, redacted, failed, Steps: 4);
    }

    /// <summary>
    /// Führt einen Löschschritt aus und meldet seinen Fehlschlag, statt ihn zu werfen.
    /// </summary>
    /// <remarks>
    /// Breit gefangen mit Absicht: Was hier ankommt, ist eine gesperrte oder geschlossene
    /// Datenbank. Eine nicht gelöschte Zeile von vorgestern ist kein Grund, den Takt abbrechen
    /// zu lassen, in dem dieser Durchlauf mitläuft — und der nächste Durchlauf holt sie nach.
    /// </remarks>
    private static (int Removed, bool Failed) Step(Func<int> step)
    {
        try
        {
            return (step(), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (0, true);
        }
    }
}

/// <summary>
/// Der Verlauf und seine beiden Fristen, für einen Durchlauf zusammengefasst.
/// </summary>
/// <remarks>
/// Die beiden Zeitspannen stehen hier und nicht an <see cref="StatePruner.Run"/>, weil sie mit
/// der Frist für Warteschlange und Protokoll nichts zu tun haben: Sie stammen aus einem
/// anderen Konfigurationsabschnitt und dürfen andere Zahlen sein. Ein einziger
/// Frist-Parameter für alles hätte genau das verwischt.
/// </remarks>
/// <param name="Store">Der Verlauf.</param>
/// <param name="Retention">
/// <c>history.retention_days</c> — wann eine Verlaufszeile <b>frühestens</b> fortkommt.
/// Frühestens, denn solange noch eine Aufzeichnung zu dieser Sitzung liegt, bleibt sie stehen.
/// </param>
/// <param name="PlainText">
/// <c>history.plain_text_days</c> — wann der Klartext der Gegenstelle aus der Zeile
/// verschwindet. Die Zeile selbst bleibt und trägt danach nur noch den Abdruck.
/// </param>
public sealed record HistoryRetention(SessionHistoryStore Store, TimeSpan Retention,
                                      TimeSpan PlainText);

/// <summary>Was ein Durchlauf getan hat.</summary>
/// <param name="Retention">Die Frist, nach der Warteschlange und Protokoll gelöscht wurden.</param>
/// <param name="QueueEntries">Wie viele erledigte Warteschlangeneinträge fortkamen.</param>
/// <param name="LogEntries">Wie viele Protokollzeilen fortkamen.</param>
/// <param name="HistoryEntries">
/// Wie viele Verlaufszeilen fortkamen; <c>null</c>, wenn der Verlauf an diesem Durchlauf nicht
/// teilgenommen hat. <c>null</c> und <c>0</c> sind ausdrücklich zweierlei — nicht ermittelt
/// ist keine Null.
/// </param>
/// <param name="HistoryRedacted">
/// Bei wie vielen Verlaufszeilen die Gegenstelle geschwärzt wurde; <c>null</c> wie oben.
/// </param>
/// <param name="FailedSteps">Wie viele Schritte misslangen.</param>
/// <param name="Steps">
/// Wie viele Schritte überhaupt anstanden — zwei ohne, vier mit Verlauf. Die Zahl steht dabei,
/// damit der Satz „eins von vier misslang“ sie nicht raten muss.
/// </param>
public readonly record struct PruneResult(TimeSpan Retention, int QueueEntries, int LogEntries,
                                          int? HistoryEntries, int? HistoryRedacted,
                                          int FailedSteps, int Steps)
{
    /// <summary>Hat der Durchlauf überhaupt etwas getan?</summary>
    public bool DidAnything => QueueEntries > 0 || LogEntries > 0 || HistoryEntries > 0
        || HistoryRedacted > 0 || FailedSteps > 0;

    /// <summary>Ein Satz für die Anzeige und das Protokoll.</summary>
    public string Summary => !DidAnything
        ? "Nichts fällig."
        : string.Create(CultureInfo.CurrentCulture,
            $"Aufbewahrungsfrist {Retention.TotalDays:0} Tage: {QueueEntries} erledigte "
            + $"Warteschlangeneinträge und {LogEntries} Protokollzeilen entfernt.")
          + HistorySentence
          + (FailedSteps > 0
              ? string.Create(CultureInfo.CurrentCulture,
                  $" {FailedSteps} von {Steps} Schritten misslang; beim nächsten Durchlauf "
                  + $"steht dasselbe wieder an.")
              : string.Empty);

    /// <summary>Der Satzteil zum Verlauf; leer, wenn er nicht teilgenommen hat.</summary>
    private string HistorySentence =>
        HistoryEntries is { } removed && HistoryRedacted is { } redacted
            ? string.Create(CultureInfo.CurrentCulture,
                $" Verlauf: {removed} Zeilen entfernt, bei {redacted} die Gegenstelle "
                + $"geschwärzt.")
            : string.Empty;
}
