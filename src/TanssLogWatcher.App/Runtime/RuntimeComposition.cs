using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Repository;
using TanssLogWatcher.Monitoring;
using TanssLogWatcher.Monitoring.Model;
using TanssLogWatcher.Monitoring.Native;
using TanssLogWatcher.Storage;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.History;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;
using TanssLogWatcher.Storage.Recordings;
using TanssLogWatcher.Storage.Secrets;

namespace TanssLogWatcher.App.Runtime;

/// <summary>
/// Der Zusammenbau: aus einer geprüften <see cref="AppConfig"/> entstehen hier alle Bausteine.
/// </summary>
/// <remarks>
/// <para><b>Nahe verwandt mit <c>TanssLogWatcher.Cli/Composition.cs</c>, und das mit
/// Absicht.</b> Die Oberfläche verweist nicht auf die Kommandozeile — ein Werkzeug mit
/// Fenstern, das eine Konsolenanwendung als Bibliothek einbindet, zöge deren
/// Befehlszeilenauswertung und deren Konsolenausgabe mit herein. Der Preis ist diese eine
/// wiederholte Datei; der Gegenwert ist, dass beide Programme dieselbe Reihenfolge, dieselbe
/// Verzögerung und dieselben Einstellungen benutzen. <b>Wer an Netz, Beobachtung,
/// Warteschlange oder Protokoll etwas ändert, ändert es dort mit.</b></para>
///
/// <para><b>Zwei Bausteine führt nur dieser Zusammenbau</b>, und das ist kein Versehen:
/// <see cref="Recordings"/> und <see cref="History"/>. Beide hängen an Diensten, die es in
/// der Kommandozeile nicht gibt — die Bildschirmaufzeichnung und der Aufräumtakt aus
/// <c>UploadService.Housekeep</c> laufen ausschliesslich im Fensterprogramm. Sie dort
/// nachzubilden hiesse, zwei Eigenschaften anzulegen, die niemand liest. Wer der
/// Kommandozeile einen dieser Dienste gibt, zieht den zugehörigen Baustein mit — und mit ihm
/// die Fristen, die sonst niemand laufen lässt.</para>
///
/// <para>Es gibt genau diese eine Stelle, an der Netz, Beobachtung und Ablage verdrahtet
/// werden. Jeder zweite Ort, an dem ein Baustein gebaut wird, ist ein Ort, an dem eine
/// Einstellung fehlen kann — und die gefährlichste davon ist
/// <c>logging.redact_window_titles</c>: Sie wirkt nur, wenn das Protokoll über
/// <see cref="SessionLog.FromConfig(AppConfig, StateDatabase, TimeProvider)"/> entsteht. Wird
/// sie vergessen, fällt das nicht auf; es stehen dann bloß Kundennamen im Klartext in
/// <c>state.db</c>.</para>
///
/// <para><b>Ablage und Netz entstehen getrennt und auf Abruf.</b> Eine Anwendung, die beim
/// Start ohne Not <c>state.db</c> anlegt, hinterlässt auf einem Rechner, auf dem sie nur
/// einmal geöffnet wurde, eine Datenbank und — bei eingeschalteter Schwärzung — eine
/// Schlüsseldatei. Beides soll erst beim ersten Bedarf entstehen.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RuntimeComposition : IDisposable
{
    private readonly ILoggerFactory _loggers;
    private readonly Lazy<StateDatabase> _database;
    private readonly Lazy<UploadQueue> _queue;
    private readonly Lazy<SessionLog> _log;
    private readonly Lazy<RecordingStore> _recordings;
    private readonly Lazy<SessionHistoryStore> _history;
    private readonly Lazy<HostNameResolver> _hostNames;
    private readonly Lazy<SessionEngine> _engine;
    private bool _disposed;

    /// <summary>Baut alles aus der geprüften Konfiguration.</summary>
    /// <param name="config">Die geladene und geprüfte Konfiguration.</param>
    /// <param name="loggers">Die Protokollfabrik; sie gehört dem Aufrufer.</param>
    /// <param name="statePath">
    /// Abweichender Pfad der Zustandsdatenbank. Nur für Tests gedacht; ohne Angabe gilt der
    /// vorgesehene Ort im lokalen Profil.
    /// </param>
    public RuntimeComposition(AppConfig config, ILoggerFactory loggers, string? statePath = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(loggers);

        Config = config;
        _loggers = loggers;

        Tokens = DpapiTokenStore.Default();
        // Das Proxy-Kennwort wird HIER aufgeloest und nicht in ToTanssOptions: Jenes
        // Stueck ist eine reine Abbildung und kennt keinen Schluesselspeicher. Ohne
        // diese Zeile ginge bei einem Proxy mit Anmeldung eine leere Zeichenkette als
        // Kennwort hinaus, und die Abweisung saehe aus wie ein Problem mit TANSS.
        string? proxyPassword = config.Proxy.Enabled && config.Proxy.User is not null
            ? DpapiSecretStore.ForProxy().Read()
            : null;

        Client = new TanssClient(config.ToTanssOptions(proxyPassword), Tokens);
        RemoteSupports = new RemoteSupportRepository(Client, config.Tanss.EmployeeId);
        Technicians = new TechnicianRepository(Client);
        Tickets = new TicketRepository(Client);
        Timers = new TimerRepository(Client);
        Supports = new SupportRepository(Client);
        Companies = new CompanyRepository(Client);
        NewTickets = new TicketCreator(Client);

        Windows = new WindowSource(loggers.CreateLogger<WindowSource>());
        Processes = new ProcessSource(loggers.CreateLogger<ProcessSource>());
        TcpConnections = new TcpConnectionSource(loggers.CreateLogger<TcpConnectionSource>());
        Settings = ToSettings(config);

        string databasePath = statePath ?? StoragePaths.StateDatabaseFile;
        _database = new Lazy<StateDatabase>(() => new StateDatabase(databasePath));
        _queue = new Lazy<UploadQueue>(() => new UploadQueue(Database));

        // Ausschliesslich ueber FromConfig: hier wirkt logging.redact_window_titles.
        _log = new Lazy<SessionLog>(() => SessionLog.FromConfig(config, Database));

        // Auf derselben Datenbank wie die Warteschlange: Eine zweite Datei waere eine zweite
        // Sicherungsstrategie, und die Buchfuehrung ueber Aufzeichnungen ist der Nachweis, an
        // dem eine Auskunft nach Art. 15 DSGVO haengt.
        _recordings = new Lazy<RecordingStore>(() => new RecordingStore(Database));

        // Ausschliesslich ueber FromConfig, und aus demselben Grund wie beim Protokoll: Nur
        // dort loest history.redact_destination ueber logging.redact_window_titles auf. Ein
        // "?? false" an einer Aufrufstelle kehrte die ausdrueckliche Entscheidung des
        // Technikers still um - die Gegenstelle ist derselbe Text, den die Schwaerzung
        // verbirgt, bei zehn von sechsunddreissig Profilen der ganze Fenstertitel.
        // Der Nachtrag haengt HIER und nicht am Programmstart: Ein Aufruf im Start zwaenge
        // die Datenbank in jedem Fall ins Dasein und naehme der traegen Erzeugung genau die
        // Wirkung, derentwegen es sie gibt. So laeuft er beim ERSTEN Zugriff auf den Verlauf
        // - der die Datei ohnehin oeffnet - und durch Lazy genau einmal je Programmlauf.
        _history = new Lazy<SessionHistoryStore>(() =>
        {
            SessionHistoryStore history = SessionHistoryStore.FromConfig(config, Database);
            Backfill(config, Database, history);
            return history;
        });

        _hostNames = new Lazy<HostNameResolver>(
            () => new HostNameResolver(logger: loggers.CreateLogger<HostNameResolver>()));

        _engine = new Lazy<SessionEngine>(() => new SessionEngine(
            Windows, Processes,
            new DestinationResolver(TcpConnections, HostNames,
                                    loggers.CreateLogger<DestinationResolver>()),
            clock: null,
            logger: loggers.CreateLogger<SessionEngine>(),
            options: new SessionEngineOptions
            {
                SampleInterval = TimeSpan.FromSeconds(config.Watcher.PollIntervalSeconds),
                IpGracePeriod = TimeSpan.FromSeconds(config.Watcher.IpSessionGraceSeconds),
            }));
    }

    /// <summary>Die Konfiguration, aus der alles gebaut wurde.</summary>
    public AppConfig Config { get; }

    /// <summary>Der Tokenspeicher im lokalen Profil.</summary>
    public ITokenStore Tokens { get; }

    /// <summary>Der HTTP-Zugang zu TANSS.</summary>
    public TanssClient Client { get; }

    /// <summary>Fernwartungen lesen und schreiben.</summary>
    public IRemoteSupportRepository RemoteSupports { get; }

    /// <summary>Die Techniker der Instanz.</summary>
    public ITechnicianRepository Technicians { get; }

    /// <summary>Die eigenen Tickets.</summary>
    public ITicketRepository Tickets { get; }

    /// <summary>Die eigenen Timer.</summary>
    public ITimerRepository Timers { get; }

    /// <summary>Leistungen — aus einem Timer vorbereiten und anlegen.</summary>
    public ISupportRepository Supports { get; }

    /// <summary>Die Firmensuche über <c>PUT /api/v1/search</c>.</summary>
    /// <remarks>
    /// <para>Sie wird gebraucht, sobald ein Ticket angelegt werden soll: Ein Ticket ohne Firma
    /// landet in TANSS in einer eigenen Liste, die niemand im Alltag ansieht.</para>
    /// <para><b>Nur hier und nicht in der Kommandozeile.</b> Gesucht wird ausschliesslich aus
    /// einem Dialog heraus; eine Eigenschaft in <c>Cli/Composition.cs</c>, die niemand liest,
    /// wäre eine zweite Stelle, an der eine Einstellung fehlen kann.</para>
    /// </remarks>
    public ICompanyRepository Companies { get; }

    /// <summary>Tickets anlegen — <c>POST /api/v1/tickets</c>.</summary>
    /// <remarks>
    /// Getrennt von <see cref="Tickets"/>, weil der lesende Vertrag schmal bleiben soll. Auch
    /// dieser Baustein läuft nur im Fensterprogramm: Ein Ticket entsteht auf Anweisung des
    /// Technikers und niemals aus einem Takt heraus.
    /// </remarks>
    public ITicketCreation NewTickets { get; }

    /// <summary>Quelle der sichtbaren Fenster.</summary>
    public IWindowSource Windows { get; }

    /// <summary>Quelle der Prozessmomentaufnahme.</summary>
    public IProcessSource Processes { get; }

    /// <summary>Quelle der offenen TCP-Verbindungen.</summary>
    public ITcpConnectionSource TcpConnections { get; }

    /// <summary>Die Beobachtungseinstellungen, abgeglichen mit dem Profilkatalog.</summary>
    public IReadOnlyList<MonitoringSetting> Settings { get; }

    /// <summary>Die Zustandsdatenbank. Entsteht beim ersten Zugriff.</summary>
    public StateDatabase Database => _database.Value;

    /// <summary>Die Warteschlange, auf derselben Datenbank wie das Änderungsprotokoll.</summary>
    public IUploadQueue Queue => _queue.Value;

    /// <summary>Das Änderungsprotokoll — mit <c>logging.redact_window_titles</c>.</summary>
    public SessionLog Log => _log.Value;

    /// <summary>
    /// Die Buchführung über Bildschirmaufzeichnungen — auf derselben Datenbank.
    /// </summary>
    /// <remarks>
    /// Sie entsteht auch dann, wenn nicht aufgezeichnet wird: Was ein früherer Lauf mit
    /// eingeschalteter Aufzeichnung hinterlassen hat, muss gelöscht werden, wenn seine Frist
    /// abläuft — und zwar unabhängig davon, ob heute noch aufgezeichnet wird.
    /// </remarks>
    public RecordingStore Recordings => _recordings.Value;

    /// <summary>
    /// Der Verlauf abgeschlossener Sitzungen — auf derselben Datenbank.
    /// </summary>
    /// <remarks>
    /// <para>Er entsteht auch dann, wenn <c>history.enabled</c> aus ist: Was ein früherer Lauf
    /// mit eingeschaltetem Verlauf hinterlassen hat, muss gelöscht und geschwärzt werden, wenn
    /// seine Frist abläuft. Ein Speicher, der sich beim Abschalten in eine Attrappe
    /// verwandelte, hielte diese Zeilen für immer. Ob <b>geschrieben</b> wird, entscheidet
    /// deshalb der jeweilige Dienst an <c>history.enabled</c> und nicht dieser Zusammenbau.</para>
    /// </remarks>
    public SessionHistoryStore History => _history.Value;

    /// <summary>Die Rückwärtsauflösung mit Zwischenspeicher.</summary>
    public IHostNameResolver HostNames => _hostNames.Value;

    /// <summary>Die Zustandsmaschine der Sitzungserkennung.</summary>
    public SessionEngine Engine => _engine.Value;

    /// <summary>
    /// Baut einen zweiten Zugang mit einem <b>anderen</b> Token.
    /// </summary>
    /// <remarks>
    /// Der einzige Grund dafür ist die Probe beim Tokenwechsel: Das frisch geprägte Token muss
    /// sich bewähren, <b>bevor</b> es das bisherige ablöst. Dafür braucht es einen Zugang, der
    /// es trägt, ohne dass es im Speicher steht — gelingt die Probe nicht, soll ja alles
    /// bleiben, wie es war.
    /// </remarks>
    /// <param name="token">Das zu prüfende Token, mit oder ohne <c>Bearer </c>.</param>
    public TanssClient CreateClientWith(string token) =>
        new(Config.ToTanssOptions(), new FixedTokenStore(token));

    /// <summary>Die Protokollfabrik, mit der dieser Zusammenbau entstanden ist.</summary>
    /// <remarks>
    /// Die Hintergrunddienste schreiben in dieselbe Fabrik. Eine zweite hätte eine zweite
    /// Protokollstufe, und <c>logging.level</c> gälte dann nur für die Hälfte des Programms.
    /// </remarks>
    public ILoggerFactory Loggers => _loggers;

    /// <summary>Gibt alles frei, was tatsächlich entstanden ist.</summary>
    /// <remarks>
    /// Die Reihenfolge ist nicht beliebig: Warteschlange und Protokoll benutzen die Datenbank,
    /// ohne sie zu besitzen. Würde sie zuerst geschlossen, liefe ein noch laufender
    /// Schreibvorgang in eine geschlossene Verbindung.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_log.IsValueCreated)
        {
            _log.Value.Dispose();
        }

        if (_queue.IsValueCreated)
        {
            _queue.Value.Dispose();
        }

        if (_recordings.IsValueCreated)
        {
            _recordings.Value.Dispose();
        }

        if (_history.IsValueCreated)
        {
            _history.Value.Dispose();
        }

        if (_database.IsValueCreated)
        {
            _database.Value.Dispose();
        }

        if (_hostNames.IsValueCreated)
        {
            _hostNames.Value.Dispose();
        }

        Client.Dispose();
    }

    /// <summary>
    /// Trägt den Verlauf einmalig aus dem nach, was schon in der Datenbank steht.
    /// </summary>
    /// <remarks>
    /// <para><b>Genau einmal, und nur beim ersten Start nach der Überführung.</b>
    /// <see cref="SessionHistoryBackfill.IsDue"/> liest
    /// <see cref="StateDatabase.PreviousSchemaVersion"/> — den Stand, den die Datei beim
    /// Öffnen trug. Eine frisch angelegte Datei (<c>0</c>) hat nichts nachzutragen, eine
    /// Datei auf dem laufenden Stand ebenfalls nicht. Dazwischen lag
    /// <c>session_history</c> noch nicht vor, während Warteschlange und Protokoll bereits
    /// schrieben; genau diese Sitzungen holt der Nachtrag. Dass er innerhalb der trägen
    /// Erzeugung des Verlaufs steht, gibt die Einmaligkeit schon durch
    /// <see cref="Lazy{T}"/> her — liefe er dennoch ein zweites Mal, kostete er drei
    /// Abfragen und änderte nichts, denn er fügt nur ein, was nicht schon dasteht.</para>
    ///
    /// <para><b>Ein misslungener Nachtrag kostet den Nachtrag, nicht den Start.</b> Deshalb
    /// der eigene <c>catch</c>: Der Speicher wird in jedem Fall zurückgegeben. Innerhalb des
    /// Laufes fängt der Nachtrag nur unlesbare Nutzlasten ab — ein Fehler der Datenbank
    /// selbst käme hier heraus und dürfte weder die Seite noch die Dienste anhalten.</para>
    ///
    /// <para><c>history.enabled</c> wird hier sehr wohl geprüft: Der Nachtrag <b>schreibt</b>,
    /// und was ein abgeschalteter Verlauf nicht führen soll, soll er auch nicht nachträglich
    /// bekommen. Das unterscheidet ihn vom Aufräumtakt, der die Fristen bestehender Zeilen
    /// unabhängig davon laufen lassen muss.</para>
    /// </remarks>
    /// <param name="config">Die geprüfte Konfiguration; sie trägt Schalter und Frist.</param>
    /// <param name="database">Die geöffnete Zustandsdatenbank.</param>
    /// <param name="history">Der eben gebaute Verlauf, in den geschrieben wird.</param>
    private void Backfill(AppConfig config, StateDatabase database, SessionHistoryStore history)
    {
        try
        {
            if (!config.History.Enabled || !SessionHistoryBackfill.IsDue(database))
            {
                return;
            }

            BackfillResult done = SessionHistoryBackfill.FromConfig(config, database, history);

            if (done.Notice is not { } notice)
            {
                return;
            }

            // Der Satz nennt die Fehlstellen selbst. Unlesbare Zeilen machen den Eintrag zum
            // Fehler und nicht zum Erfolg: Wer im Protokoll nach Fehlern sucht, soll die
            // Sitzungen finden, die der Verlauf nicht bekommen hat.
            _ = Log.Append(new SessionLogEntry
            {
                Operation = "history.backfill",
                Outcome = done.Unreadable > 0 ? SessionOutcome.Error : SessionOutcome.Ok,
                Reason = notice,
                Trigger = SessionTrigger.Startup,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5. Der Verlauf bleibt dann leerer als moeglich - die Seite sagt das
            // wahrheitsgemaess, und keine Sitzung von heute geht dadurch verloren.
            _loggers.CreateLogger<RuntimeComposition>().LogWarning(
                ex, "Der einmalige Nachtrag des Verlaufs ist misslungen.");
        }
    }

    /// <summary>
    /// Übersetzt die Konfigurationseinträge in Beobachtungseinstellungen und gleicht sie mit
    /// dem Profilkatalog ab.
    /// </summary>
    /// <remarks>
    /// <para>Der Abgleich über <see cref="MonitoringProfiles.Reconcile"/> ist kein Beiwerk: Er
    /// ergänzt fehlende Profile abgeschaltet und wirft unbekannte Schlüssel hinaus. Eine
    /// Konfiguration aus einer neueren Programmversion nimmt dieses Werkzeug damit an, statt
    /// sie abzulehnen — und beobachtet dabei nichts, was es nicht kennt.</para>
    /// <para>Die Ausschlussliste wird zu einer mit Semikolon getrennten Zeichenkette
    /// zusammengezogen, weil <see cref="MonitoringSetting.ExcludeIpAddresses"/> genau diese
    /// Form erwartet — die Schreibweise der Vorlage, an der <see cref="IpFilter"/> hängt.</para>
    /// </remarks>
    private static IReadOnlyList<MonitoringSetting> ToSettings(AppConfig config) =>
        MonitoringProfiles.Reconcile(config.Monitoring.Select(entry => new MonitoringSetting
        {
            Key = entry.Key,
            RemoteSupportTypeId = entry.RemoteSupportTypeId,
            ExcludeIpAddresses = entry.ExcludeIpAddresses.Count == 0
                ? null
                : string.Join(IpFilter.ExcludeSeparator, entry.ExcludeIpAddresses),
            RegexPatternOverride = entry.RegexPatternOverride,
        }));
}

/// <summary>
/// Ein Tokenspeicher, der genau ein mitgegebenes Token führt und nichts schreibt.
/// </summary>
/// <remarks>
/// Ausschliesslich für die Probe beim Tokenwechsel gedacht. <see cref="Write"/> wirft
/// absichtlich: Ein Speicher, der stillschweigend nichts täte, würde beim nächsten Umbau als
/// echter Speicher verwendet — und der Tokenwechsel ginge dann folgenlos ins Leere.
/// </remarks>
internal sealed class FixedTokenStore : ITokenStore
{
    private readonly string _token;

    /// <summary>
    /// Nimmt das Token auf und bringt es in die Form, die der Header verlangt.
    /// </summary>
    /// <remarks>
    /// <b>Die Normalisierung ist hier nicht Bequemlichkeit, sondern Pflicht.</b>
    /// <see cref="ITokenStore.Read"/> sagt zu, das Token <i>einschliesslich</i> des Präfixes
    /// <c>Bearer </c> zu liefern, und <see cref="TanssClient"/> setzt den Rückgabewert
    /// unverändert in den Kopfzeileneintrag <c>apiToken</c>. Fehlt das Präfix, überspringt
    /// TANSS die Prüfung vollständig — der Aufruf läuft dann unangemeldet weiter und wird mit
    /// 403 abgewiesen, ununterscheidbar von einem abgelaufenen Token. Dieser Speicher gab das
    /// Token bisher wörtlich zurück; wer ihm ein abgeschnittenes übergab, bekam auf jeden
    /// Aufruf eine 403, deren Ursache nirgends stand.
    /// </remarks>
    public FixedTokenStore(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _token = DpapiTokenStore.Normalize(token);
    }

    /// <inheritdoc />
    public string Read() => _token;

    /// <inheritdoc />
    public void Write(string token) => throw new InvalidOperationException(
        "Dieser Tokenspeicher ist die Probe beim Tokenwechsel und kann nichts ablegen. "
        + "Geschrieben wird ausschliesslich über DpapiTokenStore, und zwar erst, nachdem "
        + "das neue Token sich bewährt hat.");
}
