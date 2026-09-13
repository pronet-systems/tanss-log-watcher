using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.Api.Contract;

/// <summary>
/// Ein Ticket anlegen — <c>POST /api/v1/tickets</c>.
/// </summary>
/// <remarks>
/// <para><b>Warum eine eigene Schnittstelle.</b> Dieselbe Überlegung wie bei
/// <see cref="ITicketVerification"/>: <see cref="ITicketRepository"/> bleibt schmal und liest
/// nur, das Anlegen steht daneben. Die Oberfläche bekommt damit eine Grenze, hinter die sich
/// eine Attrappe setzen lässt — ohne sie wäre ein Dialog, der Tickets anlegt, nur mit einer
/// echten TANSS-Instanz zu prüfen, und eine Zusage, die kein Test hält, ist eine Zusage auf
/// Zuruf.</para>
/// <para><b>Diese Schnittstelle wirft</b>, anders als die Prüfung: Ein Anlegen, das
/// fehlschlägt, ist kein Nebenbefund, sondern der Vorgang selbst. Den anzeigbaren Text zu
/// einer <c>TanssException</c> liefert <c>TicketCreator.Explain</c>.</para>
/// </remarks>
public interface ITicketCreation
{
    /// <summary>
    /// Legt das Ticket an und gibt zurück, was TANSS dazu gesagt hat.
    /// </summary>
    /// <remarks>
    /// <b>Ohne Wiederholung, und das mit Absicht.</b> Ein Ticket trägt keine eigene Kennung wie
    /// <c>remoteMaintenanceId</c>, an der sich eine Dublette erkennen liesse. Eine Antwort ohne
    /// Ticketnummer endet deshalb in <see cref="TicketCreateResult.Warning"/> und nicht in einer
    /// Ausnahme — sie sähe aus wie „nichts passiert“ und verleitete zum zweiten Versuch.
    /// </remarks>
    /// <param name="draft">Der Entwurf. Muss <see cref="TicketDraft.IsComplete"/> erfüllen.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>Das Ergebnis; niemals <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Der Entwurf ist unvollständig. Es wird dann <b>nichts gesendet</b>.
    /// </exception>
    Task<TicketCreateResult> CreateAsync(TicketDraft draft, CancellationToken ct = default);
}
