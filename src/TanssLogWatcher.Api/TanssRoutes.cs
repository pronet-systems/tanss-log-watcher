namespace TanssLogWatcher.Api;

/// <summary>
/// Die Routen, die dieses Werkzeug benutzt — und die Regel, welches Präfix
/// <c>loggedInUserId</c> verlangt und welches ihn verbietet.
/// </summary>
/// <remarks>
/// <para><b>Der überwiegende Teil dieser Routen ist undokumentiert.</b> Die OpenAPI-Beschreibung
/// von TANSS 10.10.0 kennt weder <c>/api/tanss.x/v1</c> noch <c>PUT /api/v1/remoteSupports</c>
/// noch <c>/api/v1/jwts</c>. Belegt sind sie über Messungen gegen eine Produktivinstanz der
/// Fassung 10.10.0: jede hier aufgeführte Route ist dort aufgerufen und ihre Antwort
/// festgehalten worden.</para>
///
/// <para>Daraus folgt eine Betriebsregel: bricht eine dieser Routen nach einem TANSS-Update weg,
/// meldet das Werkzeug das ausdrücklich, statt still zu scheitern. Der dokumentierte Weg
/// <c>/api/remoteSupports/v1</c> bleibt als Rückfallebene bestehen, verlangt aber ein
/// typgebundenes Token je Anbindung.</para>
/// </remarks>
public static class TanssRoutes
{
    /// <summary>Integrationsschnittstelle. Hier niemals <c>loggedInUserId</c> anhängen.</summary>
    public const string TanssXPrefix = "/api/tanss.x/v1";

    /// <summary>Reguläre Schnittstelle. Hier ist <c>loggedInUserId</c> zwingend.</summary>
    public const string V1Prefix = "/api/v1";

    // --- Fernwartungen -------------------------------------------------------------------
    /// <summary>Anlegen. Einziges Verb auf dieser Route ist POST.</summary>
    public const string RemoteSupportsCreate = TanssXPrefix + "/remoteSupports";

    /// <summary>Die in TANSS gepflegten externen Anbindungen.</summary>
    public const string RemoteSupportSystems = TanssXPrefix + "/remoteSupports/systems";

    /// <summary>Filterabfrage über PUT. Undokumentiert, aber die einzige Leseroute für uns.</summary>
    public const string RemoteSupportsList = V1Prefix + "/remoteSupports";

    // --- Tickets, Timer, Personen --------------------------------------------------------
    public const string OwnTickets = V1Prefix + "/tickets/own";

    /// <summary>
    /// Ticketsuche mit Filterrumpf. <b>PUT, obwohl es liest</b> — die Route nimmt den Filter im
    /// Rumpf entgegen, und ein GET hätte dafür keinen Platz.
    /// </summary>
    /// <remarks>
    /// Nachgemessen gegen eine Instanz der Fassung 10.10.0: Mit
    /// <c>{"staff":[&lt;id&gt;],"includeDoneTickets":false}</c> kommen die offenen Tickets des
    /// Mitarbeiters zurück, mit <c>true</c> zusätzlich die erledigten (dort Zustand 1000).
    /// </remarks>
    public const string TicketSearch = V1Prefix + "/tickets";

    /// <summary>
    /// Ein einzelnes Ticket. Antwortet mit 404, wenn es die Kennung nicht gibt — der Weg, eine
    /// eingetippte Nummer zu prüfen, statt sie ungeprüft zu buchen.
    /// </summary>
    public static string TicketById(int ticketId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{V1Prefix}/tickets/{ticketId}");

    // --- Suche ---------------------------------------------------------------------------
    /// <summary>
    /// Die bereichsübergreifende Suche. Der einzige Weg, aus einem Firmennamen eine
    /// <c>companyId</c> zu machen.
    /// </summary>
    /// <remarks>
    /// <para><b>PUT, obwohl es liest</b> — wie die Ticketsuche: die Suchvorgabe geht im Rumpf,
    /// und dafür hat ein GET keinen Platz. Dokumentiert ab Zeile 8363 der Beschreibung zu
    /// 10.10.0.</para>
    /// <para><b>Die Falle sitzt in <c>configs.company.maxResults</c>:</b> Das ist eine Schwelle
    /// und keine Begrenzung. Wird sie überschritten, kommt eine leere Liste statt einer
    /// gekürzten. Nachgemessen: <c>Gmb</c> hat 540 Treffer, mit 539 kommen null, mit 540 kommen
    /// alle. Näheres bei <c>CompanySearchConfig.MaxResults</c>.</para>
    /// </remarks>
    public const string Search = V1Prefix + "/search";

    // --- Leistungen ----------------------------------------------------------------------
    /// <summary>
    /// Leistungen. Anlegen ist der <b>einzige</b> Weg hier hinein.
    /// </summary>
    /// <remarks>
    /// Nachgemessen gegen eine Instanz der Fassung 10.10.0: <c>PUT</c> antwortet mit
    /// <c>405 Method Not Allowed</c> und nennt im Kopf <c>Allow: POST</c>. Die Route steht in
    /// keiner Beschreibung; das <c>Allow</c> ist der Beleg.
    /// </remarks>
    public const string Supports = V1Prefix + "/supports";

    /// <summary>
    /// Bereitet eine Leistung vor, ohne sie anzulegen.
    /// </summary>
    /// <remarks>
    /// <para>Mit <c>{"initializers":[{"type":"TIMER","id":&lt;id&gt;}]}</c> antwortet TANSS mit
    /// einer vollständig vorbelegten Leistung: Stundensatz, Abrechnungsart, Fahrzeug, Zone — und
    /// vor allem <c>multiTextItems</c> mit <b>einem Eintrag je Laufabschnitt des Timers</b>.
    /// Damit ist die Frage „eine Leistung über alles oder eine je Abschnitt“ nicht von uns zu
    /// entscheiden: TANSS sieht eine Leistung mit gegliedertem Text vor.</para>
    /// <para><b>POST, obwohl es nichts anlegt.</b> Die Vorbereitung braucht den Rumpf mit den
    /// Quellen; angelegt wird erst durch <see cref="Supports"/>.</para>
    /// </remarks>
    public const string SupportProperties = V1Prefix + "/supports/properties";

    public const string Technicians = TanssXPrefix + "/technicians";
    public const string Timers = V1Prefix + "/timers";
    public const string TimerNotes = V1Prefix + "/timers/notes";

    // --- Anmeldung und Token -------------------------------------------------------------
    public const string Login = V1Prefix + "/login";

    /// <summary>
    /// Alles, was TANSS ein Token ausstellen lässt.
    /// </summary>
    /// <remarks>
    /// Der Riegel gegen Wiederholungen hängt am <b>Präfix</b> und nicht an einer einzelnen
    /// Route. Er hing vorher an <see cref="MintToken"/> allein — und hätte damit jede weitere
    /// Tokenart, etwa die je Fernwartungsanbindung, ungeschützt in die Wiederholung laufen
    /// lassen. Ein Pfad, der ein unwiderrufliches Token ausstellt, darf nicht erst dann
    /// geschützt werden, wenn jemand daran denkt.
    /// </remarks>
    public const string JwtPrefix = V1Prefix + "/jwts";

    /// <summary>Token prägen. <c>tanss_app</c> ist der gültige Wert — <c>tanss_x</c> gibt es nicht.</summary>
    public const string MintToken = JwtPrefix + "/tanss_app";

    /// <summary>
    /// Braucht der Pfad den Parameter <c>loggedInUserId</c>?
    /// </summary>
    /// <remarks>
    /// Die Prüfung auf das <c>tanss.x</c>-Präfix muss <b>zuerst</b> stehen: beide Präfixe
    /// beginnen nicht gemeinsam, aber die Reihenfolge macht die Absicht lesbar und schützt
    /// vor einer späteren Umstellung auf einen gemeinsamen Stamm.
    /// </remarks>
    public static bool NeedsLoggedInUserId(string path) =>
        !path.StartsWith(TanssXPrefix, StringComparison.Ordinal)
        && path.StartsWith(V1Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Hinterlässt ein <c>GET</c> auf diesen Pfad serverseitig eine Spur, die sich nicht
    /// zurücknehmen lässt?
    /// </summary>
    /// <remarks>
    /// <para><see cref="MintToken"/> ist trotz des Verbs <b>kein Lesevorgang</b>: TANSS stellt
    /// bei jedem Aufruf ein neues JWT aus. <c>isForTesting=true</c> ändert daran nichts —
    /// nachgemessen liefert der Aufruf mit und ohne dieses Kennzeichen ein gleichwertiges Token
    /// mit der angefragten Laufzeit. Harmlos wird der Rechte-Trockentest allein dadurch, dass
    /// <c>TanssAuth.MintAsync</c> dafür 60 Sekunden anfragt.</para>
    /// <para>Daraus folgt: ein zweiter Versuch nach einer Zeitüberschreitung liest nicht
    /// dasselbe noch einmal, sondern prägt ein <b>zweites</b> Token. Behalten wird höchstens
    /// eines; die übrigen bleiben bis zu 365 Tage gültig, und TANSS 10.10.0 kennt keinen
    /// Widerruf. Solche Pfade laufen deshalb mit genau einem Versuch.</para>
    /// <para><b>Geprüft wird gegen <see cref="JwtPrefix"/> und nicht gegen die eine bekannte
    /// Route.</b> Unter <c>/jwts</c> liegt mehr als <c>tanss_app</c> — jede Tokenart, die dort
    /// später dazukommt, ist vom ersten Aufruf an geschützt, statt erst dann, wenn jemand den
    /// Riegel nachträgt. Ein vergessener Nachtrag kostet hier nicht einen Fehlversuch, sondern
    /// ein Jahr lang gültige Token, die niemand zurücknehmen kann.</para>
    /// </remarks>
    public static bool HasSideEffectOnGet(string path) =>
        path.StartsWith(JwtPrefix, StringComparison.Ordinal);
}
