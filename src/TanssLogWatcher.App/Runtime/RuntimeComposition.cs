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
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;
using TanssLogWatcher.Storage.Secrets;

namespace TanssLogWatcher.App.Runtime;

/// <summary>
/// Der Zusammenbau: aus einer geprüften <see cref="AppConfig"/> entstehen hier alle Bausteine.
/// </summary>
/// <remarks>
/// <para><b>Wortgleich zu <c>TanssLogWatcher.Cli/Composition.cs</c>, und das mit Absicht.</b>
/// Die Oberfläche verweist nicht auf die Kommandozeile — ein Werkzeug mit Fenstern, das eine
/// Konsolenanwendung als Bibliothek einbindet, zöge deren Befehlszeilenauswertung und deren
/// Konsolenausgabe mit herein. Der Preis ist diese eine wiederholte Datei; der Gegenwert ist,
/// dass beide Programme dieselbe Reihenfolge, dieselbe Verzögerung und dieselben
/// Einstellungen benutzen. Wer hier etwas ändert, ändert es dort mit.</para>
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
        Client = new TanssClient(config.ToTanssOptions(), Tokens);
        RemoteSupports = new RemoteSupportRepository(Client, config.Tanss.EmployeeId);
        Technicians = new TechnicianRepository(Client);
        Tickets = new TicketRepository(Client);
        Timers = new TimerRepository(Client);

        Windows = new WindowSource(loggers.CreateLogger<WindowSource>());
        Processes = new ProcessSource(loggers.CreateLogger<ProcessSource>());
        TcpConnections = new TcpConnectionSource(loggers.CreateLogger<TcpConnectionSource>());
        Settings = ToSettings(config);

        string databasePath = statePath ?? StoragePaths.StateDatabaseFile;
        _database = new Lazy<StateDatabase>(() => new StateDatabase(databasePath));
        _queue = new Lazy<UploadQueue>(() => new UploadQueue(Database));

        // Ausschliesslich ueber FromConfig: hier wirkt logging.redact_window_titles.
        _log = new Lazy<SessionLog>(() => SessionLog.FromConfig(config, Database));

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
    /// Übersetzt die Konfigurationseinträge in Beobachtungseinstellungen und gleicht sie mit
    /// dem Profilkatalog ab.
    /// </summary>
    /// <remarks>
    /// <para>Der Abgleich über <see cref="MonitoringProfiles.Reconcile"/> ist kein Beiwerk: Er
    /// ergänzt fehlende Profile abgeschaltet und wirft unbekannte Schlüssel hinaus. Eine
    /// Konfiguration aus einer neueren Programmfassung nimmt dieses Werkzeug damit an, statt
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
