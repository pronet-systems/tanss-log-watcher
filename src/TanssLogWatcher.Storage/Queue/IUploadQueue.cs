using System.Diagnostics.CodeAnalysis;
using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.Storage.Queue;

/// <summary>
/// Die Warteschlange noch nicht hochgeladener Fernwartungen.
/// </summary>
/// <remarks>
/// <para>Der Teil, an dem das Original am deutlichsten scheitert: Es hatte gar keine
/// Warteschlange. Ein Netzfehler kostete die Sitzung ersatzlos — kein Hinweis, kein zweiter
/// Versuch, die Arbeit war fort. Hier liegt jede abgeschlossene Sitzung zuerst auf der Platte
/// und erst danach im Netz.</para>
///
/// <para><b>Der Ablauf ist immer derselbe:</b> <see cref="Enqueue"/> beim Abschluss der
/// Sitzung, <see cref="Lease"/> holt fällige Einträge, dann entweder
/// <see cref="MarkDone"/> oder <see cref="MarkFailed"/>. Ein Eintrag, der zwischen Lease und
/// Rückmeldung verlorengeht — Absturz, Stromausfall —, kommt über
/// <see cref="RequeueStuck"/> zurück.</para>
///
/// <para><b>Wiederholung nur nach Existenzprüfung.</b> TANSS dedupliziert nicht: Ein zweiter
/// POST mit derselben <c>remoteMaintenanceId</c> erzeugt nachweislich einen zweiten Datensatz.
/// Ein Eintrag, dessen Versand in eine Zeitüberschreitung lief, darf deshalb nie blind
/// wiederholt werden — der Aufrufer fragt erst <c>IRemoteSupportRepository.ExistsAsync</c>.
/// Diese Warteschlange kann das nicht für ihn tun; sie weiß nichts vom Netz.</para>
/// </remarks>
[SuppressMessage("Naming", "CA1711",
    Justification = "Die Endung „Queue“ ist hier die Sache selbst: eine Warteschlange, "
        + "keine Ableitung von System.Collections.Queue. Ein Deckname wie „UploadBuffer“ "
        + "verschleierte, worum es geht.")]
public interface IUploadQueue : IDisposable
{
    /// <summary>
    /// Nimmt eine abgeschlossene Sitzung auf. Ein bereits bekannter Schlüssel ändert nichts.
    /// </summary>
    /// <returns><c>true</c>, wenn die Zeile neu entstanden ist.</returns>
    /// <remarks>
    /// Die Vorkehrung gegen Doppelbuchungen beginnt hier. Auch eine bereits als <c>done</c>
    /// vermerkte Kennung wird nicht erneut aufgenommen: Ihre Zeile ist der Beleg, dass die
    /// Fernwartung bei TANSS steht.
    /// </remarks>
    /// <param name="item">Die Nutzlast.</param>
    /// <param name="hold">
    /// Eine Schonfrist, vor deren Ablauf der Sendedienst den Eintrag nicht anfasst.
    /// <para><b>Wozu.</b> Nach dem Ende einer Sitzung fragt die Oberfläche nach Kommentar und
    /// Ticket. Ohne diese Frist kann der Sendedienst den Eintrag verschicken, während der
    /// Techniker noch tippt — der Bericht wäre dann geschrieben und nicht angekommen. Die Frist
    /// steht in der Datenbank und überlebt deshalb auch einen Absturz mitten im Tippen.</para>
    /// <para>Ohne Angabe ist der Eintrag sofort fällig — so verhält sich die Kommandozeile,
    /// die niemanden fragt.</para>
    /// </param>
    bool Enqueue(RemoteSupportWrite item, TimeSpan? hold = null);

    /// <summary>
    /// Holt bis zu <paramref name="max"/> fällige Einträge und setzt sie auf
    /// <see cref="QueueState.Sending"/>.
    /// </summary>
    /// <remarks>
    /// Zuteilung und Zustandswechsel geschehen in einem Schritt. Ein Eintrag wird niemals
    /// zweimal ausgegeben, auch nicht an zwei gleichzeitige Aufrufer.
    /// <para>Zurück kommen die Einträge nach Fälligkeit geordnet, der längst überfällige
    /// zuerst. Bricht der Versand nach der Hälfte ab, ist damit wenigstens das Älteste
    /// fort — und nicht das, was die Datenbank zufällig zuerst gefunden hat.</para>
    /// <para><b>Eine unlesbare Zeile kostet nur sich selbst.</b> Sie geht mit sprechendem
    /// <c>last_error</c> auf <see cref="QueueState.Failed"/>, die übrigen werden
    /// ausgeliefert. Andernfalls hielte eine einzige zerstörte Nutzlast die ganze
    /// Warteschlange an — und damit den Baustein, der verhindern soll, dass eine Sitzung
    /// verlorengeht.</para>
    /// </remarks>
    IReadOnlyList<QueuedUpload> Lease(int max);

    /// <summary>Vermerkt den geglückten Upload.</summary>
    void MarkDone(string remoteMaintenanceId);

    /// <summary>
    /// Vermerkt einen misslungenen Versuch.
    /// </summary>
    /// <param name="remoteMaintenanceId">Der Eintrag.</param>
    /// <param name="errorText">Der Fehler, wie er dem Techniker angezeigt wird.</param>
    /// <param name="nextAttempt">
    /// Zeitpunkt des nächsten Versuchs, üblicherweise aus <see cref="Backoff"/>.
    /// <c>null</c> bedeutet <b>aufgeben</b>: Der Eintrag geht auf
    /// <see cref="QueueState.Failed"/> und wird nicht mehr von selbst versucht. Das ist die
    /// richtige Antwort auf einen Fehler, den kein Warten heilt — ein Fernwartungstyp, den
    /// es in TANSS nicht gibt, oder ein nicht lizenziertes Modul.
    /// </param>
    void MarkFailed(string remoteMaintenanceId, string errorText, DateTimeOffset? nextAttempt);

    /// <summary>
    /// Holt Einträge zurück, die länger als <paramref name="olderThan"/> auf
    /// <see cref="QueueState.Sending"/> stehen.
    /// </summary>
    /// <returns>Anzahl der zurückgeholten Einträge.</returns>
    /// <remarks>
    /// <para><c>sending</c> ohne Rückmeldung heißt: Der Prozess ist während des Sendens
    /// verschwunden. Ob TANSS die Fernwartung trotzdem bekommen hat, weiß hier niemand —
    /// deshalb kehrt der Eintrag mit erhöhtem Versuchszähler und Rückstau zurück.</para>
    ///
    /// <para>Der zurückgeholte Eintrag trägt <see cref="QueuedUpload.OutcomeUnknown"/>. Das
    /// ist das Kennzeichen, an dem der Aufrufer festmacht, ob er vor dem Wiederholen die
    /// Existenzprüfung stellen muss — ohne es wäre ein bereits angekommener Eintrag von
    /// einem nie gesendeten nicht zu unterscheiden, und TANSS dedupliziert nicht. Deutsche
    /// Prosa in <see cref="QueuedUpload.LastError"/> taugt dafür nicht, und
    /// <see cref="QueuedUpload.Attempts"/> ebenso wenig: Der Zähler steigt auch nach einer
    /// eindeutigen Ablehnung.</para>
    /// </remarks>
    int RequeueStuck(TimeSpan olderThan);

    /// <summary>
    /// Gibt einen zurückgehaltenen Eintrag sofort frei.
    /// </summary>
    /// <remarks>
    /// Das Gegenstück zur Schonfrist beim Einreihen: Hat der Techniker seinen Bericht
    /// geschrieben, soll der Eintrag nicht noch die restliche Frist danebenliegen. Wirkt nur
    /// auf <c>pending</c>.
    /// </remarks>
    /// <param name="remoteMaintenanceId">Der Eintrag.</param>
    /// <returns><c>false</c>, wenn es ihn nicht gibt oder er nicht mehr wartet.</returns>
    bool Release(string remoteMaintenanceId);

    /// <summary>Liest einen einzelnen Eintrag. <c>null</c>, wenn unbekannt.</summary>
    QueuedUpload? Find(string remoteMaintenanceId);

    /// <summary>
    /// Ersetzt die Nutzlast eines noch nicht gesendeten Eintrags.
    /// </summary>
    /// <remarks>
    /// <para>Der Weg für den Abschlussdialog: Die Sitzung wird beim Ende <b>sofort</b>
    /// eingereiht — sonst verlöre ein Absturz während des Tippens genau die Sitzung, die der
    /// Dialog gerade festhalten soll. Kommentar und Ticket kommen danach hier hinein.</para>
    ///
    /// <para><b>Wirkt ausschliesslich auf <c>pending</c>.</b> Ein Eintrag, der bereits
    /// unterwegs ist, darf nicht mehr verändert werden: Was TANSS gerade entgegennimmt, stünde
    /// sonst dort anders als hier. Ein bereits gesendeter erst recht nicht — nachträglich
    /// geändert würde die Nutzlast zum Beleg, der nicht mehr belegt, was gesendet wurde.</para>
    /// </remarks>
    /// <param name="payload">Die neue Nutzlast; ihre Kennung benennt den Eintrag.</param>
    /// <returns><c>false</c>, wenn es den Eintrag nicht gibt oder er nicht mehr wartet.</returns>
    bool UpdatePayload(RemoteSupportWrite payload);

    /// <summary>
    /// Entfernt einen noch nicht gesendeten Eintrag.
    /// </summary>
    /// <remarks>
    /// Das ausdrückliche Verwerfen aus dem Abschlussdialog. Auch das wirkt nur auf
    /// <c>pending</c>: Eine Sitzung, die schon bei TANSS ist, lässt sich hier nicht mehr
    /// zurückholen — der Eintrag wäre weg, der Datensatz dort bliebe.
    /// </remarks>
    /// <param name="remoteMaintenanceId">Der Eintrag.</param>
    /// <returns><c>false</c>, wenn es den Eintrag nicht gibt oder er nicht mehr wartet.</returns>
    bool Remove(string remoteMaintenanceId);

    /// <summary>
    /// Listet Einträge eines Zustands, jüngste zuerst — samt der Zahl der dabei
    /// übersprungenen unlesbaren Zeilen.
    /// </summary>
    /// <remarks>
    /// Die Rückgabe ist absichtlich keine schlichte Liste. Eine unlesbare Zeile wird
    /// übersprungen, damit eine einzige zerstörte Nutzlast nicht die ganze Anzeige kostet —
    /// aber sie zählt in <see cref="Count"/> mit, und eine Liste, die das verschweigt, stellt
    /// den Techniker vor „ausstehend: 4“ neben drei Einträgen.
    /// <see cref="QueueListing.UnreadableNotice"/> ist der Satz, den die Kommandozeile dazu
    /// ausgeben soll.
    /// </remarks>
    QueueListing List(QueueState state, int limit = 100);

    /// <summary>
    /// Zählt die Einträge eines Zustands, unlesbare eingeschlossen.
    /// </summary>
    /// <remarks>
    /// Gezählt wird über SQL, ohne die Nutzlast anzufassen — eine beschädigte Zeile ist eine
    /// wartende Fernwartung und darf aus dem Rückstau nicht verschwinden, nur weil niemand
    /// sie mehr lesen kann. Wie viele davon unlesbar sind, sagt
    /// <see cref="QueueListing.Unreadable"/>.
    /// </remarks>
    int Count(QueueState state);

    /// <summary>
    /// Entfernt abgeschlossene Einträge, die älter sind als <paramref name="olderThan"/>.
    /// </summary>
    /// <returns>Anzahl der entfernten Zeilen.</returns>
    /// <remarks>
    /// Die Aufbewahrungszeit ist zugleich das Zeitfenster, in dem eine Kennung nicht erneut
    /// aufgenommen werden kann. Sie darf deshalb nicht knapp sein: Erst wenn eine Sitzung
    /// zweifelsfrei abgeschlossen und niemand mehr auf sie zurückkommt, darf die Zeile fort.
    /// </remarks>
    int PurgeCompleted(TimeSpan olderThan);

    /// <summary>Legt eine laufende Sitzung an oder schreibt sie fort.</summary>
    void SaveOpenSession(OpenSession session);

    /// <summary>Entfernt eine laufende Sitzung, üblicherweise nach ihrem Abschluss.</summary>
    /// <returns><c>false</c>, wenn keine solche Sitzung vermerkt war.</returns>
    bool RemoveOpenSession(string remoteMaintenanceId);

    /// <summary>Liest alle laufenden Sitzungen. Der erste Griff nach einem Neustart.</summary>
    IReadOnlyList<OpenSession> LoadOpenSessions();
}
