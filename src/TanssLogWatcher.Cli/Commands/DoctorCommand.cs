using System.Globalization;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Auth;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Cli.Output;
using TanssLogWatcher.Storage;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Queue;

namespace TanssLogWatcher.Cli.Commands;

/// <summary>Was <c>doctor</c> zum Prüfen braucht.</summary>
/// <remarks>
/// Alles als Schnittstelle, damit der Befehl gegen Attrappen prüfbar ist: Sein Rückgabewert ist
/// der Überwachungsvertrag, und ein Vertrag, der sich nur gegen eine echte Instanz prüfen
/// lässt, wird nicht geprüft.
/// </remarks>
public sealed record DoctorInputs
{
    /// <summary>Die geladene und geprüfte Konfiguration.</summary>
    public required AppConfig Config { get; init; }

    /// <summary>Der Zugang — für den Trockentest der Erneuerungsfähigkeit.</summary>
    public required ITanssClient Client { get; init; }

    /// <summary>Fernwartungen; hier nur für die Liste der Anbindungen.</summary>
    public required IRemoteSupportRepository RemoteSupports { get; init; }

    /// <summary>Die Technikerliste — zugleich die billige Route für die Erreichbarkeit.</summary>
    public required ITechnicianRepository Technicians { get; init; }

    /// <summary>Der Tokenspeicher.</summary>
    public required ITokenStore Tokens { get; init; }

    /// <summary>
    /// Die Warteschlange, auf Abruf.
    /// </summary>
    /// <remarks>
    /// Bewusst eine Fabrik und kein fertiges Stück: Dass sich <c>state.db</c> nicht öffnen
    /// lässt, ist selbst ein Befund — und einer, der in der Ausgabe stehen soll, statt den
    /// ganzen Befehl mit einer Ausnahme zu beenden.
    /// </remarks>
    public required Func<IUploadQueue> Queue { get; init; }

    /// <summary>Der Ort der Konfiguration, für die Ausgabe.</summary>
    public string? ConfigPath { get; init; }

    /// <summary>Die Uhr; für Tests einsetzbar.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// Prüft der Reihe nach, ob diese Einrichtung tatsächlich trägt.
/// </summary>
/// <remarks>
/// <para><b>Der Rückgabewert ist der Überwachungsvertrag:</b> 0 gesund, 1 Warnung, 2 gestört.
/// Er ist der strengste Befund aller Stufen — ein Durchlauf ist so gesund wie seine
/// schlechteste Stufe.</para>
///
/// <para><b>Jede Stufe wird einzeln gemeldet, auch die geglückte.</b> Eine Prüfung, die nur im
/// Fehlerfall etwas sagt, lässt offen, ob sie überhaupt gelaufen ist — und genau das ist die
/// Frage, die sich beim ersten Verdacht stellt.</para>
///
/// <para><b>Eine gescheiterte Stufe beendet den Durchlauf nicht</b> (Hausregel 5). Wer ein
/// abgelaufenes Token hat, will im selben Durchgang auch erfahren, dass sein Rückstau
/// vierzehn Sitzungen zählt.</para>
/// </remarks>
public static class DoctorCommand
{
    /// <summary>Führt alle Prüfungen aus und schreibt den Bericht.</summary>
    /// <param name="inputs">Die Bausteine.</param>
    /// <param name="writer">Wohin der Bericht geht.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>0 gesund, 1 Warnung, 2 gestört.</returns>
    public static async Task<int> RunAsync(DoctorInputs inputs, TextWriter writer,
                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine("Prüfung der Einrichtung — tanss-logwatch doctor");
        writer.WriteLine();

        int worst = ExitCode.Healthy;
        void Say(CheckResult check)
        {
            Report.WriteCheck(writer, check);
            worst = ExitCode.Worse(worst, Report.ToExitCode(check.Level));
        }

        Say(Configuration(inputs));

        (CheckResult reachable, CheckResult carries, IReadOnlyList<Technician> technicians) =
            await ProbeAsync(inputs, ct).ConfigureAwait(false);
        Say(reachable);

        Say(LocalToken(inputs));
        Say(carries);
        Say(await RotationAsync(inputs, ct).ConfigureAwait(false));
        Say(await SystemsAsync(inputs, ct).ConfigureAwait(false));
        Say(OwnEmployee(inputs, technicians));
        Say(Queue(inputs, writer));
        Say(Tls(inputs));

        writer.WriteLine();
        writer.WriteLine(worst switch
        {
            ExitCode.Healthy => "Befund: gesund. Rückgabewert 0.",
            ExitCode.Warning => "Befund: Warnung — es läuft, verlangt aber Aufmerksamkeit. "
                + "Rückgabewert 1.",
            _ => "Befund: gestört — in diesem Zustand geht Arbeit verloren oder kommt nicht an. "
                + "Rückgabewert 2.",
        });

        return worst;
    }

    /// <summary>
    /// Der Befund zur Konfiguration.
    /// </summary>
    /// <remarks>
    /// Hier immer in Ordnung: Wäre sie es nicht, käme dieser Befehl gar nicht so weit — das
    /// Laden prüft mit. Die Stufe steht trotzdem in der Ausgabe, weil sie sonst als einzige
    /// fehlte und der Leser rätselte, ob sie übersprungen wurde.
    /// </remarks>
    private static CheckResult Configuration(DoctorInputs inputs)
    {
        int active = inputs.Config.Monitoring.Count;
        string where = inputs.ConfigPath is { Length: > 0 } path ? path : "dem vorgesehenen Ort";
        return new CheckResult("Konfiguration", CheckLevel.Ok,
            $"Geladen und geprüft aus {where}. {Count(active, "Beobachtungsregel", "Beobachtungsregeln")}, "
            + $"Basisadresse {inputs.Config.Tanss.BaseUrl}, Mitarbeiter-ID "
            + string.Create(CultureInfo.InvariantCulture, $"{inputs.Config.Tanss.EmployeeId}."));
    }

    /// <summary>
    /// Der eine Netzaufruf, aus dem drei Befunde entstehen: Erreichbarkeit, Tragfähigkeit des
    /// Tokens und — bei Erfolg — die Technikerliste für die Gegenprobe der eigenen Kennung.
    /// </summary>
    /// <remarks>
    /// <para>Ein Aufruf statt dreier, weil <c>GET /api/tanss.x/v1/technicians</c> alle drei
    /// Fragen zugleich beantwortet und die billigste Route ist, die wir kennen.</para>
    /// <para>Die Unterscheidung zwischen „nicht erreichbar“ und „erreichbar, aber abgewiesen“
    /// ist die wichtigste in diesem ganzen Befehl: Das eine ist ein Netz- oder Adressproblem,
    /// das andere ein Rechte- oder Tokenproblem. Wer beides zu „Verbindung fehlgeschlagen“
    /// zusammenzieht, schickt den Techniker in die falsche Richtung.</para>
    /// </remarks>
    private static async Task<(CheckResult Reachable, CheckResult Carries,
                               IReadOnlyList<Technician> Technicians)> ProbeAsync(
        DoctorInputs inputs, CancellationToken ct)
    {
        const string reachName = "TANSS erreichbar";
        const string carryName = "Token trägt (echter Aufruf)";

        try
        {
            IReadOnlyList<Technician> technicians =
                await inputs.Technicians.ListAsync(ct).ConfigureAwait(false);

            return (
                new CheckResult(reachName, CheckLevel.Ok,
                    $"GET /api/tanss.x/v1/technicians hat geantwortet ({inputs.Config.Tanss.BaseUrl})."),
                new CheckResult(carryName, CheckLevel.Ok,
                    "Der Aufruf wurde angenommen; das hinterlegte Token ist gültig und hat die "
                    + $"nötigen Rechte. {Count(technicians.Count, "Techniker", "Techniker")} gelesen."),
                technicians);
        }
        catch (TanssUnreachableException ex)
        {
            return (
                new CheckResult(reachName, CheckLevel.Fail,
                    "TANSS hat nicht geantwortet. Üblich sind drei Ursachen: keine Netzverbindung "
                    + "beziehungsweise kein VPN, eine Basisadresse, die nicht auf /backend zeigt, "
                    + "oder ein Zertifikat, dem dieser Rechner nicht traut. Zu prüfen ist "
                    + $"tanss.base_url ({inputs.Config.Tanss.BaseUrl}) und die Erreichbarkeit im "
                    + "Browser. Meldung: " + Redaction.Scrub(ex.Message)),
                new CheckResult(carryName, CheckLevel.Fail,
                    "Nicht geprüft — ohne Antwort von TANSS lässt sich über das Token nichts sagen."),
                []);
        }
        catch (TanssConfigurationException ex)
        {
            return (
                new CheckResult(reachName, CheckLevel.Fail,
                    "Die Einstellungen taugen nicht; hier hilft kein zweiter Versuch, sondern nur "
                    + "eine berichtigte Adresse. Meldung: " + Redaction.Scrub(ex.Message)),
                new CheckResult(carryName, CheckLevel.Fail,
                    "Nicht geprüft — der Aufruf ist gar nicht erst zustande gekommen."),
                []);
        }
        catch (TanssAuthException ex)
        {
            return (
                new CheckResult(reachName, CheckLevel.Ok,
                    "TANSS hat geantwortet — abweisend, aber es hat geantwortet. Adresse und Netz "
                    + "sind damit in Ordnung."),
                new CheckResult(carryName, CheckLevel.Fail,
                    "Der Aufruf wurde abgewiesen. Üblich sind drei Ursachen: das Token ist "
                    + "abgelaufen, es wurde für einen anderen Mandanten geprägt, oder dem "
                    + "Mitarbeiter fehlt das Recht auf diese Route. Ein neues Token prägt "
                    + "„tanss-logwatch token rotate“. Meldung: " + Redaction.Scrub(ex.Message)),
                []);
        }
        catch (TanssException ex)
        {
            return (
                new CheckResult(reachName, CheckLevel.Ok,
                    "TANSS hat geantwortet, wenn auch mit einem Fehler."),
                new CheckResult(carryName, CheckLevel.Fail,
                    "Der Aufruf ist fehlgeschlagen. Wenn die Instanz gerade neu startet, hilft "
                    + "ein zweiter Durchlauf in ein paar Minuten; bleibt es dabei, ist die "
                    + "TANSS-Fassung gegen die benutzten Routen abzugleichen. Meldung: "
                    + Redaction.Scrub(ex.Message)),
                []);
        }
        catch (TokenStoreException ex)
        {
            // Ohne diesen Fang stuerzt ausgerechnet „doctor“ mit „Unerwarteter Fehler —
            // Programmfehler“ ab, und zwar genau in der Lage, fuer deren Diagnose es ihn gibt:
            // Der Zugang liest das Token erst beim Aufruf, und ein fehlendes oder mit einem
            // anderen Windows-Benutzer versiegeltes Token wirft dann von unten herauf.
            return (
                new CheckResult(reachName, CheckLevel.Fail,
                    "Es wurde gar nicht erst gefragt: Ohne lesbares Token gibt es keinen Aufruf, "
                    + "den TANSS beantworten könnte."),
                new CheckResult(carryName, CheckLevel.Fail,
                    "Das hinterlegte Token liess sich nicht lesen. Üblich sind zwei Ursachen: Es "
                    + "wurde nie abgelegt, oder es wurde unter einem anderen Windows-Benutzer "
                    + "abgelegt — die Versiegelung ist an den Benutzer gebunden und lässt sich "
                    + "nicht übertragen. Meldung: " + Redaction.Scrub(ex.Message)),
                []);
        }
    }

    /// <summary>
    /// Der Befund zum hinterlegten Token, <b>ohne</b> Netzaufruf: vorhanden, entsiegelbar,
    /// Restlaufzeit aus dem <c>exp</c>-Anspruch.
    /// </summary>
    /// <remarks>
    /// Gelesen, nicht geprüft — die Signatur prüft der Server, und der Schlüssel dazu liegt
    /// dort. Diese Stufe beantwortet allein die Betriebsfrage: Ist überhaupt etwas da, lässt
    /// es sich unter diesem Windows-Konto entsiegeln, und wie lange reicht es noch?
    /// </remarks>
    private static CheckResult LocalToken(DoctorInputs inputs)
    {
        const string name = "Token vorhanden und entsiegelbar";

        string raw;
        try
        {
            raw = inputs.Tokens.Read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CheckResult(name, CheckLevel.Fail,
                "Das Token liess sich nicht aus dem lokalen Profil lesen. Üblichste Ursache: Der "
                + "Dienst läuft unter einem anderen Windows-Konto als bei der Einrichtung — die "
                + "Ablage ist mit DPAPI an Benutzer und Rechner gebunden und wandert deshalb "
                + "nicht mit. Ein neues Token richtet „tanss-logwatch setup“ ein. Meldung: "
                + Redaction.Scrub(ex.Message));
        }

        TanssTokenClaims claims;
        try
        {
            claims = TanssAuth.DecodeClaims(raw);
        }
        catch (TanssAuthException ex)
        {
            return new CheckResult(name, CheckLevel.Fail,
                "Der hinterlegte Wert ist kein lesbares JWT. Ein beschädigter Tokenspeicher "
                + "heilt nicht von selbst; er ist mit „tanss-logwatch setup“ neu einzurichten. "
                + "Meldung: " + Redaction.Scrub(ex.Message));
        }

        if (claims.ExpiresAt is not { } expiry)
        {
            return new CheckResult(name, CheckLevel.Warn,
                "Das Token ist lesbar, trägt aber keinen exp-Anspruch. Damit lässt sich von hier "
                + "aus nicht sagen, wann es abläuft — die Erneuerung kann nicht rechtzeitig "
                + "anlaufen und das Token stirbt im Betrieb. Es sollte einmal frisch geprägt "
                + "werden: „tanss-logwatch token rotate“.");
        }

        TimeSpan remaining = expiry - inputs.Clock.GetUtcNow();
        string when = $"Läuft ab am {Report.Moment(expiry)} ({Report.Duration(remaining)}).";

        if (remaining <= TimeSpan.Zero)
        {
            return new CheckResult(name, CheckLevel.Fail,
                when + " Ein abgelaufenes Token nimmt TANSS nicht mehr an; jede Sitzung bliebe "
                + "in der Warteschlange liegen. Neu prägen mit „tanss-logwatch token rotate“.");
        }

        if (remaining <= TimeSpan.FromDays(inputs.Config.Tanss.RotateBeforeDays))
        {
            return new CheckResult(name, CheckLevel.Warn,
                when + " Damit ist die Erneuerung fällig (tanss.rotate_before_days = "
                + string.Create(CultureInfo.InvariantCulture,
                    $"{inputs.Config.Tanss.RotateBeforeDays}). ")
                + "Sie geschieht nicht von selbst, solange kein Dienst läuft: "
                + "„tanss-logwatch token rotate“.");
        }

        return new CheckResult(name, CheckLevel.Ok, when);
    }

    /// <summary>
    /// Der Trockentest auf Erneuerungsfähigkeit.
    /// </summary>
    /// <remarks>
    /// <para><c>isForTesting=true</c> ist der einzige folgenlose Weg, diese Frage zu stellen:
    /// TANSS antwortet mit einem unbrauchbaren Token von 60 Sekunden Laufzeit und
    /// protokolliert den Vorgang nicht. Jeder andere Versuch prägte ein echtes, nicht
    /// widerrufbares Token.</para>
    /// <para>Der Fehlschlag ist ausdrücklich eine <b>Warnung</b> und kein Fehler: Alles läuft
    /// heute. Nur läuft es an dem Tag, an dem das Token abläuft, lautlos nicht mehr weiter —
    /// und genau das soll hier stehen, solange noch Zeit ist, das Recht zu vergeben.</para>
    /// </remarks>
    private static async Task<CheckResult> RotationAsync(DoctorInputs inputs, CancellationToken ct)
    {
        const string name = "Erneuerungsfähigkeit (Trockentest)";

        bool? allowed;
        try
        {
            allowed = await TanssAuth.CanRotateAsync(inputs.Client, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CheckResult(name, CheckLevel.Warn,
                "Der Trockentest ist nicht zustande gekommen; über die Erneuerungsfähigkeit "
                + "lässt sich damit nichts sagen. Meldung: " + Redaction.Scrub(ex.Message));
        }

        // Drei Ausgaenge, nicht zwei. "Konnte nicht gefragt werden" ist keine Aussage ueber ein
        // Recht - wer das zu "darf nicht" zusammenzieht, schickt den Techniker wegen eines
        // Netzproblems zum TANSS-Administrator.
        return allowed switch
        {
            true => new CheckResult(name, CheckLevel.Ok,
                "Der Mitarbeiter darf Token prägen. Die Erneuerung vor Ablauf wird gelingen."),
            false => new CheckResult(name, CheckLevel.Warn,
                "Der Mitarbeiter darf keine Token prägen (in TANSS das Recht 480, "
                + "NOT_ALLOWED_TO_CREATE_JWTS). Heute merkt man davon nichts — aber am Tag des "
                + "Ablaufs stirbt das Token lautlos, und ab dann bleibt jede Fernwartung in der "
                + "Warteschlange liegen. Das Recht ist in TANSS zu vergeben; von hier aus lässt "
                + "es sich nicht setzen."),
            null => new CheckResult(name, CheckLevel.Warn,
                "Die Frage liess sich nicht stellen — TANSS hat den Trockentest nicht "
                + "beantwortet. Über das Prägerecht ist damit nichts bekannt; es ist "
                + "ausdrücklich NICHT gesagt, dass es fehlt. Zuerst sind die Befunde oben zu "
                + "Erreichbarkeit und Token zu prüfen."),
        };
    }

    /// <summary>
    /// Der Befund zum Modul Fernwartung — und die Gegenprobe der eingestellten Typkennungen.
    /// </summary>
    /// <remarks>
    /// Die Gegenprobe steht hier und nicht in der Konfigurationsprüfung, weil sie einen
    /// Netzaufruf braucht: Ob 1005 in <i>dieser</i> Instanz existiert, weiss nur die Instanz.
    /// Ein Typ, den es dort nicht gibt, wird erst beim Upload mit <c>TYPE_DOESNT_EXIST</c>
    /// abgewiesen — also nach getaner Arbeit, deren Sitzung dann festhinge.
    /// </remarks>
    private static async Task<CheckResult> SystemsAsync(DoctorInputs inputs, CancellationToken ct)
    {
        const string name = "Modul Fernwartung";

        IReadOnlyList<RemoteSupportSystem> systems;
        try
        {
            systems = await inputs.RemoteSupports.ListSystemsAsync(ct).ConfigureAwait(false);
        }
        catch (TanssModuleNotLicensedException ex)
        {
            return new CheckResult(name, CheckLevel.Fail,
                "Das Modul Fernwartung ist auf dieser Instanz nicht lizenziert. Ohne es nimmt "
                + "TANSS keine Fernwartung an; dieses Werkzeug hätte nichts zu tun. Die Lizenz "
                + "ist beim Hersteller zu klären. Meldung: " + Redaction.Scrub(ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CheckResult(name, CheckLevel.Fail,
                "Die Anbindungen liessen sich nicht lesen (GET "
                + "/api/tanss.x/v1/remoteSupports/systems). Solange das so bleibt, ist nicht zu "
                + "sagen, ob die eingestellten Typkennungen überhaupt existieren. Meldung: "
                + Redaction.Scrub(ex.Message));
        }

        if (systems.Count == 0)
        {
            return new CheckResult(name, CheckLevel.Warn,
                "Die Instanz führt keine externen Fernwartungs-Anbindungen. Jeder Upload würde "
                + "mit TYPE_DOESNT_EXIST abgewiesen. Anzulegen sind sie in TANSS unter "
                + "„Externe Fernwartungs-Anbindungen verwalten“; die Kennung muss mindestens "
                + "1000 sein.");
        }

        string known = string.Join(", ", systems.Select(system => string.Create(
            CultureInfo.InvariantCulture, $"{system.Id} {system.Name}")));

        List<string> missing = [.. inputs.Config.Monitoring
            .Select(entry => entry.RemoteSupportTypeId)
            .Distinct()
            .Where(id => !systems.Any(system => system.Id == id))
            .Select(id => id.ToString(CultureInfo.InvariantCulture))];

        return missing.Count == 0
            ? new CheckResult(name, CheckLevel.Ok,
                $"{Count(systems.Count, "Anbindung", "Anbindungen")} vorhanden: {known}. Alle "
                + "eingestellten Typkennungen kommen darin vor.")
            : new CheckResult(name, CheckLevel.Warn,
                $"Vorhanden sind: {known}. In der Konfiguration stehen aber Typkennungen, die es "
                + $"hier nicht gibt: {string.Join(", ", missing)}. Ein Upload darauf wird mit "
                + "TYPE_DOESNT_EXIST abgewiesen — und zwar erst nach getaner Arbeit. Die "
                + "Kennungen in monitoring[].remote_support_type_id sind gegen die obige Liste "
                + "abzugleichen.");
    }

    /// <summary>Die eigene Mitarbeiter-ID gegen die Technikerliste.</summary>
    /// <remarks>
    /// Eine Warnung und kein Fehler: Es gibt Instanzen, in denen die Technikerliste nicht jedes
    /// buchungsfähige Konto führt. Die Folge wäre aber schwerwiegend — die Fernwartung landet
    /// bei niemandem —, deshalb steht sie ausdrücklich da.
    /// </remarks>
    private static CheckResult OwnEmployee(DoctorInputs inputs, IReadOnlyList<Technician> technicians)
    {
        const string name = "Eigene Mitarbeiter-ID";
        int own = inputs.Config.Tanss.EmployeeId;

        if (technicians.Count == 0)
        {
            // "Leer" und "nicht lesbar" sehen hier gleich aus - die Liste kommt als leere
            // Sammlung zurueck, wenn der Aufruf scheitert (siehe ProbeAsync). Zu behaupten,
            // sie "liess sich nicht lesen", waere in dem Fall geraten, in dem die Instanz
            // tatsaechlich keinen Techniker fuehrt.
            return new CheckResult(name, CheckLevel.Warn,
                "Nicht gegengeprüft: Es lag keine Technikerliste vor. Entweder ist der Aufruf "
                + "fehlgeschlagen — dann steht der Grund in den Befunden oben — oder die Instanz "
                + "führt keinen Techniker. Welches von beidem, sagt der Befund „TANSS "
                + "erreichbar“.");
        }

        Technician? found = technicians.FirstOrDefault(technician => technician.Id == own);

        return found is not null
            ? new CheckResult(name, CheckLevel.Ok, string.Create(CultureInfo.InvariantCulture,
                $"{own} ist in der Technikerliste als „{found.Name}“ geführt."))
            : new CheckResult(name, CheckLevel.Warn, string.Create(CultureInfo.InvariantCulture,
                $"tanss.employee_id = {own} kommt in der Technikerliste dieser Instanz nicht vor. ")
                + "Diese Kennung trägt die gesamte Attribution: Stimmt sie nicht, wird die "
                + "Fernwartung zwar angelegt, taucht aber in der Auswertung des Technikers nicht "
                + "auf. Die richtige Kennung steht in TANSS in der Mitarbeiterverwaltung.");
    }

    /// <summary>
    /// Der Befund zur Warteschlange: offen, fehlgeschlagen, unlesbar.
    /// </summary>
    /// <remarks>
    /// <para><see cref="IUploadQueue.Count"/> zählt über SQL und sieht jede Zeile,
    /// <see cref="IUploadQueue.List"/> überspringt die unlesbaren. Die beiden Angaben stimmen
    /// nur überein, solange die Obergrenze der Abfrage nicht erreicht ist — deshalb steht hier
    /// beides nebeneinander und nicht eine Zahl, die stillschweigend eine andere meint.</para>
    /// <para><c>UnreadableNotice</c> wird mit ausgegeben. Ohne ihn stünde „ausstehend: 4“ neben
    /// drei Einträgen, und niemand wüsste, wo der vierte geblieben ist.</para>
    /// </remarks>
    private static CheckResult Queue(DoctorInputs inputs, TextWriter writer)
    {
        const string name = "Warteschlange";

        try
        {
            IUploadQueue queue = inputs.Queue();

            int pending = queue.Count(QueueState.Pending);
            int sending = queue.Count(QueueState.Sending);
            int failed = queue.Count(QueueState.Failed);

            QueueListing pendingList = queue.List(QueueState.Pending);
            QueueListing failedList = queue.List(QueueState.Failed);
            int unreadable = pendingList.Unreadable + failedList.Unreadable;

            string figures = string.Create(CultureInfo.InvariantCulture,
                $"ausstehend {pending}, unterwegs {sending}, fehlgeschlagen {failed}, "
                + $"davon unlesbar {unreadable} "
                + $"(geprüft {pendingList.Examined + failedList.Examined} Zeilen).");

            foreach (string? notice in new string?[]
                     { pendingList.UnreadableNotice, failedList.UnreadableNotice })
            {
                if (notice is not null)
                {
                    writer.WriteLine("            " + notice);
                }
            }

            if (failed > 0 || unreadable > 0)
            {
                return new CheckResult(name, CheckLevel.Warn,
                    figures + " Aufgegebene und unlesbare Einträge werden nicht mehr von selbst "
                    + "versucht; sie sind mit „tanss-logwatch queue“ anzusehen. Vor einer "
                    + "Nachbuchung von Hand ist erst zu prüfen, ob die Fernwartung nicht doch "
                    + "schon in TANSS steht — TANSS dedupliziert nicht.");
            }

            return pending + sending > 0
                ? new CheckResult(name, CheckLevel.Ok,
                    figures + " Die offenen Einträge gehen beim nächsten „queue --flush“ "
                    + "beziehungsweise beim laufenden Dienst hinaus.")
                : new CheckResult(name, CheckLevel.Ok, figures + " Nichts liegt an.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CheckResult(name, CheckLevel.Fail,
                "Die Zustandsdatenbank liess sich nicht öffnen. Solange das so bleibt, kann "
                + "keine Sitzung zwischengelagert werden — ein Netzfehler kostet dann die "
                + "Sitzung selbst. Üblichste Ursache: ein zweiter Lauf des Werkzeugs hält die "
                + "Datei, oder das Profil liegt auf einem nicht erreichbaren Netzlaufwerk. "
                + "Meldung: " + Redaction.Scrub(ex.Message));
        }
    }

    /// <summary>Der Befund zur Zertifikatsprüfung.</summary>
    /// <remarks>
    /// Ausdrücklich eine eigene Stufe. Abgeschaltete TLS-Prüfung ist keine Nebensache: Das
    /// Token wandert bei jedem Aufruf über die Leitung, und ohne Zertifikatsprüfung genügt ein
    /// vorgeschalteter Mitleser, um es mitzunehmen.
    /// </remarks>
    private static CheckResult Tls(DoctorInputs inputs) =>
        inputs.Config.Tanss.VerifyTls
            ? new CheckResult("TLS-Prüfung", CheckLevel.Ok,
                "Zertifikate werden geprüft (tanss.verify_tls = true).")
            : new CheckResult("TLS-Prüfung", CheckLevel.Warn,
                "Die Zertifikatsprüfung ist abgeschaltet (tanss.verify_tls = false). Das Token "
                + "geht bei jedem Aufruf über die Leitung; ohne Prüfung genügt ein "
                + "vorgeschalteter Mitleser, um es mitzunehmen — und TANSS 10.10.0 kann ein "
                + "ausgestelltes Token nicht widerrufen. Vorgesehen ist der Schalter allein für "
                + "Testinstanzen mit selbst ausgestelltem Zertifikat.");

    /// <summary>Zahl mit passendem Ein- oder Mehrzahlwort.</summary>
    private static string Count(int value, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{value} {(value == 1 ? singular : plural)}");
}
