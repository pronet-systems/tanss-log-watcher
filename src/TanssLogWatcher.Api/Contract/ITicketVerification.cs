using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.Api.Contract;

/// <summary>
/// Eine eingetippte Ticketnummer prüfen — mit allen drei möglichen Antworten.
/// </summary>
/// <remarks>
/// <para><b>Warum eine eigene Schnittstelle und kein neues Glied auf
/// <see cref="ITicketRepository"/>.</b> Dieselbe Überlegung wie bei <c>ITanssBodyDelete</c> und
/// <c>ITanssMetaRead</c>: Der Grundvertrag bleibt schmal, damit ihn eine Attrappe in wenigen
/// Zeilen erfüllt. Wer die Prüfung braucht, fragt mit <c>is ITicketVerification</c> danach;
/// <see cref="ITicketRepository"/> bleibt Zeichen für Zeichen, wie es war.</para>
/// <para><b>Diese Prüfung wirft nicht.</b> Jeder Fehlschlag wird zu
/// <see cref="TicketCheckOutcome.Undetermined"/> — ein Fehler kostet die Prüfung, nie den
/// Vorgang (Hausregel 5). Einzige Ausnahme ist der Abbruch durch den Aufrufer selbst; der geht
/// als <see cref="OperationCanceledException"/> durch, weil ein verschluckter Abbruch schlimmer
/// ist als ein durchgereichter.</para>
/// </remarks>
public interface ITicketVerification
{
    /// <summary>
    /// Fragt TANSS, ob es dieses Ticket gibt, und holt Titel und Firma gleich mit.
    /// </summary>
    /// <remarks>
    /// <para>Eine Nummer kleiner oder gleich 0 gilt als „keine Ticketnummer“; dafür wird TANSS
    /// nicht gefragt. Alles andere geht über <c>GET /api/v1/tickets/{id}</c>.</para>
    /// <para>Der Firmenname kommt aus dem <c>meta</c>-Block. Erfüllt der HTTP-Zugang
    /// <see cref="ITanssMetaRead"/> nicht, kostet das den Namen und nicht die Prüfung.</para>
    /// </remarks>
    /// <param name="ticketId">Die zu prüfende Nummer.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>Das Ergebnis; niemals <see langword="null"/>.</returns>
    Task<TicketCheck> CheckAsync(int ticketId, CancellationToken ct = default);
}
