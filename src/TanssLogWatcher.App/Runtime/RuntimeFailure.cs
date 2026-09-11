using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Storage;

namespace TanssLogWatcher.App.Runtime;

/// <summary>Ein eingeordneter Fehlschlag: Ursache, Klartextgrund, Vorschlag.</summary>
/// <param name="Cause">Die Art der Störung.</param>
/// <param name="Reason">Was kaputt ist und warum das üblicherweise passiert.</param>
/// <param name="Advice">Was zu tun ist.</param>
public sealed record RuntimeFailureReport(DegradedCause Cause, string Reason, string Advice);

/// <summary>
/// Ordnet einen Fehlschlag gegen TANSS ein — an genau einer Stelle.
/// </summary>
/// <remarks>
/// <para><b>Warum das zusammengezogen ist.</b> Drei Stellen fragen TANSS: der Verbindungstest,
/// der Sendedienst und die Tokenerneuerung. Ordnete jede für sich ein, meldete derselbe
/// fehlende Tokenspeicher einmal „Token fehlt“ und einmal „unerwarteter Fehler“ — je nachdem,
/// welcher Dienst zuletzt fertig wurde. Der Betriebszustand flackerte, und der Techniker
/// bekäme abwechselnd zwei verschiedene Anweisungen für dieselbe Ursache.</para>
///
/// <para><b>Die wichtigste Grenze ist die zwischen „nicht erreichbar“ und „abgewiesen“.</b> Das
/// eine ist ein Netz- oder Adressproblem, das andere ein Rechte- oder Tokenproblem. Wer beides
/// zu „Verbindung fehlgeschlagen“ zusammenzieht, schickt den Techniker in die falsche Richtung.</para>
/// </remarks>
public static class RuntimeFailure
{
    /// <summary>
    /// Der Satz, der in jedem Störungsvorschlag steht.
    /// </summary>
    /// <remarks>
    /// Er ist die eigentliche Botschaft des Zustands „Gestört“: Es ist nichts verloren. Ohne ihn
    /// liest sich jede dieser Meldungen wie ein Datenverlust, und ein Techniker, der das glaubt,
    /// fängt an, Fernwartungen von Hand nachzutragen — womit er die Dubletten erzeugt, gegen
    /// die dieses Werkzeug gebaut ist.
    /// </remarks>
    public const string QueueKeeps =
        "Die Beobachtung läuft unterdessen weiter und die Warteschlange hält die Sitzungen — "
        + "es geht nichts verloren, es ruht nur das Senden.";

    /// <summary>Ordnet einen Fehlschlag ein.</summary>
    /// <param name="error">Der Fehler.</param>
    /// <param name="baseUrl">Die Basisadresse, für die Meldung; darf leer sein.</param>
    public static RuntimeFailureReport Classify(Exception error, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        string where = string.IsNullOrWhiteSpace(baseUrl) ? "die eingestellte Adresse" : baseUrl;

        return error switch
        {
            // Steht VOR TanssException: Der Tokenspeicher wirft mitten im Aufruf, und ohne
            // diesen Zweig erschiene ein nie eingerichtetes Token als Netzfehler.
            TokenStoreException token => new RuntimeFailureReport(DegradedCause.TokenUnreadable,
                "Es liegt kein lesbares Arbeitstoken vor. Entweder wurde nie eines eingerichtet, "
                + "oder die Ablage gehört einem anderen Windows-Konto: Sie ist mit DPAPI an "
                + "Benutzer und Rechner gebunden und wandert mit einem umgezogenen Profil nicht "
                + "mit. Meldung: " + Redaction.Scrub(token.Message),
                "Ein Token prägt bis auf Weiteres „tanss-logwatch token rotate“ — unter "
                + "demselben Windows-Benutzer, unter dem dieses Werkzeug läuft. " + QueueKeeps),

            TanssUnreachableException unreachable => new RuntimeFailureReport(
                DegradedCause.Unreachable,
                "TANSS hat nicht geantwortet. Üblich sind drei Ursachen: keine Netzverbindung "
                + "beziehungsweise kein VPN, eine Basisadresse, die nicht auf /backend zeigt, "
                + "oder ein Zertifikat, dem dieser Rechner nicht traut. Meldung: "
                + Redaction.Scrub(unreachable.Message),
                $"VPN prüfen und {where} im Browser aufrufen. " + QueueKeeps),

            TanssConfigurationException configuration => new RuntimeFailureReport(
                DegradedCause.Unreachable,
                "Die Einstellungen taugen nicht; hier hilft kein zweiter Versuch, sondern nur "
                + "eine berichtigte Adresse. Meldung: " + Redaction.Scrub(configuration.Message),
                "tanss.base_url berichtigen — sie muss auf /backend enden — und neu laden. "
                + QueueKeeps),

            TanssAuthException auth => new RuntimeFailureReport(DegradedCause.TokenRejected,
                "TANSS hat geantwortet und den Aufruf abgewiesen; Adresse und Netz sind damit in "
                + "Ordnung. Üblich sind drei Ursachen: das Token ist abgelaufen, es wurde für "
                + "einen anderen Mandanten geprägt, oder dem Mitarbeiter fehlt das Recht auf "
                + "diese Route. Meldung: " + Redaction.Scrub(auth.Message),
                "Ein neues Token prägt „tanss-logwatch token rotate“; Rechte sind in TANSS zu "
                + "vergeben. " + QueueKeeps),

            TanssModuleNotLicensedException licence => new RuntimeFailureReport(
                DegradedCause.ModuleNotLicensed,
                "Das Modul Fernwartung ist auf dieser Instanz nicht lizenziert. Ohne es nimmt "
                + "TANSS keine Fernwartung an; dieses Werkzeug hätte nichts abzuliefern. "
                + "Meldung: " + Redaction.Scrub(licence.Message),
                "Die Lizenz ist beim Hersteller zu klären; von hier aus lässt sie sich nicht "
                + "setzen. " + QueueKeeps),

            StorageException storage => new RuntimeFailureReport(DegradedCause.StateDatabase,
                "Die Ablage auf diesem Rechner liess sich nicht benutzen. Üblichste Ursache: ein "
                + "zweiter Lauf des Werkzeugs hält state.db, oder das Benutzerprofil liegt auf "
                + "einem gerade nicht erreichbaren Netzlaufwerk. Meldung: "
                + Redaction.Scrub(storage.Message),
                "Zweite Instanz beenden und neu laden. Solange das so bleibt, kann keine Sitzung "
                + "zwischengelagert werden — ein Netzfehler kostet dann die Sitzung selbst."),

            TanssException tanss => new RuntimeFailureReport(DegradedCause.Unknown,
                "TANSS hat mit einem Fehler geantwortet. Meldung: "
                + Redaction.Scrub(tanss.Message),
                "Startet die Instanz gerade neu, hilft ein zweiter Versuch in ein paar Minuten; "
                + "bleibt es dabei, ist die TANSS-Fassung gegen die benutzten Routen "
                + "abzugleichen. " + QueueKeeps),

            _ => new RuntimeFailureReport(DegradedCause.Unknown,
                "Ein unerwarteter Fehler ist aufgetreten. Das ist ein Programmfehler und keine "
                + "Frage der Einrichtung. Meldung: " + Redaction.Scrub(error.Message),
                "Die Meldung gehört in eine Fehlermeldung an den Hersteller. " + QueueKeeps),
        };
    }

    /// <summary>Ordnet einen Fehlschlag ein und meldet ihn als Störung.</summary>
    /// <param name="context">Der Zustandsträger.</param>
    /// <param name="error">Der Fehler.</param>
    /// <param name="baseUrl">Die Basisadresse, für die Meldung.</param>
    public static void Degrade(IRuntimeContext context, Exception error, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        RuntimeFailureReport report = Classify(error, baseUrl);
        context.ReportDegraded(report.Cause, report.Reason, report.Advice);
    }
}
