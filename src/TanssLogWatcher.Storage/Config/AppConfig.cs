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
    /// <para>Standardmäßig an. Eine Fensterbeschriftung trägt regelmäßig Kundennamen,
    /// Rechnernamen und gelegentlich Benutzerkonten — sie gehört in die Fernwartung bei
    /// TANSS, nicht in eine Protokolldatei im Benutzerprofil.</para>
    ///
    /// <para>Wirksam wird die Einstellung in
    /// <see cref="Logging.SessionLog"/>: Sie wird dem Protokoll beim Öffnen mitgegeben und
    /// entscheidet dort über <see cref="Logging.SessionLogEntry.WindowTitle"/>. Eine
    /// Einstellung, die geschrieben und gelesen, aber nirgends ausgewertet wird, ist die
    /// gefährlichste Sorte: Der Techniker hält die Schwärzung für aktiv, während die
    /// Beschriftungen im Klartext auf der Platte liegen.</para>
    /// </remarks>
    public bool RedactWindowTitles { get; init; } = true;

    /// <summary>Wie lange Protokolleinträge aufgehoben werden, in Tagen.</summary>
    public int RetentionDays { get; init; } = 30;
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

    /// <summary>Nach wie vielen Minuten eine neue Datei begonnen wird.</summary>
    /// <remarks>
    /// Eine lange Fernwartung in einer einzigen Datei ist beim ersten Fehler ganz verloren —
    /// ohne Abschluss gibt es keinen Index und damit nichts Abspielbares. Abschnitte begrenzen
    /// den Schaden auf den letzten angefangenen.
    /// </remarks>
    public int SegmentMinutes { get; init; } = 10;

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

    /// <summary>Liegt eine erteilte Kenntnisnahme vor?</summary>
    [JsonIgnore]
    public bool HasAcknowledgement => !string.IsNullOrWhiteSpace(AcknowledgedAt);

    /// <summary>
    /// Darf tatsächlich aufgezeichnet werden?
    /// </summary>
    /// <remarks>
    /// Die einzige Stelle, an der diese Frage beantwortet wird — wie
    /// <see cref="AiSection.IsUsable"/>. Vier Bedingungen: eingeschaltet, Kenntnisnahme
    /// erteilt, Rechtsgrundlage benannt, Beleg benannt.
    /// </remarks>
    [JsonIgnore]
    public bool IsUsable => Enabled
        && HasAcknowledgement
        && !string.IsNullOrWhiteSpace(LegalBasis)
        && !string.IsNullOrWhiteSpace(LegalReference);
}
