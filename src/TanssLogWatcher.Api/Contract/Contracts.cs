using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.Api.Contract;

/// <summary>
/// Woher das Arbeitstoken kommt und wohin das erneuerte geht.
/// </summary>
/// <remarks>
/// Bewusst eine Schnittstelle: die Netzschicht soll nichts über DPAPI, Dateipfade oder Windows
/// wissen. Die Umsetzung liegt in <c>TanssLogWatcher.Storage</c>.
/// </remarks>
public interface ITokenStore
{
    /// <summary>Liefert das Token einschließlich des Präfixes <c>Bearer </c>.</summary>
    string Read();

    /// <summary>
    /// Ersetzt das Token. Die Umsetzung legt vorher eine Sicherung des bisherigen an — ein
    /// misslungener Wechsel darf nicht bedeuten, dass gar kein Token mehr da ist.
    /// </summary>
    void Write(string token);
}

/// <summary>Reiner HTTP-Zugriff auf TANSS. Kennt keine Fachlogik.</summary>
/// <remarks>
/// <para><b>Zwei Präfixe, eine Pflicht.</b> Auf <c>/api/v1/**</c> ist <c>loggedInUserId</c>
/// zwingend — ohne ihn antwortet TANSS mit 403; nachgemessen an <c>GET /api/v1/timers</c>.
/// Auf <c>/api/tanss.x/v1/**</c> wird er dagegen schlicht ignoriert: derselbe Aufruf antwortet
/// mit und ohne ihn mit 200.</para>
/// <para><b>Frühere Fassungen behaupteten hier, er dürfe auf <c>tanss.x</c> „niemals"
/// mitgeschickt werden.</b> Das ist nachgemessen falsch und stand als Ursache Nummer eins in
/// der Meldung zu jeder 403 — es schickte den Techniker auf eine Fährte, die es nicht gibt.
/// Die Umsetzung setzt den Parameter weiterhin selbsttätig nur auf <c>/api/v1</c>: dort ist er
/// nötig, hier ist er überflüssig, und Überflüssiges gehört nicht auf die Leitung.</para>
/// </remarks>
public interface ITanssClient : IDisposable
{
    /// <summary>
    /// Liest. Wird bei Netzfehlern und 5xx wiederholt — <b>außer</b> auf den Pfaden, die
    /// <see cref="TanssRoutes.HasSideEffectOnGet"/> als seiteneffektbehaftet kennt; die laufen
    /// mit genau einem Versuch, weil dort jeder Versuch serverseitig etwas anlegt.
    /// </summary>
    Task<T?> GetAsync<T>(string path, IDictionary<string, string?>? query = null,
                         CancellationToken ct = default);

    Task<T?> PutAsync<T>(string path, object? body = null,
                         IDictionary<string, string?>? query = null,
                         CancellationToken ct = default);

    Task<T?> PostAsync<T>(string path, object? body = null,
                          IDictionary<string, string?>? query = null,
                          CancellationToken ct = default);

    Task DeleteAsync(string path, IDictionary<string, string?>? query = null,
                     CancellationToken ct = default);

    /// <summary>
    /// Wie <see cref="PostAsync{T}"/>, liefert zusätzlich den <c>meta</c>-Block. Der trägt die
    /// <c>linkedEntities</c> und ist beim Anlegen einer Fernwartung der Nachweis, dass die
    /// Attribution serverseitig gegriffen hat.
    /// </summary>
    Task<(T? Content, IReadOnlyDictionary<string, object?> Meta)> PostWithMetaAsync<T>(
        string path, object? body = null, IDictionary<string, string?>? query = null,
        CancellationToken ct = default);
}

/// <summary>
/// Lesen <b>mit</b> dem <c>meta</c>-Block.
/// </summary>
/// <remarks>
/// <para><b>Warum es diesen Weg braucht.</b> <see cref="ITanssClient.GetAsync{T}"/> und
/// <see cref="ITanssClient.PutAsync{T}"/> geben nur den <c>content</c> heraus und werfen den
/// Umschlag weg. Der Name zu einer Kennung steht aber ausschließlich im Umschlag: nachgemessen
/// am 13.09.2026 trägt <c>GET /api/v1/tickets/{id}</c> die <c>companyId</c> im Inhalt und den
/// Firmennamen nur unter <c>meta.linkedEntities.companies</c>. Wer ohne diesen Weg arbeitet,
/// kann eine Firmenkennung nur als Zahl anzeigen.</para>
/// <para><b>Warum eine eigene Schnittstelle und keine neuen Glieder auf
/// <see cref="ITanssClient"/>.</b> Dieselbe Überlegung wie bei <c>ITanssBodyDelete</c>: der
/// Grundvertrag bleibt schmal, damit ihn eine Attrappe in drei Zeilen erfüllt. Wer den
/// Umschlag braucht, prüft auf diese Schnittstelle; wer nicht, merkt nichts davon. Die
/// bestehenden Wege bleiben unberührt — <see cref="ITanssClient.GetAsync{T}"/> verhält sich
/// Zeichen für Zeichen wie zuvor.</para>
/// <para><b>Der <c>meta</c>-Block bleibt roh.</b> Seine Werte kommen als <c>JsonElement</c>
/// durch, genau wie bei <see cref="ITanssClient.PostWithMetaAsync{T}"/>. Ausgewertet wird er
/// von <c>LinkedEntities</c>, nicht hier: jede feste Modellierung wäre nach dem nächsten
/// TANSS-Update falsch.</para>
/// </remarks>
public interface ITanssMetaRead
{
    /// <summary>
    /// Liest wie <see cref="ITanssClient.GetAsync{T}"/> und gibt zusätzlich <c>meta</c> heraus.
    /// </summary>
    /// <remarks>
    /// Wiederholt nach denselben Regeln wie <see cref="ITanssClient.GetAsync{T}"/> — also
    /// nicht auf den Pfaden, die <see cref="TanssRoutes.HasSideEffectOnGet"/> kennt.
    /// </remarks>
    /// <typeparam name="T">Das erwartete Modell des <c>content</c>.</typeparam>
    /// <param name="path">Der Pfad, mit Präfix.</param>
    /// <param name="query">Abfrageparameter; <c>loggedInUserId</c> setzt die Umsetzung selbst.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>Inhalt und <c>meta</c>; der <c>meta</c>-Teil ist nie <see langword="null"/>.</returns>
    Task<(T? Content, IReadOnlyDictionary<string, object?> Meta)> GetWithMetaAsync<T>(
        string path, IDictionary<string, string?>? query = null, CancellationToken ct = default);

    /// <summary>
    /// Schickt wie <see cref="ITanssClient.PutAsync{T}"/> und gibt zusätzlich <c>meta</c> heraus.
    /// </summary>
    /// <remarks>
    /// Für die lesenden Abfragen, die TANSS als PUT verlangt — die Firmensuche
    /// <c>PUT /api/v1/search</c> und die Ticketsuche <c>PUT /api/v1/tickets</c>. Läuft wie
    /// <see cref="ITanssClient.PutAsync{T}"/> mit genau einem Versuch; wer wiederholen will,
    /// tut es bewusst selbst.
    /// </remarks>
    /// <typeparam name="T">Das erwartete Modell des <c>content</c>.</typeparam>
    /// <param name="path">Der Pfad, mit Präfix.</param>
    /// <param name="body">Der Rumpf; wird über seinen Laufzeittyp serialisiert.</param>
    /// <param name="query">Abfrageparameter; <c>loggedInUserId</c> setzt die Umsetzung selbst.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>Inhalt und <c>meta</c>; der <c>meta</c>-Teil ist nie <see langword="null"/>.</returns>
    Task<(T? Content, IReadOnlyDictionary<string, object?> Meta)> PutWithMetaAsync<T>(
        string path, object? body = null, IDictionary<string, string?>? query = null,
        CancellationToken ct = default);
}

/// <summary>
/// Leistungen: aus einem Timer vorbereiten und anlegen.
/// </summary>
/// <remarks>
/// <para><b>Zwei Schritte, und das ist keine Umständlichkeit.</b> TANSS belegt beim ersten
/// Schritt über achtzig Felder vor — darunter Stundensatz und Abrechnungsart, die sich aus
/// Kunde, Vertrag und Mitarbeiter ergeben und die dieses Werkzeug weder kennt noch kennen
/// sollte. Wer den zweiten Schritt ohne den ersten ginge, buchte eine Leistung, die
/// rechnerisch nichts wert ist.</para>
/// <para>Dazwischen liegt der Dialog: Der Techniker beschreibt, was er in jedem Zeitabschnitt
/// getan hat. Mehr wird nicht verändert.</para>
/// </remarks>
public interface ISupportRepository
{
    /// <summary>
    /// Lässt TANSS eine Leistung aus einem Timer vorbereiten. Legt nichts an.
    /// </summary>
    /// <param name="timerId">Der Timer.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>Die vorbelegte Leistung samt einem Abschnitt je Timerlauf.</returns>
    Task<SupportDraft> PrepareFromTimerAsync(int timerId, CancellationToken ct = default);


    /// <summary>
    /// Legt die Leistung an.
    /// </summary>
    /// <remarks>
    /// <b>Es gibt kein Zurück.</b> Eine gebuchte Leistung ist in TANSS zu korrigieren, nicht
    /// von hier aus. Die Oberfläche fragt deshalb vorher.
    /// </remarks>
    /// <param name="draft">Die vorbereitete und ausgefüllte Leistung.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>Die Kennung der angelegten Leistung; 0, wenn TANSS keine genannt hat.</returns>
    Task<int> CreateAsync(SupportDraft draft, CancellationToken ct = default);
}

/// <summary>Fernwartungen lesen und schreiben.</summary>
public interface IRemoteSupportRepository
{
    /// <summary>
    /// Legt eine Fernwartung an.
    /// </summary>
    /// <remarks>
    /// <b>TANSS dedupliziert nicht.</b> Ein zweiter Aufruf mit derselben
    /// <c>remoteMaintenanceId</c> erzeugt nachweislich einen zweiten Datensatz (geprüft am
    /// 11.09.2026, IDs 38584 und 38585). Wiederholungen nach einer Zeitüberschreitung deshalb
    /// niemals blind, sondern erst <see cref="ExistsAsync(RemoteSupportWrite, CancellationToken)"/> fragen.
    /// </remarks>
    Task<RemoteSupportRead> CreateAsync(RemoteSupportWrite item, CancellationToken ct = default);

    /// <summary>
    /// Legt eine Fernwartung an und meldet zusätzlich, ob TANSS sie dem erwarteten Mitarbeiter
    /// zugeordnet hat.
    /// </summary>
    /// <remarks>
    /// <para><b>Gehört auf die Schnittstelle und nicht nur in die Umsetzung.</b> Der Sendedienst
    /// rief lange <see cref="CreateAsync"/> — und warf damit die Gegenprobe weg, die die
    /// Umsetzung längst berechnet hatte. Eine Fernwartung, die bei TANSS auf einem fremden
    /// Mitarbeiter landet, ist falsch gebuchte Arbeitszeit, und sie fällt niemandem auf: Der
    /// Aufruf war erfolgreich, die Zeile steht, nur eben beim Falschen.</para>
    /// <para><see cref="CreateAsync"/> bleibt für Aufrufer, denen die Zuordnung gleichgültig
    /// ist — etwa eine reine Wiederholung nach bestandener Existenzprüfung.</para>
    /// </remarks>
    Task<RemoteSupportCreateResult> CreateWithDiagnosticsAsync(RemoteSupportWrite item,
                                                               CancellationToken ct = default);

    /// <summary>
    /// Prüft anhand der eigenen Sitzungskennung, ob ein Upload bereits angekommen ist.
    /// </summary>
    /// <remarks>
    /// Der Bezugszeitpunkt spannt ein festes Fenster von einem Tag nach beiden Seiten. Wer
    /// Anfang und Ende der Sitzung kennt, nimmt eine der Überladungen: eine Fernwartung, die
    /// länger läuft als das feste Fenster, würde hier sonst übersehen — und ein übersehener
    /// Datensatz wird beim nächsten Versuch zur Dublette.
    /// </remarks>
    Task<bool> ExistsAsync(string remoteMaintenanceId, DateTimeOffset around,
                           CancellationToken ct = default);

    /// <summary>
    /// Prüft im Fenster der Sitzung selbst, großzügig gepolstert gegen abweichende Serveruhren.
    /// </summary>
    Task<bool> ExistsAsync(string remoteMaintenanceId, DateTimeOffset sessionStart,
                           DateTimeOffset sessionEnd, CancellationToken ct = default);

    /// <summary>
    /// Prüft für eine konkrete Sitzung — der übliche Weg vor einem Wiederholungsversuch.
    /// </summary>
    /// <remarks>
    /// Nimmt Kennung und Zeitfenster aus demselben Datensatz, der gleich hochgeladen werden
    /// soll. Damit kann die Prüfung nicht an einem anderen Zeitraum vorbeigreifen als der
    /// Upload selbst.
    /// </remarks>
    Task<bool> ExistsAsync(RemoteSupportWrite session, CancellationToken ct = default);

    /// <summary>Listet Fernwartungen im Zeitfenster, wahlweise mit Textfilter.</summary>
    Task<IReadOnlyList<RemoteSupportRead>> ListAsync(Timeframe timeframe, string? text = null,
                                                     CancellationToken ct = default);

    /// <summary>Die in TANSS gepflegten externen Anbindungen.</summary>
    Task<IReadOnlyList<RemoteSupportSystem>> ListSystemsAsync(CancellationToken ct = default);
}

/// <summary>Tickets des angemeldeten Technikers.</summary>
public interface ITicketRepository
{
    Task<IReadOnlyList<Ticket>> ListOwnAsync(CancellationToken ct = default);

    /// <summary>
    /// Sucht die <b>offenen</b> Tickets eines Mitarbeiters.
    /// </summary>
    /// <remarks>
    /// <para>Der Weg zu einer Auswahl statt eines Zahlenfelds. Vorher musste der Techniker die
    /// Ticketnummer auswendig wissen, und eingetippt wurde jede Zahl angenommen — auch eine, zu
    /// der es kein Ticket gibt. Eine Leistung auf eine erfundene Nummer zu buchen ist
    /// schlimmer als gar kein Ticket: Sie taucht in keiner Auswertung auf und fällt niemandem
    /// auf.</para>
    /// <para>Erledigte Tickets bleiben aussen vor. Nachgemessen ergab das 20 statt 572
    /// Einträge — auf ein abgeschlossenes Ticket zu buchen ist fast immer ein Versehen.</para>
    /// </remarks>
    /// <param name="employeeId">Der Mitarbeiter, dessen Tickets gesucht werden.</param>
    /// <param name="ct">Abbruchmarke.</param>
    Task<IReadOnlyList<Ticket>> SearchOpenAsync(int employeeId, CancellationToken ct = default);

    /// <summary>
    /// Liest ein einzelnes Ticket — der Weg, eine eingetippte Nummer zu prüfen.
    /// </summary>
    /// <remarks>
    /// Gibt <see langword="null"/> zurück, wenn es die Kennung nicht gibt; TANSS antwortet dann
    /// mit 404. Das ist kein Fehler, sondern die Antwort auf die Frage.
    /// </remarks>
    /// <param name="ticketId">Die zu prüfende Kennung.</param>
    /// <param name="ct">Abbruchmarke.</param>
    Task<Ticket?> FindAsync(int ticketId, CancellationToken ct = default);

    /// <summary>
    /// Sucht die <b>offenen</b> Tickets einer Firma — und nennt dabei deren Namen.
    /// </summary>
    /// <remarks>
    /// <para>Der Weg, der im Abschlussdialog aus einer erkannten Firma eine Ticketauswahl macht.
    /// Gefiltert wird über <c>companies</c> und ausdrücklich <b>nicht</b> zusätzlich über
    /// <c>staff</c>: Wer eine Fernwartung für den Kollegen übernimmt, bucht auf dessen Ticket,
    /// und mit beiden Filtern zugleich stünde genau dieses nicht in der Liste.</para>
    /// <para><b>Wirft nicht</b> (Hausregel 5). Die Ticketauswahl ist eine Bequemlichkeit, die
    /// Buchung ist Arbeitszeit; ein Aussetzer der Leitung darf den Dialog nicht kosten. Ein
    /// Fehlschlag kommt als <see cref="CompanyTicketOutcome.Undetermined"/> samt fertigem Satz
    /// zurück — <see cref="SearchOpenAsync"/> wirft dagegen weiterhin, denn dort hängt kein
    /// Fenster daran.</para>
    /// </remarks>
    /// <param name="companyId">Die Firma. Muss grösser als 0 sein.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>Tickets, Firmenname und die Aussage, wie eine leere Liste zu lesen ist.</returns>
    Task<CompanyTickets> ListForCompanyAsync(int companyId, CancellationToken ct = default);
}

/// <summary>Timer des angemeldeten Technikers.</summary>
/// <remarks>
/// Die Routen dahinter sind undokumentiert und gegen eine Instanz der Fassung 10.10.0
/// ausgemessen. Zwei Eigenheiten prägen diese Schnittstelle: das Löschen nimmt die Kennung im
/// Rumpf, und Notizen hängen nicht am Timer, sondern an seinen Laufabschnitten.
/// </remarks>
public interface ITimerRepository
{
    Task<IReadOnlyList<TanssTimer>> ListAsync(CancellationToken ct = default);

    /// <summary>Liest einen einzelnen Timer.</summary>
    Task<TanssTimer> GetAsync(int timerId, CancellationToken ct = default);

    /// <summary>Legt einen Timer mit Titel und wahlweise Ticketbezug an.</summary>
    Task<TanssTimer> CreateAsync(string title, int ticketId = 0, CancellationToken ct = default);

    /// <summary>Legt einen Timer mit allen setzbaren Feldern an.</summary>
    Task<TanssTimer> CreateAsync(TimerDraft draft, CancellationToken ct = default);

    /// <summary>Startet oder hält an — TANSS kennt nur diesen einen Umschalter.</summary>
    Task<TanssTimer> ToggleAsync(int timerId, CancellationToken ct = default);

    Task DeleteAsync(int timerId, CancellationToken ct = default);
    Task<IReadOnlyList<TimerFragment>> ListNotesAsync(int timerId, CancellationToken ct = default);

    /// <summary>Hängt eine Notiz an den zuletzt begonnenen Laufabschnitt an.</summary>
    Task AddNoteAsync(int timerId, string note, CancellationToken ct = default);

    /// <summary>
    /// Ersetzt den Text eines <b>bestehenden</b> Laufabschnitts. Das Fragment muss zuvor gelesen
    /// worden sein — TANSS vergleicht seine Prüfsumme und lehnt eine veraltete ab.
    /// </summary>
    Task UpdateNoteAsync(TimerFragment fragment, string note, CancellationToken ct = default);
}

/// <summary>Techniker nachschlagen — für die Einrichtung und die Eigenidentifikation.</summary>
public interface ITechnicianRepository
{
    Task<IReadOnlyList<Technician>> ListAsync(CancellationToken ct = default);
}
