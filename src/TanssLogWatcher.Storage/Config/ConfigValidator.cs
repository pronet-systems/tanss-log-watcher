using System.Globalization;
using System.Text.RegularExpressions;

namespace TanssLogWatcher.Storage.Config;

/// <summary>
/// Prüft eine geladene Konfiguration auf Regelverstöße.
/// </summary>
/// <remarks>
/// <para>Die Prüfung sammelt <b>alle</b> Verstöße und wirft erst danach. Wer eine Datei von
/// Hand pflegt, soll sie in einem Durchgang richtigstellen können und nicht nach jedem Start
/// den nächsten Einzelfehler vorgesetzt bekommen.</para>
///
/// <para>Jede Meldung nennt das Feld in der Schreibweise der Datei — also
/// <c>tanss.base_url</c>, nicht <c>Tanss.BaseUrl</c>. Der Benutzer sucht in JSON, nicht im
/// Quelltext.</para>
///
/// <para>Was hier <b>nicht</b> geprüft wird: ob die Instanz erreichbar ist, ob die
/// Mitarbeiter-ID existiert und ob der Fernwartungstyp in TANSS angelegt wurde. Das kostet
/// Netzzugriffe und gehört in den Einrichtungsassistenten, nicht in jeden Programmstart.
/// Geprüft wird nur, was sich ohne TANSS entscheiden lässt.</para>
/// </remarks>
public static class ConfigValidator
{
    /// <summary>Kleinster Fernwartungstyp, den TANSS für externe Anbindungen annimmt.</summary>
    public const int MinimumRemoteSupportTypeId = 1000;

    private const string RequiredSuffix = "/backend";

    /// <summary>Prüft die Konfiguration und wirft bei mindestens einem Verstoß.</summary>
    /// <param name="config">Die geladene Konfiguration.</param>
    /// <param name="origin">
    /// Herkunft für die Überschrift der Meldung, üblicherweise der Dateipfad. Fehlt sie,
    /// steht dort nur „Die Konfiguration“.
    /// </param>
    /// <exception cref="ConfigValidationException">Mindestens eine Regel ist verletzt.</exception>
    public static void Validate(AppConfig config, string? origin = null)
    {
        IReadOnlyList<string> problems = Collect(config);
        if (problems.Count == 0)
        {
            return;
        }

        string headline = string.IsNullOrWhiteSpace(origin)
            ? "Die Konfiguration ist unvollständig oder fehlerhaft:"
            : $"{origin} ist unvollständig oder fehlerhaft:";
        throw new ConfigValidationException(headline, problems);
    }

    /// <summary>Liefert alle Verstöße, ohne zu werfen. Für Prüfbefehle und Oberflächen.</summary>
    public static IReadOnlyList<string> Collect(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<string> problems = [];
        CheckVersion(config, problems);
        CheckTanss(config.Tanss, problems);
        CheckWatcher(config.Watcher, problems);
        CheckMonitoring(config.Monitoring, problems);
        CheckProxy(config.Proxy, problems);
        CheckLogging(config.Logging, problems);
        CheckRecording(config.Recording, problems);
        CheckHistory(config.History, problems);
        return problems;
    }

    private static void CheckVersion(AppConfig config, List<string> problems)
    {
        if (config.Version < 1)
        {
            problems.Add($"version: {config.Version} ist kein gültiger Stand, erwartet wird 1.");
        }
        else if (config.Version > AppConfig.CurrentVersion)
        {
            problems.Add(
                $"version: {config.Version} stammt aus einer neueren Programmversion "
                + $"(diese liest höchstens {AppConfig.CurrentVersion}). Bitte das Werkzeug "
                + "aktualisieren, statt die Zahl herunterzusetzen — die Datei enthält "
                + "vermutlich Felder, die hier abgelehnt würden.");
        }
    }

    private static void CheckTanss(TanssSection tanss, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(tanss.BaseUrl))
        {
            problems.Add("tanss.base_url: fehlt. Erwartet wird etwa https://tanss.kunde.de/backend.");
        }
        else if (!Uri.TryCreate(tanss.BaseUrl, UriKind.Absolute, out Uri? uri)
                 || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            problems.Add(
                $"tanss.base_url: „{tanss.BaseUrl}“ ist keine vollständige http- oder "
                + "https-Adresse. Erwartet wird etwa https://tanss.kunde.de/backend.");
        }
        else if (tanss.BaseUrl.EndsWith('/'))
        {
            problems.Add(
                $"tanss.base_url: „{tanss.BaseUrl}“ endet auf einen Schrägstrich. Die Pfade "
                + "werden wörtlich angehängt, daraus entstünde ein doppelter Schrägstrich. "
                + $"Erwartet wird „{tanss.BaseUrl.TrimEnd('/')}“.");
        }
        else if (!tanss.BaseUrl.EndsWith(RequiredSuffix, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(
                $"tanss.base_url: „{tanss.BaseUrl}“ endet nicht auf /backend. Zeigt die "
                + "Adresse auf die Weboberfläche statt auf die Schnittstelle, antwortet die "
                + "PHP-Oberfläche auf jede Anfrage mit HTTP 400 — auch ohne Token, weshalb "
                + "der Fehler wie ein defektes Werkzeug aussieht. Erwartet wird "
                + $"„{tanss.BaseUrl.TrimEnd('/')}{RequiredSuffix}“.");
        }

        if (tanss.EmployeeId <= 0)
        {
            problems.Add(
                $"tanss.employee_id: {tanss.EmployeeId} ist unbrauchbar, erwartet wird eine "
                + "Zahl größer 0. Sie trägt die gesamte Attribution der Fernwartungen; ohne "
                + "sie landet die Arbeit bei niemandem.");
        }

        if (string.IsNullOrWhiteSpace(tanss.TokenRef))
        {
            problems.Add(
                "tanss.token_ref: fehlt. Erwartet wird ein Verweis auf die Ablage des "
                + "Tokens, üblicherweise „dpapi:credentials“.");
        }

        RequireRange(problems, "tanss.rotate_before_days", tanss.RotateBeforeDays, 1, 365,
            "Darunter bliebe keine Zeit für eine misslungene Erneuerung, darüber wäre die "
            + "Erneuerung vor der Ausstellung fällig.");

        RequireRange(problems, "tanss.timeout_seconds", tanss.TimeoutSeconds, 5, 300,
            "Unter 5 Sekunden bricht schon eine normale Antwort ab, über 300 hängt der "
            + "Hintergrunddienst spürbar lange an einer toten Verbindung.");
    }

    private static void CheckWatcher(WatcherSection watcher, List<string> problems)
    {
        RequireRange(problems, "watcher.poll_interval_seconds", watcher.PollIntervalSeconds, 1, 3600,
            "Der Takt ist zugleich die Messgenauigkeit von Sitzungsbeginn und -ende. "
            + "Eine Stunde ist die äußerste sinnvolle Grenze, darüber wäre jede Sitzung "
            + "nur noch geraten.");

        RequireRange(problems, "watcher.ip_session_grace_seconds", watcher.IpSessionGraceSeconds, 0, 86400,
            "0 schaltet die Schonfrist ab; ein kurzer Verbindungsabriss zerfällt dann in "
            + "zwei Fernwartungen, und TANSS dedupliziert nicht.");

        if (watcher.IpSessionGraceSeconds > 0
            && watcher.IpSessionGraceSeconds < watcher.PollIntervalSeconds)
        {
            problems.Add(
                $"watcher.ip_session_grace_seconds: {watcher.IpSessionGraceSeconds} ist "
                + $"kleiner als watcher.poll_interval_seconds ({watcher.PollIntervalSeconds}). "
                + "Die Schonfrist wäre damit wirkungslos, weil zwischen zwei Momentaufnahmen "
                + "bereits mehr Zeit vergeht als sie gewährt.");
        }
    }

    private static void CheckMonitoring(IReadOnlyList<MonitoringEntry> entries, List<string> problems)
    {
        if (entries.Count == 0)
        {
            problems.Add(
                "monitoring: leer. Ohne mindestens eine Regel beobachtet das Werkzeug nichts "
                + "und schreibt entsprechend nie eine Fernwartung.");
            return;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < entries.Count; index++)
        {
            MonitoringEntry entry = entries[index];
            string where = string.Create(CultureInfo.InvariantCulture, $"monitoring[{index}]");

            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                problems.Add($"{where}.key: fehlt. Der Schlüssel benennt das Beobachtungsverfahren, etwa „rdp“.");
            }
            else
            {
                // Gesucht wird der Schluessel spaeter woertlich (AppConfig.MonitorFor). Ein
                // Rand-Leerzeichen bestuende sonst jede Pruefung, und die Regel griffe
                // trotzdem nie - dieselbe stille Wirkungslosigkeit, gegen die diese Datei
                // sonst konsequent vorgeht.
                if (!string.Equals(entry.Key, entry.Key.Trim(), StringComparison.Ordinal))
                {
                    problems.Add(
                        $"{where}.key: „{entry.Key}“ hat ein Leerzeichen am Rand. Gesucht "
                        + "wird der Schlüssel wörtlich; die Regel würde damit nie gefunden "
                        + $"und die Beobachtung liefe stillschweigend leer. Erwartet wird "
                        + $"„{entry.Key.Trim()}“.");
                }

                if (!seen.Add(entry.Key.Trim()))
                {
                    problems.Add(
                        $"{where}.key: „{entry.Key}“ kommt mehrfach vor. Welche der beiden Regeln "
                        + "gälte, wäre reine Reihenfolge — das ist keine Grundlage für eine Buchung.");
                }
            }

            if (entry.RemoteSupportTypeId < MinimumRemoteSupportTypeId)
            {
                problems.Add(
                    $"{where}.remote_support_type_id: {entry.RemoteSupportTypeId} ist kleiner "
                    + $"als {MinimumRemoteSupportTypeId}. Darunter liegen die fest eingebauten "
                    + "Typen des Herstellers; TANSS lehnt den Upload mit 403 TYPE_GREATER_1000 "
                    + "ab — und zwar erst nach getaner Arbeit.");
            }

            CheckExcludes(entry, where, problems);
            CheckPattern(entry, where, problems);
        }
    }

    private static void CheckExcludes(MonitoringEntry entry, string where, List<string> problems)
    {
        for (int index = 0; index < entry.ExcludeIpAddresses.Count; index++)
        {
            string raw = entry.ExcludeIpAddresses[index];
            if (!IpRange.TryParse(raw, out _))
            {
                string field = string.Create(CultureInfo.InvariantCulture,
                    $"{where}.exclude_ip_addresses[{index}]");
                problems.Add(
                    $"{field}: „{raw}“ ist weder eine IP-Adresse noch ein Netz in "
                    + "CIDR-Schreibweise (erwartet etwa 10.0.0.5, 192.168.0.0/16 oder "
                    + "fe80::/10). Ein Tippfehler hier bliebe sonst unbemerkt, und jede "
                    + "Sitzung würde gebucht.");
            }
        }
    }

    private static void CheckPattern(MonitoringEntry entry, string where, List<string> problems)
    {
        if (entry.RegexPatternOverride is null)
        {
            return;
        }

        if (entry.RegexPatternOverride.Length == 0)
        {
            problems.Add(
                $"{where}.regex_pattern_override: leer. Wer den eingebauten Ausdruck behalten "
                + "will, lässt das Feld weg; ein leerer Ausdruck passt auf alles.");
            return;
        }

        try
        {
            _ = Regex.Match(string.Empty, entry.RegexPatternOverride,
                RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException exception)
        {
            problems.Add(
                $"{where}.regex_pattern_override: „{entry.RegexPatternOverride}“ ist kein "
                + $"gültiger regulärer Ausdruck — {exception.Message}");
        }
        catch (RegexMatchTimeoutException)
        {
            problems.Add(
                $"{where}.regex_pattern_override: „{entry.RegexPatternOverride}“ braucht "
                + "schon auf leerem Text länger als eine Sekunde. Gegen eine lange "
                + "Fensterbeschriftung würde er den Beobachter anhalten.");
        }
    }

    private static void CheckProxy(ProxySection proxy, List<string> problems)
    {
        if (!proxy.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(proxy.Address))
        {
            problems.Add("proxy.address: fehlt, obwohl proxy.enabled gesetzt ist.");
        }

        RequireRange(problems, "proxy.port", proxy.Port, 1, 65535,
            "Außerhalb dieses Bereichs gibt es keinen TCP-Port.");

        if (!string.IsNullOrWhiteSpace(proxy.User) && string.IsNullOrWhiteSpace(proxy.PasswordRef))
        {
            problems.Add(
                "proxy.password_ref: fehlt, obwohl proxy.user gesetzt ist. Ein Benutzername "
                + "ohne Kennwort führt zu einer Anmeldeaufforderung, die niemand beantwortet.");
        }
    }

    private static void CheckLogging(LoggingSection logging, List<string> problems)
    {
        if (!LoggingSection.Levels.Contains(logging.Level, StringComparer.Ordinal))
        {
            problems.Add(
                $"logging.level: „{logging.Level}“ ist keine bekannte Stufe. Erlaubt sind "
                + string.Join(", ", LoggingSection.Levels) + " (kleingeschrieben).");
        }

        RequireRange(problems, "logging.retention_days", logging.RetentionDays, 1, 3650,
            "0 löschte das Protokoll sofort und nähme jeder Rückfrage die Grundlage.");
    }

    /// <summary>
    /// Prüft den Abschnitt zur Bildschirmaufzeichnung.
    /// </summary>
    /// <remarks>
    /// <para><b>Geprüft wird auch, was abgeschaltet ist.</b> Ein unsinniger Wert in einem
    /// ruhenden Abschnitt fällt sonst erst an dem Tag auf, an dem jemand die Aufzeichnung
    /// einschaltet — und das ist selten ein guter Tag dafür.</para>
    /// <para><b>Die Kenntnisnahme wird hier nicht verlangt.</b> Ob aufgezeichnet werden darf,
    /// beantwortet <see cref="RecordingSection.IsUsable"/> und sonst niemand. Diese Prüfung
    /// sagt nur, ob die Datei in sich stimmig ist — eine Konfiguration ohne Kenntnisnahme ist
    /// vollkommen gültig, sie zeichnet eben nicht auf.</para>
    /// </remarks>
    /// <param name="recording">Der Abschnitt.</param>
    /// <param name="problems">Die Sammelliste der Beanstandungen.</param>
    private static void CheckRecording(RecordingSection recording, List<string> problems)
    {
        // recording.segment_minutes wird weder geprueft noch bemaengelt noch gemeldet: Der
        // Schluessel bewirkt nichts und faellt beim naechsten Speichern weg. Eine Beanstandung
        // hielte das ganze Werkzeug an - fuer eine Einstellung, die ohnehin ins Leere geht.

        if (!string.IsNullOrWhiteSpace(recording.Scope)
            && !RecordingSection.Scopes.Contains(recording.Scope, StringComparer.Ordinal))
        {
            problems.Add(
                $"recording.scope: „{recording.Scope}“ ist kein gültiger Aufnahmebereich. "
                + $"Erlaubt sind {string.Join(", ", RecordingSection.Scopes)} — „windows“ nimmt "
                + "nur die Fenster der Sitzung auf, „screen“ den ganzen Bildschirm, auf dem sie "
                + "liegt.");
        }

        RequireRange(problems, "recording.retention_days", recording.RetentionDays, 1, 3650,
            "0 löschte die Aufzeichnung noch am selben Tag und machte sie damit wertlos; "
            + "zehn Jahre sind die äusserste Grenze, oberhalb derer keine Aufbewahrung mehr "
            + "zu rechtfertigen wäre.");

        RequireRange(problems, "recording.frames_per_second", recording.FramesPerSecond, 1, 15,
            "Unter einem Bild je Sekunde entgehen Dialoge, die nur kurz stehen. Über fünfzehn "
            + "wächst allein die Datei — die Frage „was wurde getan“ beantwortet eine höhere "
            + "Bildrate nicht besser.");

        RequireRange(problems, "recording.heartbeat_seconds", recording.HeartbeatSeconds, 1, 60,
            "Ohne Herzschlag bekäme ein stehender Bildschirm keine Zeitachse, und der "
            + "Abspieler spränge über die Stille hinweg. Länger als eine Minute wäre keine "
            + "Zeitachse mehr, sondern eine Andeutung.");

        RequireRange(problems, "recording.minimum_free_megabytes",
            recording.MinimumFreeMegabytes, 128, 1_048_576,
            "Eine volle Platte nimmt auch der Warteschlange den Platz, und die trägt die "
            + "Arbeitszeit. Die Aufzeichnung muss vorher zurücktreten.");

        if (recording.HeartbeatSeconds * recording.FramesPerSecond < 1)
        {
            problems.Add(
                "recording.heartbeat_seconds und recording.frames_per_second passen nicht "
                + "zusammen: Der Herzschlag müsste häufiger kommen als die Bildrate überhaupt "
                + "zulässt.");
        }

        // Die Kenntnisnahme ist kein Pflichtfeld - aber eine halb ausgefuellte ist ein Nachweis,
        // der im Ernstfall nichts belegt. Entweder ganz oder gar nicht.
        bool anyLegal = !string.IsNullOrWhiteSpace(recording.AcknowledgedAt)
            || !string.IsNullOrWhiteSpace(recording.AcknowledgedBy)
            || !string.IsNullOrWhiteSpace(recording.LegalBasis)
            || !string.IsNullOrWhiteSpace(recording.LegalReference);

        bool allLegal = !string.IsNullOrWhiteSpace(recording.AcknowledgedAt)
            && !string.IsNullOrWhiteSpace(recording.AcknowledgedBy)
            && !string.IsNullOrWhiteSpace(recording.LegalBasis)
            && !string.IsNullOrWhiteSpace(recording.LegalReference);

        if (anyLegal && !allLegal)
        {
            problems.Add(
                "recording: Die Kenntnisnahme ist unvollständig. Zeitpunkt, Person, "
                + "Rechtsgrundlage und Beleg gehören zusammen — eine Kenntnisnahme ohne "
                + "benannte Grundlage ist ein Haken und kein Nachweis. Entweder alle vier "
                + "Angaben oder keine.");
        }

        if (recording.Directory.Length > 0 && !Path.IsPathFullyQualified(recording.Directory))
        {
            problems.Add(
                $"recording.directory: „{recording.Directory}“ ist kein vollständiger Pfad. "
                + "Ein relativer Pfad zeigte je nach Startverzeichnis woandershin — und "
                + "gelöscht wird später ausschliesslich unterhalb dieses Ordners.");
        }
    }

    /// <summary>
    /// Prüft den Abschnitt zum Verlauf.
    /// </summary>
    /// <remarks>
    /// <para><b>Was hier bewusst nicht geprüft wird:</b> ob <c>history.retention_days</c> über
    /// <c>recording.retention_days</c> liegt. Eine solche Regel hielte das ganze Werkzeug an,
    /// bis der Benutzer die Aufbewahrung personenbezogener Daten <b>verlängert</b> — ein
    /// Startverbot als Hebel zu längerer Speicherung. Stattdessen sagt
    /// <see cref="HistorySection.RetentionDays"/> ausdrücklich „frühestens“: Die Verlaufszeile
    /// bleibt stehen, solange noch eine Aufzeichnung zu ihr liegt, und der Hinweis
    /// „Aufzeichnung gelöscht“ kommt damit auch dann zustande, wenn die Zahlen ungünstig
    /// stehen.</para>
    ///
    /// <para>Beanstandet wird nur, was <b>in sich</b> widersprüchlich ist: eine
    /// Schwärzungsfrist jenseits der Aufbewahrungsfrist käme nie zum Zuge.</para>
    /// </remarks>
    /// <param name="history">Der Abschnitt.</param>
    /// <param name="problems">Die Sammelliste der Beanstandungen.</param>
    private static void CheckHistory(HistorySection history, List<string> problems)
    {
        // history.redact_destination wird nicht geprueft: null ist der Normalfall und heisst
        // "es gilt logging.redact_window_titles". Eine Beanstandung gaebe es hier nur fuer
        // einen Wert, den JSON ohnehin nicht liefern kann.

        RequireRange(problems, "history.retention_days", history.RetentionDays, 1, 3650,
            "0 löschte den Verlauf sofort und nähme der Seite „Verlauf“ ihren Gegenstand; "
            + "zehn Jahre sind die äusserste Grenze, oberhalb derer keine Aufbewahrung von "
            + "Gegenstellen mehr zu rechtfertigen wäre.");

        RequireRange(problems, "history.plain_text_days", history.PlainTextDays, 0, 3650,
            "Die Zahl sagt, wie lange die Gegenstelle im Klartext steht. 0 ist zulässig und "
            + "heisst: beim nächsten Aufräumtakt schwärzen.");

        if (history.PlainTextDays > history.RetentionDays)
        {
            string plain = history.PlainTextDays.ToString(CultureInfo.InvariantCulture);
            string keep = history.RetentionDays.ToString(CultureInfo.InvariantCulture);
            problems.Add(
                $"history.plain_text_days ({plain}) ist grösser als history.retention_days "
                + $"({keep}). Der Klartext soll vor der Zeile verschwinden, nicht nach ihr — "
                + "sonst ist die Schwärzung eine Zusage, die nie zum Zuge kommt.");
        }
    }

    private static void RequireRange(List<string> problems, string field, int value,
                              int minimum, int maximum, string why)
    {
        if (value < minimum || value > maximum)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"{field}: {value} liegt außerhalb von {minimum} bis {maximum}. {why}"));
        }
    }
}
