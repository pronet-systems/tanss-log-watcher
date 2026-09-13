using TanssLogWatcher.App.Runtime;

namespace TanssLogWatcher.App.Services;

/// <summary>
/// Das sofortige Buchen eines wartenden Warteschlangeneintrags — aus dem Abschlussdialog
/// heraus.
/// </summary>
/// <remarks>
/// <para><b>Warum es diese Schnittstelle gibt und nicht nur die Methode am Sendedienst.</b>
/// Der Abschlussdialog ist das Fenster, an dem Arbeitszeit hängt; er muss sich ohne TANSS
/// prüfen lassen. Ohne diesen Schnitt führe jeder Test des Dialogs einen echten Namen auf und
/// buchte im besten Fall nichts, im schlechtesten etwas.</para>
///
/// <para><b>Es ist ausdrücklich kein zweiter Sendeweg.</b> Die einzige Umsetzung im Betrieb
/// ist <see cref="UploadService.BookNowAsync"/>, und die läuft durch dieselbe Existenzprüfung
/// und dieselben Quittungen wie der Takt. Ein eigener, kürzerer Weg aus dem Dialog heraus
/// hätte die Prüfung umgangen, die verhindert, dass TANSS eine Fernwartung zweimal führt —
/// TANSS dedupliziert nicht.</para>
/// </remarks>
public interface IImmediateBooking
{
    /// <summary>Bucht den wartenden Eintrag sofort.</summary>
    /// <param name="remoteMaintenanceId">Der Eintrag, üblicherweise die Sitzungskennung.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>
    /// Was mit ihm geschehen ist, oder <see langword="null"/>, wenn er gar nicht mehr wartete —
    /// dann ist er unterwegs, gesendet oder aufgegeben, und der Aufrufer darf keinen zweiten
    /// Versand auslösen.
    /// </returns>
    Task<UploadReport?> BookNowAsync(string remoteMaintenanceId, CancellationToken ct = default);
}
