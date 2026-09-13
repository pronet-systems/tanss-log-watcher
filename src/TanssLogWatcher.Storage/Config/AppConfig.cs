using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using TanssLogWatcher.Api;

namespace TanssLogWatcher.Storage.Config;

/// <summary>
/// Die gesamte Konfiguration des Werkzeugs, so wie sie in <c>config.json</c> steht.
/// </summary>
/// <remarks>
/// <para><b>Unbekannte Felder werden abgelehnt</b> (<see cref="JsonUnmappedMemberHandling.Disallow"/>).
/// Eine vertippte Einstellung, die stillschweigend ignoriert wird, ist schlimmer als ein
/// Fehler: Wer <c>exclude_ip_adresses</c> schreibt, hält seine Ausschlussliste für aktiv,
/// während in Wahrheit jede Sitzung gebucht wird. Der Preis dafür ist, dass eine ältere
/// Programmfassung eine neuere Datei nicht liest — dafür gibt es <see cref="Version"/>.</para>
///
/// <para>Geheimnisse stehen hier <b>nicht</b> drin, nur Verweise darauf
/// (<c>token_ref</c>, <c>password_ref</c>). Das Token selbst liegt DPAPI-verschlüsselt im
/// lokalen Profil, siehe <see cref="Secrets.DpapiTokenStore"/>.</para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppConfig
{
    /// <summary>Höchster Stand, den diese Programmfassung lesen kann.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Stand des Dateiaufbaus. Erlaubt später eine Überführung ohne Rätselraten.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>
    /// Die Sprachmodell-Unterstützung beim Abschluss einer Sitzung.
    /// </summary>
    /// <remarks>
    /// Ausdrücklich ein eigener Abschnitt und nicht eine Handvoll Schalter anderswo: Hier
    /// verlassen zum ersten Mal Daten dieses Werkzeugs das Haus. Wer wissen will, ob das auf
    /// einem Rechner geschieht, soll genau eine Stelle nachsehen müssen.
    /// </remarks>
    public AiSection Ai { get; init; } = new();

    /// <summary>
    /// Bildschirmaufzeichnung der Fernwartungssitzungen. Standardmässig abgeschaltet.
    /// </summary>
    /// <remarks>
    /// Abgeschaltet, bis jemand sie ausdrücklich einschaltet und die Kenntnisnahme erteilt.
    /// Das ist dieselbe Haltung wie bei <see cref="Ai"/>: Eine Funktion, die personenbezogene
    /// Daten erzeugt, geht nicht von selbst an.
    /// </remarks>
    public RecordingSection Recording { get; init; } = new();

    /// <summary>Zugang zur TANSS-Instanz des Kunden.</summary>
    public required TanssSection Tanss { get; init; }

    /// <summary>Taktung der Beobachtung.</summary>
    public WatcherSection Watcher { get; init; } = new();

    /// <summary>Welche Anwendungen beobachtet werden und auf welchen Fernwartungstyp sie buchen.</summary>
    public IReadOnlyList<MonitoringEntry> Monitoring { get; init; } = [];

    /// <summary>Vorgeschalteter Proxy. Bleibt abgeschaltet, wenn keiner nötig ist.</summary>
    public ProxySection Proxy { get; init; } = new();

    /// <summary>Protokollierung auf dem Rechner des Technikers.</summary>
    public LoggingSection Logging { get; init; } = new();

    /// <summary>Der Verlauf abgeschlossener Sitzungen.</summary>
    /// <remarks>
    /// Fehlt der Abschnitt in einer bestehenden <c>config.json</c>, gelten die Vorgaben — er
    /// ist bewusst nicht <c>required</c>. <see cref="JsonUnmappedMemberHandling.Disallow"/>
    /// verbietet <b>unbekannte</b> Felder, nicht fehlende: Eine unveränderte Datei lädt weiter
    /// und bekommt einen Verlauf, keinen Ladefehler.
    /// </remarks>
    public HistorySection History { get; init; } = new();

    /// <summary>Übersetzt in die Netzoptionen der API-Schicht.</summary>
    /// <remarks>
    /// <para>Der Weg führt bewusst nur in diese Richtung. Die API-Schicht kennt weder Dateipfade
    /// noch die Beobachtungsregeln und soll das auch nicht.</para>
    /// <para><b>Das Proxy-Kennwort muss von aussen kommen.</b> In der Konfiguration steht nur
    /// ein Verweis (<see cref="ProxySection.PasswordRef"/>), nie das Geheimnis selbst — und
    /// dieses Stück hat keinen Zugang zum Schlüsselspeicher. Wer ein Kennwort braucht, löst es
    /// vorher auf und reicht es hier herein.</para>
    /// </remarks>
    /// <param name="proxyPassword">
    /// Das aufgelöste Proxy-Kennwort, oder <see langword="null"/>.
    /// <para>Fehlte dieser Parameter, ginge bei einem Proxy mit Anmeldung eine leere Zeichenkette
    /// als Kennwort über die Leitung — der Proxy wiese ab, und die Meldung sähe aus wie ein
    /// Problem mit TANSS. Genau so war es, bevor dieser Parameter hinzukam.</para>
    /// </param>
    public TanssOptions ToTanssOptions(string? proxyPassword = null) => new()
    {
        BaseUrl = Tanss.BaseUrl,
        EmployeeId = Tanss.EmployeeId,
        Timeout = TimeSpan.FromSeconds(Tanss.TimeoutSeconds),
        VerifyTls = Tanss.VerifyTls,
        Proxy = Proxy.Enabled
            ? new ProxyOptions
            {
                Address = Proxy.Address,
                Port = Proxy.Port,
                User = Proxy.User,
                Password = proxyPassword,
            }
            : null,
    };

    /// <summary>
    /// Findet die Beobachtungsregel zu einem Schlüssel, ohne Rücksicht auf Groß- und
    /// Kleinschreibung.
    /// </summary>
    public MonitoringEntry? MonitorFor(string key) =>
        Monitoring.FirstOrDefault(
            entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Prüft die geladene Konfiguration und wirft bei Verstößen.</summary>
    /// <exception cref="ConfigValidationException">Mindestens eine Regel ist verletzt.</exception>
    public void Validate(string? origin = null) => ConfigValidator.Validate(this, origin);
}

/// <summary>Zugang zur TANSS-Instanz.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TanssSection
{
    /// <summary>
    /// Basisadresse einschließlich <c>/backend</c>, ohne Schrägstrich am Ende.
    /// </summary>
    /// <remarks>
    /// Zeigt sie stattdessen auf die Weboberfläche, antwortet die PHP-Oberfläche auf
    /// <b>jede</b> Anfrage mit HTTP 400 — auch ohne Token. Der Fehler sieht dann wie ein
    /// kaputtes Werkzeug aus und nicht wie eine falsche Adresse, deshalb wird er schon
    /// beim Laden abgefangen.
    /// </remarks>
    public required string BaseUrl { get; init; }

    /// <summary>Mitarbeiter-ID des Technikers. Trägt die gesamte Attribution der Fernwartungen.</summary>
    public required int EmployeeId { get; init; }

    /// <summary>
    /// Verweis auf das Arbeitstoken, nicht das Token selbst.
    /// </summary>
    /// <remarks>
    /// Vorgesehen ist <c>dpapi:credentials</c> — die Ablage im lokalen Profil. Der Verweis
    /// steht trotzdem in der Konfiguration, damit später ein zweiter Weg (etwa ein
    /// Firmen-Schlüsselbund) hinzukommen kann, ohne die Datei umzubauen.
    /// </remarks>
    public string TokenRef { get; init; } = "dpapi:credentials";

    /// <summary>
    /// Wie viele Tage vor Ablauf das Token erneuert wird.
    /// </summary>
    /// <remarks>
    /// Bewusst großzügig: Ein Techniker, der drei Wochen im Urlaub war, soll sein Werkzeug
    /// starten können, ohne sich neu anmelden zu müssen.
    /// </remarks>
    public int RotateBeforeDays { get; init; } = 60;

    /// <summary>TLS-Zertifikat prüfen. Nur für Testinstanzen abschaltbar.</summary>
    public bool VerifyTls { get; init; } = true;

    /// <summary>Zeitüberschreitung einzelner Aufrufe in Sekunden.</summary>
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>Taktung der Beobachtung.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WatcherSection
{
    /// <summary>
    /// Abstand zwischen zwei Durchläufen in Sekunden.
    /// </summary>
    /// <remarks>
    /// <para>Die Sitzungsgrenzen werden aus Momentaufnahmen abgeleitet; der Takt ist damit die
    /// Messgenauigkeit von Beginn und Ende. Eine Sekunde heisst: Das Ende einer Fernwartung wird
    /// spätestens eine Sekunde nach dem Schliessen des Fensters bemerkt, und der Abschlussdialog
    /// geht auf, solange der Techniker noch davorsitzt. Bei zehn Sekunden — der früheren
    /// Voreinstellung — war er oft schon weitergezogen.</para>
    /// <para>Was der Takt kostet, steht nicht hier, sondern auf der Diagnoseseite: Sie zeigt die
    /// gemessene Dauer des letzten Durchlaufs. Bleibt sie deutlich unter dem Takt, ist er
    /// tragbar; nähert sie sich ihm, gehört der Wert hier heraufgesetzt.</para>
    /// </remarks>
    public int PollIntervalSeconds { get; init; } = 1;

    /// <summary>
    /// Wie lange eine verschwundene Verbindung zu derselben Gegenstelle noch als dieselbe
    /// Sitzung gilt.
    /// </summary>
    /// <remarks>
    /// Ohne diese Schonfrist zerfiele eine einzige Fernwartung mit kurzem Verbindungsabriss
    /// in mehrere Datensätze — und TANSS dedupliziert nicht, die stünden also alle beim
    /// Kunden. Drei Minuten decken einen WLAN-Wechsel und einen Neustart der Gegenstelle ab.
    /// </remarks>
    public int IpSessionGraceSeconds { get; init; } = 180;
}

/// <summary>Eine beobachtete Anwendung und ihre Zuordnung nach TANSS.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MonitoringEntry
{
    /// <summary>
    /// Schlüssel der Beobachtungsregel, etwa <c>rdp</c>, <c>anydesk</c>, <c>putty</c>.
    /// </summary>
    /// <remarks>
    /// Die Beobachtungsschicht bringt zu jedem bekannten Schlüssel ein eigenes Verfahren mit
    /// (Prozessname, Fensterbeschriftung, Protokolldatei). Ein unbekannter Schlüssel wird
    /// deshalb hier <b>nicht</b> abgelehnt — die Prüfung gegen die tatsächlich vorhandenen
    /// Verfahren gehört dorthin, wo sie bekannt sind. Sonst müsste diese Datei bei jedem
    /// neuen Verfahren mitwachsen.
    /// </remarks>
    public required string Key { get; init; }

    /// <summary>
    /// Kennung der externen Fernwartungsanbindung in TANSS. Muss mindestens 1000 sein.
    /// </summary>
    /// <remarks>
    /// Unterhalb von 1000 liegen die fest eingebauten Typen des Herstellers; TANSS antwortet
    /// darauf mit <c>403 TYPE_GREATER_1000</c>. Die Prüfung steht hier, weil der Fehler
    /// sonst erst beim ersten echten Upload aufträte — also nach getaner Arbeit, deren
    /// Sitzung dann in der Warteschlange festhinge.
    /// </remarks>
    public required int RemoteSupportTypeId { get; init; }

    /// <summary>
    /// Gegenstellen, die nicht als Fernwartung zählen: eigene Server, Testgeräte, das
    /// eigene Rechenzentrum. Einzeladressen oder Netze in CIDR-Schreibweise.
    /// </summary>
    public IReadOnlyList<string> ExcludeIpAddresses { get; init; } = [];

    /// <summary>
    /// Ersetzt den eingebauten Ausdruck, mit dem die Gegenstelle aus Fensterbeschriftung
    /// oder Protokollzeile gelesen wird.
    /// </summary>
    /// <remarks>
    /// Gedacht für abweichende Sprachfassungen und angepasste Beschriftungen. Wird beim
    /// Laden übersetzt, damit ein fehlerhafter Ausdruck sofort auffällt und nicht erst bei
    /// der ersten Sitzung.
    /// </remarks>
    public string? RegexPatternOverride { get; init; }

    /// <summary>Die Ausschlussliste als geprüfte Bereiche.</summary>
    /// <exception cref="FormatException">Ein Eintrag ist weder Adresse noch Netz.</exception>
    public IReadOnlyList<IpRange> ParseExcludedRanges() =>
        new ReadOnlyCollection<IpRange>([.. ExcludeIpAddresses.Select(IpRange.Parse)]);
}

/// <summary>Vorgeschalteter Proxy.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProxySection
{
    /// <summary>Proxy verwenden.</summary>
    public bool Enabled { get; init; }

    /// <summary>Adresse ohne Schema und ohne Portangabe.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>Port des Proxys.</summary>
    public int Port { get; init; } = 8080;

    /// <summary>Benutzername, falls der Proxy eine Anmeldung verlangt.</summary>
    public string? User { get; init; }

    /// <summary>Verweis auf das Proxy-Kennwort, nicht das Kennwort selbst.</summary>
    public string? PasswordRef { get; init; }
}

/// <summary>Protokollierung auf dem Rechner des Technikers.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LoggingSection
{
    /// <summary>Erlaubte Stufen. Kleingeschrieben, damit die Datei von Hand lesbar bleibt.</summary>
    public static readonly IReadOnlyList<string> Levels = ["debug", "info", "warning", "error"];

    /// <summary>Protokollstufe.</summary>
    public string Level { get; init; } = "info";

    /// <summary>
    /// Fensterbeschriftungen beim Protokollieren schwärzen.
    /// </summary>
    /// <remarks>
    /// <para><b>Voreingestellt aus.</b> Bei den Profilen, die im Betrieb wirklich benutzt
    /// werden — Microsoft Remotedesktop, der Store-Client, PuTTY —, trägt der Titel einen
    /// Rechnernamen und sonst nichts. Ein Abdruck statt des Namens kostet dort jede
    /// Nachvollziehbarkeit und verhindert wenig: Derselbe Name steht ohnehin im Kommentar der
    /// Fernwartung, die nach TANSS geht. Seit es die Seite „Verlauf“ gibt, entscheidet diese
    /// Einstellung zusätzlich, ob dort eine Gegenstelle oder der Satz „Gegenstelle
    /// geschwärzt“ steht.</para>
    ///
    /// <para><b>Einschalten, sobald ein Profil mehr als einen Rechnernamen in den Titel
    /// nimmt.</b> Zehn der Profile im Katalog nehmen den <b>ganzen</b> Fenstertitel als Ziel
    /// (unter anderem <c>cmd</c>, <c>powershell</c>, <c>RoyalTS</c>, <c>WindowsTerminal</c>),
    /// und die beiden Outlook-Profile nehmen den <b>Nachrichtenbetreff</b>. Wer eines davon
    /// benutzt, sollte hier <see langword="true"/> setzen — sonst liegen Betreffzeilen und
    /// Dokumentnamen im Klartext in <c>state.db</c>.</para>
    ///
    /// <para>Wirksam wird die Einstellung in
    /// <see cref="Logging.SessionLog"/>: Sie wird dem Protokoll beim Öffnen mitgegeben und
    /// entscheidet dort über <see cref="Logging.SessionLogEntry.WindowTitle"/>. Eine
    /// Einstellung, die geschrieben und gelesen, aber nirgends ausgewertet wird, ist die
    /// gefährlichste Sorte: Der Techniker hält die Schwärzung für aktiv, während die
    /// Beschriftungen im Klartext auf der Platte liegen.</para>
    /// </remarks>
    public bool RedactWindowTitles { get; init; }

    /// <summary>Wie lange Protokolleinträge aufgehoben werden, in Tagen.</summary>
    public int RetentionDays { get; init; } = 30;
}

/// <summary>
/// Der Verlauf abgeschlossener Sitzungen.
/// </summary>
/// <remarks>
/// <para><b>Hier liegt zum ersten Mal eine Gegenstelle absichtlich und benannt auf der
/// Platte.</b> Sie liegt zwar schon heute dort — in der Nutzlast des erledigten
/// Warteschlangeneintrags, ungeschwärzt —, aber dort ist sie ein Nebenprodukt des Versands,
/// das <see cref="StatePruner"/> nach <see cref="LoggingSection.RetentionDays"/> wieder
/// wegräumt. Dieser Abschnitt macht daraus einen benannten Bestand mit benannter Frist, und
/// das ist der Unterschied, auf den es ankommt.</para>
///
/// <para><b>Die Gegenstelle ist derselbe Text, den
/// <see cref="LoggingSection.RedactWindowTitles"/> verbirgt.</b> Zehn der sechsunddreissig
/// Profile nehmen den ganzen Fenstertitel als Ziel, die beiden Outlook-Profile den
/// Nachrichtenbetreff. Deshalb hat <see cref="RedactDestination"/> keine eigene Vorgabe,
/// sondern folgt der Schwärzung des Protokolls — wer sie eingeschaltet hat, soll sie nicht
/// dadurch verlieren, dass er eine neuere Programmfassung einspielt.</para>
///
/// <para><b>Die Frist ist zweistufig.</b> Nach <see cref="PlainTextDays"/> verschwindet der
/// Klartext der Gegenstelle und es bleibt ihr Abdruck; nach <see cref="RetentionDays"/>
/// verschwindet die Zeile. Dieselbe Bauform wie der Grabstein einer gelöschten Aufzeichnung:
/// Die Auskunft, was es gab, überlebt die Angabe, um wen es ging.</para>
///
/// <para><b>Beide Fristen gehören in denselben Aufräumtakt</b>, der schon Protokoll und
/// Warteschlange räumt. Eine Frist, die in der Datei steht und nirgends vollzogen wird, ist
/// keine Frist, sondern eine Behauptung — und drei nebeneinander stehende Zahlen, von denen
/// eine nicht läuft, sind schlimmer als zwei.</para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HistorySection
{
    /// <summary>Der vorgegebene Aufbewahrungszeitraum in Tagen.</summary>
    /// <remarks>
    /// Neunzig Tage, und die Zahl folgt zwei Gründen. Erstens wird eine Fernwartung monatlich
    /// abgerechnet und bis zum Ende des Folgemonats beanstandet — das sind bis zu sechzig Tage;
    /// neunzig ist das nächste volle Quartal darüber. Zweitens liegt sie über den dreissig
    /// Tagen aus <see cref="RecordingSection.DefaultRetentionDays"/>: Verschwände die
    /// Verlaufszeile zugleich mit der Aufzeichnung, wäre der Hinweis „Aufzeichnung gelöscht“
    /// nie zu sehen, wegen dessen der Verlauf überhaupt gebaut ist.
    /// </remarks>
    public const int DefaultRetentionDays = 90;

    /// <summary>Wie lange die Gegenstelle vorgabegemäss im Klartext steht, in Tagen.</summary>
    /// <remarks>
    /// Dieselbe Zahl wie die Vorgabe von <see cref="LoggingSection.RetentionDays"/>, damit zwei
    /// Fristen für denselben Namen nicht auseinanderlaufen.
    /// </remarks>
    public const int DefaultPlainTextDays = 30;

    /// <summary>Soll überhaupt ein Verlauf geführt werden?</summary>
    /// <remarks>
    /// Voreingestellt an — anders als <see cref="RecordingSection"/> und
    /// <see cref="AiSection"/>, die von selbst nicht angehen. Die beiden erzeugen etwas Neues:
    /// ein Video vom Bildschirm des Kunden, eine Abfrage an einen Anbieter im Ausland. Der
    /// Verlauf erzeugt nichts Neues, er ordnet, was ohnehin in <c>state.db</c> steht, und hält
    /// die Gegenstelle nur so fest, wie <see cref="ShouldRedactDestination"/> es zulässt. Bei
    /// eingeschalteter Schwärzung steht darin kein Klartext, der nicht schon dort stünde.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>Nach wie vielen Tagen eine Verlaufszeile frühestens entfernt wird.</summary>
    /// <remarks>
    /// <para><b>„Frühestens“ steht hier mit Absicht.</b> Solange zu einer Sitzung noch eine
    /// Aufzeichnung auf der Platte liegt, bleibt ihre Verlaufszeile stehen: Ein Video, zu dem
    /// kein Eintrag mehr sagt, wozu es gehört, wäre schlimmer als der Eintrag. Wer
    /// <c>recording.retention_days</c> über diese Zahl hebt, verlängert damit auch die
    /// Verlaufszeilen der aufgezeichneten Sitzungen.</para>
    ///
    /// <para><b>Erzwungen wird die Reihenfolge nicht</b>, und das ist die Entscheidung hinter
    /// dieser Formulierung. Eine Prüfung, die <c>history.retention_days &gt;=
    /// recording.retention_days</c> verlangte, hielte das ganze Werkzeug an, bis der Benutzer
    /// die Aufbewahrung personenbezogener Daten <b>verlängert</b> — ein Startverbot als Hebel
    /// zu längerer Speicherung ist der falsche Hebel. Nötig wäre sie auch nicht: Der Klartext
    /// ist nach <see cref="PlainTextDays"/> ohnehin fort, die überlebende Zeile trägt dann nur
    /// noch einen Abdruck.</para>
    /// </remarks>
    public int RetentionDays { get; init; } = DefaultRetentionDays;

    /// <summary>
    /// Die Gegenstelle gar nicht erst im Klartext festhalten. <c>null</c> folgt dem Protokoll.
    /// </summary>
    /// <remarks>
    /// <para><b>Absichtlich <c>bool?</c> und absichtlich ohne eigenen Vorgabewert.</b> Fehlt
    /// der Wert — und er fehlt in jeder bestehenden Datei —, entscheidet
    /// <see cref="LoggingSection.RedactWindowTitles"/>. Eine feste Vorgabe <c>false</c>
    /// schriebe auf einem Arbeitsplatz, an dem die Schwärzung eingeschaltet ist, genau die
    /// Zeichenkette im Klartext auf die Platte, die der Techniker verbergen liess — ohne dass
    /// er etwas geändert hätte. Eine Vorgabe darf eine ausdrückliche Entscheidung des
    /// Benutzers nicht umkehren.</para>
    ///
    /// <para>Der Schalter bleibt trotzdem eigenständig: Wer den Klartext im Verlauf will,
    /// obwohl das Protokoll schwärzt, trägt <c>false</c> ein; wer umgekehrt nur den Verlauf
    /// schwärzen will, <c>true</c>. Beides steht dann als eigene Entscheidung in der Datei und
    /// nicht als Nebenwirkung eines Vorgabewerts.</para>
    ///
    /// <para>Aufgelöst wird der fehlende Wert ausschliesslich in
    /// <see cref="ShouldRedactDestination"/>. Ein <c>?? false</c> an irgendeiner Aufrufstelle
    /// wäre derselbe Schaden noch einmal.</para>
    /// </remarks>
    public bool? RedactDestination { get; init; }

    /// <summary>Nach wie vielen Tagen der Klartext der Gegenstelle entfernt wird.</summary>
    /// <remarks>
    /// Greift nur, wo überhaupt Klartext festgehalten wird — siehe
    /// <see cref="ShouldRedactDestination"/>. <c>0</c> ist zulässig und heisst: beim nächsten
    /// Aufräumtakt schwärzen.
    /// </remarks>
    public int PlainTextDays { get; init; } = DefaultPlainTextDays;

    /// <summary>Entscheidet, ob die Gegenstelle im Verlauf geschwärzt wird.</summary>
    /// <param name="logging">Der Protokollabschnitt derselben Konfiguration.</param>
    /// <returns>
    /// <see cref="RedactDestination"/>, sofern gesetzt; sonst
    /// <see cref="LoggingSection.RedactWindowTitles"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="logging"/> fehlt.</exception>
    public bool ShouldRedactDestination(LoggingSection logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        // Die eine Stelle, an der der fehlende Wert aufgeloest wird. Sie liegt hier und nicht
        // beim Aufrufer, damit niemand versehentlich ein anderes Ersatzverhalten waehlt - ein
        // vorgelagertes "?? false" kehrte die Entscheidung des Benutzers still um.
        return RedactDestination ?? logging.RedactWindowTitles;
    }
}

/// <summary>
/// Die Sprachmodell-Unterstützung beim Abschluss einer Sitzung.
/// </summary>
/// <remarks>
/// <para><b>Hier verlässt zum ersten Mal etwas das Haus.</b> Alles übrige in diesem Werkzeug
/// spricht ausschliesslich mit der TANSS-Instanz des Kunden. Wird diese Unterstützung
/// eingeschaltet, geht der Sitzungsbericht an einen Anbieter in den Vereinigten Staaten —
/// mit allem, was darin steht: Zielsystem, Gerätename, Arbeitsplatz, Techniker.</para>
///
/// <para><b>Deshalb ist sie abgeschaltet und bleibt es ohne ausdrückliche Einwilligung.</b>
/// <see cref="Enabled"/> allein genügt nicht; ohne <see cref="ConsentGivenAt"/> sendet das
/// Werkzeug nichts. Die Einwilligung wird mit Zeitpunkt und Windows-Benutzer festgehalten,
/// damit später nachweisbar ist, wer sie wann erteilt hat — das ist der Zweck der beiden
/// Felder, nicht Buchhaltung.</para>
///
/// <para><b>Was das rechtlich bedeutet</b>, entscheidet nicht dieses Werkzeug: Eine Übermittlung
/// personenbezogener Daten in ein Drittland verlangt eine Rechtsgrundlage, einen
/// Auftragsverarbeitungsvertrag mit dem Anbieter und in aller Regel die Beteiligung von
/// Datenschutzbeauftragtem und Mitbestimmung. Die Einwilligungsmaske sagt das; entschieden
/// wird es ausserhalb.</para>
/// </remarks>
public sealed record AiSection
{
    /// <summary>Soll die Unterstützung überhaupt angeboten werden?</summary>
    public bool Enabled { get; init; }

    /// <summary>Der Anbieter: <c>anthropic</c> oder <c>openai</c>.</summary>
    public string Provider { get; init; } = "anthropic";

    /// <summary>
    /// Die Kennung des Modells, etwa <c>claude-opus-5</c>.
    /// </summary>
    /// <remarks>
    /// Leer, solange keines gewählt wurde. Die Auswahl kommt aus der Modellliste des Anbieters
    /// und steht bewusst nicht fest im Programm: Modelle kommen und gehen, und ein fest
    /// eingebauter Name wäre an dem Tag falsch, an dem der Anbieter ihn abkündigt.
    /// </remarks>
    public string Model { get; init; } = string.Empty;

    /// <summary>Verweis auf den Schlüssel; er selbst liegt DPAPI-verschlüsselt im Profil.</summary>
    public string KeyRef { get; init; } = "dpapi:ai";

    /// <summary>
    /// Wann die Einwilligung erteilt wurde; <c>null</c> heisst: gar nicht.
    /// </summary>
    /// <remarks>
    /// Als Zeichenkette in ISO-Schreibweise und nicht als Zeitstempeltyp, damit die Datei von
    /// Hand lesbar bleibt — sie ist im Zweifel ein Nachweis.
    /// </remarks>
    public string? ConsentGivenAt { get; init; }

    /// <summary>Der Windows-Benutzer, der die Einwilligung erteilt hat.</summary>
    public string? ConsentBy { get; init; }

    /// <summary>
    /// Namen von Zielsystemen und Arbeitsplätzen vor dem Senden unkenntlich machen.
    /// </summary>
    /// <remarks>
    /// Standardmässig an, und das ist mehr als Beiwerk: Für eine Rechtschreibprüfung muss kein
    /// Anbieter wissen, dass der Kunde einen Server namens <c>sap</c> betreibt. Ersetzt werden
    /// die bekannten Namen vor dem Senden durch Platzhalter und danach wieder zurück — was
    /// zurückkommt, liest sich unverändert.
    /// </remarks>
    public bool RedactIdentifiers { get; init; } = true;

    /// <summary>Liegt eine erteilte Einwilligung vor?</summary>
    /// <summary>
    /// Die Rolle, die dem Modell vorangestellt wird; leer heisst: der eingebaute Text.
    /// </summary>
    /// <remarks>
    /// <para><b>Bearbeitbar, weil die Berichte eures Hauses sind.</b> Wie ein Bericht klingen
    /// soll, weiss der Betrieb und nicht dieses Werkzeug — ob geduzt oder gesiezt wird, ob
    /// Fachbegriffe stehen bleiben, ob der Kunde ein Krankenhaus oder eine Werkstatt ist.</para>
    /// <para><b>Die vier unverhandelbaren Regeln bleiben trotzdem.</b> Sie werden beim Senden
    /// angehängt und lassen sich hier nicht entfernen: nichts erfinden, keine Zahlen und Namen
    /// ändern, die Sprache behalten, ausschliesslich mit dem Text antworten. Die ersten beiden
    /// sind die Zusage, auf die hin eingewilligt wurde; die letzte ist Technik, ohne sie
    /// landete eine Vorrede des Modells mitten im Bericht.</para>
    /// </remarks>
    public string? RolePrompt { get; init; }

    /// <summary>Der Auftrag für die Rechtschreibprüfung; leer heisst: der eingebaute Text.</summary>
    public string? ProofreadPrompt { get; init; }

    /// <summary>Der Auftrag für das Ausformulieren; leer heisst: der eingebaute Text.</summary>
    public string? ImprovePrompt { get; init; }

    [JsonIgnore]
    public bool HasConsent => !string.IsNullOrWhiteSpace(ConsentGivenAt);

    /// <summary>
    /// Darf tatsächlich gesendet werden?
    /// </summary>
    /// <remarks>
    /// Die einzige Stelle, an der diese Frage beantwortet wird. Drei Bedingungen, und alle drei
    /// müssen gelten: eingeschaltet, eingewilligt, ein Modell gewählt.
    /// </remarks>
    [JsonIgnore]
    public bool IsUsable => Enabled && HasConsent && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>
/// Bildschirmaufzeichnung der Fernwartungssitzungen.
/// </summary>
/// <remarks>
/// <para><b>Warum das schärfer geregelt ist als alles andere in dieser Datei.</b> Eine
/// Aufzeichnung zeigt den Bildschirm des Kunden — mit dessen Daten und den Daten Dritter — und
/// den Bildschirm des Technikers, was Leistungs- und Verhaltenskontrolle im Sinne von
/// §87 Abs. 1 Nr. 6 BetrVG ist. Deshalb genügt hier kein Schalter: Es braucht eine
/// Kenntnisnahme, und die ist nur gültig, wenn die Grundlage benannt ist.</para>
///
/// <para><b>Die Löschfrist ist ein Versprechen, kein Vorschlag.</b> Sie steht hier in Tagen;
/// der tatsächliche Löschzeitpunkt einer einzelnen Aufzeichnung wird beim Abschluss daraus
/// berechnet und festgeschrieben. Wer die Frist später verkürzt, verkürzt auch die
/// bestehenden; wer sie verlängert, verlängert die bestehenden <b>nicht</b> — ein gegebenes
/// Versprechen wird nicht nachträglich gedehnt.</para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecordingSection
{
    /// <summary>Der vorgegebene Aufbewahrungszeitraum in Tagen.</summary>
    /// <remarks>
    /// Dreissig Tage sind die Voreinstellung, weil eine Fernwartung üblicherweise binnen eines
    /// Monats beanstandet wird — danach überwiegt das Interesse an der Löschung. Der Wert ist
    /// eine Voreinstellung und keine Empfehlung: Was zulässig ist, ergibt sich aus dem
    /// Vertrag mit dem Kunden, nicht aus dieser Zahl.
    /// </remarks>
    public const int DefaultRetentionDays = 30;

    /// <summary>Soll überhaupt aufgezeichnet werden?</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Die erlaubten Aufnahmebereiche.
    /// </summary>
    /// <remarks>
    /// Kleingeschriebene Zeichenketten wie <see cref="LoggingSection.Levels"/> und nicht ein
    /// Wahrheitswert: Ein Wahrheitswert benennt immer nur einen der beiden Fälle. Wer
    /// <c>"capture_full_screen": false</c> liest, erfährt, was <b>nicht</b> geschieht, und muss
    /// raten, was statt dessen aufgenommen wird. <c>"scope": "windows"</c> sagt beides in einem
    /// Wort — und liesse sich um einen dritten Fall erweitern, ohne dass zwei Wahrheitswerte
    /// einander widersprechen könnten.
    /// </remarks>
    public static readonly IReadOnlyList<string> Scopes = ["windows", "screen"];

    /// <summary>Der Aufnahmebereich, mit dem eine Installation anfängt.</summary>
    public const string DefaultScope = "windows";

    /// <summary>
    /// Was aufgenommen wird: <c>windows</c> — nur die Fenster der Sitzung — oder
    /// <c>screen</c> — der ganze Bildschirm, auf dem die Sitzung liegt.
    /// </summary>
    /// <remarks>
    /// <para><b>Die engere Betriebsart ist die Voreinstellung</b>, und das ist keine
    /// Bequemlichkeit: Im Fenstermodus landet nachweislich kein fremdes Fenster im Bild, auch
    /// keines, das über der Sitzung liegt. Wer den ganzen Bildschirm aufzeichnen will,
    /// entscheidet das und verantwortet es.</para>
    /// <para>Der Unterschied ist kein technischer, sondern einer im Kreis der Betroffenen —
    /// deshalb entwertet ein Wechsel auf <c>screen</c> die Kenntnisnahme: Eine für die
    /// Fensteraufnahme erteilte deckt die Aufnahme des ganzen Bildschirms nicht.</para>
    /// </remarks>
    public string Scope { get; init; } = DefaultScope;

    /// <summary>Wird der ganze Bildschirm aufgezeichnet?</summary>
    /// <remarks>Die einzige Stelle, an der <see cref="Scope"/> ausgewertet wird.</remarks>
    [JsonIgnore]
    public bool CapturesScreen =>
        string.Equals(Scope, "screen", StringComparison.Ordinal);

    /// <summary>
    /// Wohin die Aufzeichnungen gehen; leer heisst: der vorgesehene Ort im Profil.
    /// </summary>
    /// <remarks>
    /// Ein eigener Ort ist der Normalfall — Videodateien gehören selten auf dieselbe Platte
    /// wie das Betriebssystem. Gelöscht wird ausschliesslich unterhalb dieses Ordners, und
    /// zwar nur, was das Werkzeug selbst angelegt hat.
    /// </remarks>
    public string Directory { get; init; } = string.Empty;

    /// <summary>Nach wie vielen Tagen eine Aufzeichnung gelöscht wird.</summary>
    public int RetentionDays { get; init; } = DefaultRetentionDays;

    /// <summary>
    /// Wie viele Bilder je Sekunde höchstens aufgezeichnet werden.
    /// </summary>
    /// <remarks>
    /// <para>Vier, und die Zahl folgt dem Zweck. Belegt werden soll, welcher Bildschirmzustand
    /// bestand, während der Techniker handelte — nicht, wie sich die Maus bewegte. Der
    /// kleinste belegenswerte Zustand ist einer, den ein Mensch lesen und auf den er reagieren
    /// kann; Dialoge und Bestätigungsabfragen stehen Sekunden. Bei vier Bildern je Sekunde
    /// liegt jeder Zustand ab einer Viertelsekunde in mindestens einem Bild.</para>
    /// <para>Flüssige Mausbewegung bräuchte das Vier- bis Achtfache an Daten für eine
    /// Information, die die Frage „was wurde getan“ nicht anders beantwortet.</para>
    /// </remarks>
    public int FramesPerSecond { get; init; } = 4;

    /// <summary>
    /// Nach wie vielen Sekunden ohne Bildänderung trotzdem ein Bild geschrieben wird.
    /// </summary>
    /// <remarks>
    /// Ein stehender Bildschirm liefert keine Bilder; ohne diesen Herzschlag hätte die Datei
    /// an dieser Stelle eine Lücke und der Abspieler spränge darüber hinweg. Zwei Sekunden
    /// kosten bei stehendem Bild fast nichts und halten die Zeitachse ehrlich.
    /// </remarks>
    public int HeartbeatSeconds { get; init; } = 2;

    /// <summary>
    /// Unterhalb wie vieler freier Megabyte nicht mehr aufgezeichnet wird.
    /// </summary>
    /// <remarks>
    /// Eine volle Platte ist schlimmer als eine fehlende Aufzeichnung: Sie nimmt auch der
    /// Warteschlange den Platz, und die trägt die Arbeitszeit. Die Aufzeichnung tritt deshalb
    /// zurück, bevor es eng wird, und sagt es.
    /// </remarks>
    public int MinimumFreeMegabytes { get; init; } = 2048;

    /// <summary>Wann die Kenntnisnahme erteilt wurde; <c>null</c> heisst: gar nicht.</summary>
    public string? AcknowledgedAt { get; init; }

    /// <summary>Der Windows-Benutzer, der die Kenntnisnahme erteilt hat.</summary>
    public string? AcknowledgedBy { get; init; }

    /// <summary>
    /// Für welchen Aufnahmebereich die Kenntnisnahme erteilt wurde.
    /// </summary>
    /// <remarks>
    /// <para><b>Eine Kenntnisnahme gilt für das, was man zur Kenntnis genommen hat.</b> Wer
    /// bestätigt hat, dass die Fenster der Sitzung aufgezeichnet werden, hat nicht bestätigt,
    /// dass sein ganzer Bildschirm aufgezeichnet wird — das ist ein anderer Kreis von
    /// Betroffenen und ein anderer Eingriff. Stimmt dieser Wert nicht mit
    /// <see cref="Scope"/> überein, zählt die Kenntnisnahme nicht, und es wird nicht
    /// aufgezeichnet, bis sie erneut erteilt ist.</para>
    /// <para><c>null</c> in einer Datei aus einer älteren Fassung: Damals gab es nur die
    /// Fensteraufnahme, also gilt die Kenntnisnahme für <c>windows</c>.</para>
    /// </remarks>
    public string? AcknowledgedScope { get; init; }

    /// <summary>
    /// Die Rechtsgrundlage für die Aufzeichnung des Technikerbildschirms.
    /// </summary>
    /// <remarks>
    /// Bewusst ohne Vorbelegung. Eine vorausgefüllte Rechtsgrundlage wäre eine Behauptung des
    /// Werkzeugs über einen Sachverhalt, den nur der Betrieb kennt — etwa das Bestehen einer
    /// Betriebsvereinbarung. Hier steht, was jemand ausgewählt und verantwortet hat.
    /// </remarks>
    public string? LegalBasis { get; init; }

    /// <summary>
    /// Der Beleg: Betriebsvereinbarung, Beschluss, Vertragsnummer — was auch immer trägt.
    /// </summary>
    /// <remarks>
    /// Freitext, weil kein Werkzeug wissen kann, wie der Beleg im jeweiligen Haus heisst.
    /// Er muss vorhanden sein, damit die Kenntnisnahme zählt: Eine Kenntnisnahme ohne
    /// benannte Grundlage ist ein Haken, kein Nachweis.
    /// </remarks>
    public string? LegalReference { get; init; }

    /// <summary>Der Aufnahmebereich in einem Wort, für eine Meldung.</summary>
    /// <param name="scope">Der Bereich.</param>
    public static string Describe(string scope) =>
        string.Equals(scope, "screen", StringComparison.Ordinal)
            ? "der ganze Bildschirm"
            : "nur die Fenster der Sitzung";

    /// <summary>Liegt eine erteilte Kenntnisnahme vor?</summary>
    [JsonIgnore]
    public bool HasAcknowledgement => !string.IsNullOrWhiteSpace(AcknowledgedAt);

    /// <summary>
    /// Darf tatsächlich aufgezeichnet werden?
    /// </summary>
    /// <remarks>
    /// Die einzige Stelle, an der diese Frage beantwortet wird — wie
    /// <see cref="AiSection.IsUsable"/>. Vier Bedingungen: eingeschaltet, Kenntnisnahme
    /// erteilt, Rechtsgrundlage benannt, Beleg benannt. Sie stehen einmal, nämlich in
    /// <see cref="UnusableReason"/>; hier wird nur geprüft, ob dort etwas steht.
    /// </remarks>
    [JsonIgnore]
    public bool IsUsable => UnusableReason is null;

    /// <summary>
    /// Warum nicht aufgezeichnet wird — oder <c>null</c>, wenn aufgezeichnet werden darf.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Grund steht neben der Entscheidung und nicht woanders.</b> Ein Werkzeug,
    /// das an einer Stelle entscheidet und an einer anderen begründet, begründet irgendwann
    /// etwas anderes, als es entschieden hat — und wird an beiden Stellen nicht mehr
    /// geglaubt.</para>
    /// <para>Der Satz ist für Menschen geschrieben: Er steht so in der Einstellungsseite, im
    /// Zustand des Dienstes und im Änderungsprotokoll.</para>
    /// </remarks>
    [JsonIgnore]
    public string? UnusableReason
    {
        get
        {
            if (!Enabled)
            {
                return "Die Bildschirmaufzeichnung ist abgeschaltet.";
            }

            if (!HasAcknowledgement)
            {
                return "Die Bildschirmaufzeichnung ist eingeschaltet, aber die Kenntnisnahme "
                    + "fehlt. Es wird nichts aufgezeichnet, bis sie in den Einstellungen "
                    + "erteilt ist.";
            }

            if (string.IsNullOrWhiteSpace(LegalBasis)
                || string.IsNullOrWhiteSpace(LegalReference))
            {
                return "Die Kenntnisnahme ist unvollständig: Rechtsgrundlage und Beleg fehlen. "
                    + "Eine Kenntnisnahme ohne benannte Grundlage ist ein Haken und kein "
                    + "Nachweis.";
            }

            // Eine aeltere Datei kennt den Bereich nicht; damals gab es nur die Fensteraufnahme.
            string acknowledged = string.IsNullOrWhiteSpace(AcknowledgedScope)
                ? DefaultScope
                : AcknowledgedScope;

            if (!string.Equals(acknowledged, Scope, StringComparison.Ordinal))
            {
                return "Der Aufnahmebereich ist geändert worden, die Kenntnisnahme gilt aber "
                    + $"noch für „{Describe(acknowledged)}“. Wer bestätigt hat, dass die Fenster "
                    + "der Sitzung aufgezeichnet werden, hat nicht bestätigt, dass sein ganzer "
                    + "Bildschirm aufgezeichnet wird. Die Kenntnisnahme ist erneut zu erteilen.";
            }

            return null;
        }
    }
}
