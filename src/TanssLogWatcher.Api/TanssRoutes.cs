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
    public const string Technicians = TanssXPrefix + "/technicians";
    public const string Timers = V1Prefix + "/timers";
    public const string TimerNotes = V1Prefix + "/timers/notes";

    // --- Anmeldung und Token -------------------------------------------------------------
    public const string Login = V1Prefix + "/login";

    /// <summary>Token prägen. <c>tanss_app</c> ist der gültige Wert — <c>tanss_x</c> gibt es nicht.</summary>
    public const string MintToken = V1Prefix + "/jwts/tanss_app";

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
    /// bei jedem Aufruf ein neues JWT aus und schreibt es in sein Tokenprotokoll. Genau deshalb
    /// gibt es <c>isForTesting=true</c> als ausdrücklich folgenlose, nicht protokollierte
    /// Spielart — ohne dieses Kennzeichen ist der Aufruf schreibend.</para>
    /// <para>Daraus folgt: ein zweiter Versuch nach einer Zeitüberschreitung liest nicht
    /// dasselbe noch einmal, sondern prägt ein <b>zweites</b> Token. Behalten wird höchstens
    /// eines; die übrigen bleiben bis zu 365 Tage gültig, und TANSS 10.10.0 kennt keinen
    /// Widerruf. Solche Pfade laufen deshalb mit genau einem Versuch.</para>
    /// </remarks>
    public static bool HasSideEffectOnGet(string path) =>
        path.StartsWith(MintToken, StringComparison.Ordinal);
}
