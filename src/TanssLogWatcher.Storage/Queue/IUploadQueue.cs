using System.Diagnostics.CodeAnalysis;
using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.Storage.Queue;

/// <summary>
/// Die Warteschlange noch nicht hochgeladener Fernwartungen.
/// </summary>
/// <remarks>
/// <para>Ohne eine solche Warteschlange kostet ein Netzfehler die Sitzung ersatzlos — kein
/// Hinweis, kein zweiter Versuch, die Arbeit ist fort. Deshalb gibt es sie.</para>
///
/// <para><b>Sie ist der Fehlerpfad und nicht der Regelweg.</b> Das Fensterprogramm bucht eine
/// beendete Sitzung unmittelbar aus dem Abschlussdialog heraus; hier landet sie erst, wenn das
/// misslingt, wenn der Techniker „Später“ wählt oder wenn niemand antwortet und das Werkzeug
/// zugeht. Die Kommandozeile, die niemanden fragen kann, reiht dagegen weiterhin jede
/// abgeschlossene Sitzung ein. Eine Warteschlange mit Einträgen heisst deshalb im
/// Fensterprogramm: Da hängt etwas.</para>
///
/// <para><b>Der Ablauf ist immer derselbe:</b> <see cref="Enqueue"/>, <see cref="Lease"/> holt
/// fällige Einträge, dann entweder <see cref="MarkDone"/> oder <see cref="MarkFailed"/>. Ein
/// Eintrag, der zwischen Lease und Rückmeldung verlorengeht — Absturz, Stromausfall —, kommt
/// über <see cref="RequeueStuck"/> zurück.</para>
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
    /// <param name="awaitDecision">
    /// Die Zeile wartet auf die Entscheidung des Technikers und wird von <see cref="Lease"/>
    /// <b>nie</b> zugeteilt, gleich wie viel Zeit vergeht.
    /// <para><b>Wozu — und wer es heute noch setzt.</b> Beim Ende einer Sitzung entsteht keine
    /// Zeile mehr; der Abschlussdialog hält sie und bucht sie selbst. Übrig bleiben drei
    /// Aufrufer, und alle drei haben denselben Grund: Die Sitzung muss gesichert werden,
    /// <b>obwohl</b> niemand entschieden hat. Das geordnete Beenden (der Dialog hätte kein
    /// Fenster mehr), die Wiederherstellung nach einem Neustart (die Sitzung wird gleich erneut
    /// vorgelegt) und der Dialog selbst, wenn er ohne Antwort geschlossen wird. Ohne dieses
    /// Kennzeichen ginge in allen drei Fällen die automatische Beschreibung nach TANSS, ohne
    /// Firma, ohne Ticket, ohne Bericht — genau der Befund, der diesen Umbau ausgelöst
    /// hat.</para>
    /// <para><b>Ausdrücklich keine Frist.</b> Die Vorgängerfassung hielt den Eintrag fünf
    /// Minuten zurück und schickte ihn danach ungefragt mit der automatischen Beschreibung
    /// hinaus. Gemessen an der Zustandsdatenbank dieses Arbeitsplatzes ist das auch geschehen.
    /// Wer einen Zeitablauf einführt, führt genau das wieder ein.</para>
    /// <para>Aufgehoben wird das Warten allein durch eine Entscheidung: <see cref="Release"/>
    /// („Später“, „Jetzt senden“), <see cref="LeaseOne"/> („In TANSS buchen“) oder
    /// <see cref="Remove"/> („Verwerfen“). Bleibt sie aus — Absturz, Feierabend —, steht die
    /// Zeile am nächsten Tag noch da und der Dialog wird erneut angeboten.</para>
    /// <para>Ohne Angabe ist der Eintrag sofort fällig — so verhält sich die Kommandozeile,
    /// die niemanden fragt, und so verhält sich jede Zeile aus einer älteren Fassung.</para>
    /// </param>
    bool Enqueue(RemoteSupportWrite item, bool awaitDecision = false);

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
    /// <para><b>Eine Zeile mit <see cref="QueuedUpload.AwaitingDecision"/> wird nie
    /// zugeteilt.</b> Sie wartet auf den Abschlussdialog, und kein Zeitablauf hebt das auf —
    /// sonst ginge sie hinaus, während der Techniker noch entscheidet, worauf sie gebucht
    /// werden soll.</para>
    /// </remarks>
    IReadOnlyList<QueuedUpload> Lease(int max);

    /// <summary>
    /// Teilt genau einen wartenden Eintrag zu — der Weg, auf dem der Abschlussdialog
    /// unmittelbar bucht.
    /// </summary>
    /// <remarks>
    /// <para><b>Warum es das gibt.</b> Gebucht wird aus dem Dialog heraus und nicht über einen
    /// späteren Sendelauf; der Techniker soll die TANSS-Kennung sehen, solange er noch davor
    /// sitzt. Der Aufrufer benutzt danach denselben Sendeweg wie der Sendedienst, damit die
    /// Existenzprüfung gegen Dubletten nicht umgangen wird.</para>
    ///
    /// <para><b>Anders als <see cref="Lease"/> fragt diese Zuteilung nicht nach Fälligkeit
    /// und nicht nach <see cref="QueuedUpload.AwaitingDecision"/></b> — der Klick ist die
    /// Entscheidung und hebt das Warten auf. Am Zustand ändert das nichts: Die Zeile geht auf
    /// <see cref="QueueState.Sending"/> und muss mit <see cref="MarkDone"/> oder
    /// <see cref="MarkFailed"/> quittiert werden. Bleibt die Quittung aus, holt
    /// <see cref="RequeueStuck"/> sie zurück — mit ungeklärtem Ausgang und damit mit
    /// Existenzprüfung.</para>
    /// </remarks>
    /// <param name="remoteMaintenanceId">Der Eintrag.</param>
    /// <returns>
    /// Der zugeteilte Eintrag, oder <see langword="null"/>, wenn es ihn nicht gibt oder er
    /// nicht mehr wartet — dann ist er unterwegs, gesendet oder aufgegeben.
    /// </returns>
    /// <exception cref="StateDatabaseException">
    /// Die Nutzlast ist unlesbar. Die Zeile steht dann auf <see cref="QueueState.Failed"/>
    /// und wird nicht mehr von selbst versucht.
    /// </exception>
    QueuedUpload? LeaseOne(string remoteMaintenanceId);

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
    /// Gibt einen wartenden Eintrag für den Sendedienst frei.
    /// </summary>
    /// <remarks>
    /// <para>Die Entscheidung „Später“: Die Zeile geht mit dem Kommentar hinaus, der gerade in
    /// ihrer Nutzlast steht — üblicherweise die automatische Beschreibung. Ebenso die
    /// Schaltfläche „Jetzt senden“, die den Rückstau eines Fehlversuchs mit vorzieht.</para>
    /// <para>Hebt <see cref="QueuedUpload.AwaitingDecision"/> auf und setzt die Fälligkeit auf
    /// jetzt. Wirkt nur auf <c>pending</c> — was unterwegs ist, braucht keine Freigabe
    /// mehr.</para>
    /// </remarks>
    /// <param name="remoteMaintenanceId">Der Eintrag.</param>
    /// <returns><c>false</c>, wenn es ihn nicht gibt oder er nicht mehr wartet.</returns>
    bool Release(string remoteMaintenanceId);

    /// <summary>Liest einen einzelnen Eintrag. <c>null</c>, wenn unbekannt.</summary>
    QueuedUpload? Find(string remoteMaintenanceId);

    /// <summary>
    /// Hält einen wartenden Eintrag zurück: Er geht erst hinaus, wenn jemand entschieden hat.
    /// </summary>
    /// <remarks>
    /// <para>Das Gegenstück zu <see cref="Release"/>. Gebraucht wird es von „Später“ im
    /// Abschlussdialog: Der Klick heisst „nicht jetzt“ und soll die Sitzung <b>parken</b>, nicht
    /// hinausschicken.</para>
    /// <para><b>Das war einmal anders, und es hat verwirrt.</b> „Später“ gab die Zeile frei; sie
    /// war damit sofort fällig und ging im nächsten Takt hinaus — gemessen eine Sekunde nach
    /// dem Klick. In der Warteschlange war nichts zu sehen, weil nichts liegenblieb, und der
    /// Name der Schaltfläche behauptete das Gegenteil dessen, was geschah.</para>
    /// <para><b>Nur auf wartende Zeilen.</b> Was unterwegs, gesendet oder aufgegeben ist, lässt
    /// sich nicht mehr zurückhalten; die Bedingung steht in der Anweisung selbst.</para>
    /// </remarks>
    /// <param name="remoteMaintenanceId">Die Kennung.</param>
    /// <returns><see langword="true"/>, wenn eine wartende Zeile zurückgehalten wurde.</returns>
    bool Hold(string remoteMaintenanceId);

    /// <summary>
    /// Ersetzt die Nutzlast eines noch nicht gesendeten Eintrags.
    /// </summary>
    /// <remarks>
    /// <para>Der Weg für den Abschlussdialog, wenn er eine Sitzung aus einem früheren Lauf
    /// vorlegt: Deren Zeile steht schon, und Bericht, Ticket und Firma kommen hier hinein,
    /// <b>bevor</b> gesendet wird — bricht das Senden ab, steht der Text bereits auf der Platte.
    /// Für eine frisch beendete Sitzung gibt es nichts zu ändern; sie hat keine Zeile.</para>
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
