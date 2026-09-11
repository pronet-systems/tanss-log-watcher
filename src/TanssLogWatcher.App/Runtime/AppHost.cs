using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.App.Services;
using TanssLogWatcher.Storage;
using TanssLogWatcher.Storage.Config;

namespace TanssLogWatcher.App.Runtime;

/// <summary>
/// Das Rückgrat der Anwendung: lädt die Konfiguration, baut die Bausteine, führt die drei
/// Hintergrunddienste und hält den Betriebszustand.
/// </summary>
/// <remarks>
/// <para><b>Hier stürzt nichts ab.</b> Jeder Weg endet mit einem Zustand und einem Satz, der
/// sagt, was los ist — auch und gerade der erste Start auf einem frischen Rechner, auf dem
/// weder Konfiguration noch Token liegen. Eine unbehandelte Ausnahme sähe aus wie ein kaputtes
/// Werkzeug und nicht wie eine fehlende Einrichtung, und ein fehlendes Token darf die Anwendung
/// nicht beenden.</para>
///
/// <para><b>Ein Ort für den Zustand.</b> Die Dienste melden Befunde, entscheiden aber nicht:
/// Sonst überschrieben sich drei gegenseitig, und die Anzeige zeigte den Befund dessen, der
/// zufällig zuletzt fertig wurde. <see cref="ReportDegraded"/> und <see cref="ReportWorking"/>
/// sind die einzigen beiden Wege hinein.</para>
///
/// <para><b>Nichts blockiert die Oberfläche.</b> <see cref="StartAsync"/> gibt zurück, sobald
/// die Konfiguration gelesen ist; der erste Netzaufruf läuft im Hintergrund. Ein Werkzeug, das
/// beim Start dreissig Sekunden lang an einer toten Leitung hängt, sieht aus wie ein
/// abgestürztes.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AppHost : IRuntimeContext, IDisposable
{
    private readonly IConfigStore _store;
    private readonly ILoggerFactory _loggers;
    private readonly bool _ownsLoggers;
    private readonly object _gate = new();
    private RuntimeComposition? _composition;
    // Bewusst die konkrete Liste: Sie entsteht bei jedem Laden neu und wird danach nur noch
    // weitergereicht, nie veraendert. Ein AppStatus, der sie haelt, sieht sie deshalb nie
    // wechseln.
    private List<RuntimeWarning> _warnings = [];
    private bool _disposed;

    /// <summary>
    /// Baut den Host, ohne etwas zu laden und ohne etwas anzulegen.
    /// </summary>
    /// <remarks>
    /// Der Konstruktor rührt bewusst weder Platte noch Netz an. Erzeugt wird dieses Stück auf
    /// dem Strang der Oberfläche — was dort geschieht, verzögert das erste Fenster.
    /// </remarks>
    /// <param name="store">Der Konfigurationsort; ohne Angabe der vorgesehene im Benutzerprofil.</param>
    /// <param name="loggers">Die Protokollfabrik; ohne Angabe eine eigene, die mit freigegeben wird.</param>
    /// <param name="notifier">Die Umlenkung auf den Strang der Oberfläche.</param>
    /// <param name="clock">Die Uhr; für Tests einsetzbar.</param>
    public AppHost(IConfigStore? store = null, ILoggerFactory? loggers = null,
                   RuntimeNotifier? notifier = null, TimeProvider? clock = null)
    {
        _store = store ?? ConfigStore.Default();
        _ownsLoggers = loggers is null;
        _loggers = loggers ?? CreateLoggers();

        Notifier = notifier ?? new RuntimeNotifier();
        Clock = clock ?? TimeProvider.System;

        Status = new AppStatus
        {
            State = RuntimeState.NotConfigured,
            Headline = "Wird geladen",
            Reason = "Die Konfiguration ist noch nicht gelesen.",
            Advice = "Einen Augenblick.",
            ConfigPath = _store.Path,
            Since = Clock.GetLocalNow(),
        };

        Sessions = new SessionWatcherService(this);
        Uploads = new UploadService(this);
        TokenRotation = new TokenRotationService(this);
        Services = [Sessions, Uploads, TokenRotation];
    }

    /// <summary>Der Betriebszustand hat sich geändert; wird auf dem Strang der Oberfläche ausgelöst.</summary>
    public event EventHandler<AppStatus>? StatusChanged;

    /// <inheritdoc />
    public AppStatus Status { get; private set; }

    /// <inheritdoc />
    public RuntimeComposition? Composition => _composition;

    /// <inheritdoc />
    public RuntimeNotifier Notifier { get; }

    /// <inheritdoc />
    public TimeProvider Clock { get; }

    /// <summary>Die geladene Konfiguration, oder <c>null</c>, solange nichts eingerichtet ist.</summary>
    public AppConfig? Config => _composition?.Config;

    /// <summary>Der Ort, an dem die Konfiguration erwartet wird.</summary>
    public string ConfigPath => _store.Path;

    /// <summary>Die Sitzungsbeobachtung.</summary>
    public SessionWatcherService Sessions { get; }

    /// <summary>Der Sendedienst.</summary>
    public UploadService Uploads { get; }

    /// <summary>Die Tokenerneuerung.</summary>
    public TokenRotationService TokenRotation { get; }

    /// <summary>Alle drei Hintergrunddienste, für eine Anzeige, die sie gemeinsam auflistet.</summary>
    public IReadOnlyList<IBackgroundService> Services { get; }

    /// <summary>
    /// Lädt die Konfiguration und startet die Dienste.
    /// </summary>
    /// <remarks>
    /// Gibt zurück, sobald gelesen und gebaut ist. Der erste Verbindungstest läuft danach im
    /// Hintergrund weiter — bis dahin steht der Zustand auf „Arbeitend“ mit dem ausdrücklichen
    /// Hinweis, dass noch nichts geprüft wurde. Alles andere hiesse, die Oberfläche an einer
    /// toten Leitung hängen zu lassen.
    /// </remarks>
    /// <param name="ct">Abbruchmarke für den Start.</param>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _ = Load();

        foreach (IBackgroundService service in Services)
        {
            await service.StartAsync(ct).ConfigureAwait(false);
        }

        if (_composition is not null)
        {
            // Nicht erwartet: Der Start soll nicht daran haengen. Faellt der Test negativ aus,
            // setzt er den Zustand auf gestoert - die Beobachtung laeuft trotzdem schon.
            _ = Task.Run(() => CheckConnectionAsync(CancellationToken.None), CancellationToken.None);
        }
    }

    /// <summary>
    /// Beendet geordnet: laufende Sitzungen sichern, nicht verwerfen.
    /// </summary>
    /// <remarks>
    /// Die Reihenfolge ist nicht beliebig. Zuerst die Beobachtung — sie schliesst die laufenden
    /// Fernwartungen ab und reiht sie ein; danach hat der Sendedienst womöglich noch etwas zu
    /// tun. Umgekehrt gingen genau die Sitzungen erst beim nächsten Start hinaus, die gerade
    /// eben noch liefen.
    /// </remarks>
    /// <param name="ct">Abbruchmarke; sie begrenzt das Warten, nicht das Sichern.</param>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await Sessions.StopAsync(ct).ConfigureAwait(false);
        await Uploads.StopAsync(ct).ConfigureAwait(false);
        await TokenRotation.StopAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Liest die Konfiguration erneut und tauscht die Bausteine aus.
    /// </summary>
    /// <remarks>
    /// Der Weg aus dem Zustand „Nicht eingerichtet“ heraus, ohne die Anwendung neu zu starten:
    /// Vorlage schreiben, berichtigen, neu laden. Die Dienste bemerken den Wechsel von selbst —
    /// sie fragen den Zusammenbau bei jedem Takt neu ab und holen bei einem anderen ihre
    /// Vorbereitung nach.
    /// </remarks>
    /// <returns><c>true</c>, wenn danach eine gültige Konfiguration in Kraft ist.</returns>
    public bool Reload()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Load();
    }

    /// <summary>
    /// Schreibt die mitgelieferte Vorlage an den erwarteten Ort — ohne je zu überschreiben.
    /// </summary>
    /// <remarks>
    /// Das Angebot des Zustands „Nicht eingerichtet“. Geladen wird danach nicht von selbst: Die
    /// Vorlage trägt Beispieladressen, und sie ungefragt in Betrieb zu nehmen hiesse, gegen
    /// <c>tanss.kunde.de</c> zu laufen. Der Techniker berichtigt sie und lädt dann neu.
    /// </remarks>
    public ConfigTemplateResult WriteConfigTemplate()
    {
        ConfigTemplateResult result = ConfigTemplate.WriteTo(_store.Path);

        if (result.Written)
        {
            // Der Zustand bleibt "nicht eingerichtet", sagt aber jetzt etwas anderes: Die Datei
            // ist da, zu tun sind drei Angaben.
            SetStatus(Status with
            {
                Reason = result.Message,
                Advice = "Die drei genannten Angaben berichtigen und danach neu laden.",
                Problems = [],
                Since = Clock.GetLocalNow(),
            });
        }

        return result;
    }

    /// <summary>
    /// Fragt TANSS, ob Netz, Adresse, Token und Recht gerade zusammen tragen.
    /// </summary>
    /// <remarks>
    /// <c>GET /api/tanss.x/v1/technicians</c> ist die billigste Route, die wir kennen, und
    /// beantwortet alle vier Fragen auf einmal. Sie liest und ändert nichts — dieser Test
    /// kostet nichts und darf deshalb auch von einer Schaltfläche kommen.
    /// </remarks>
    /// <param name="ct">Abbruchmarke.</param>
    public async Task<AppStatus> CheckConnectionAsync(CancellationToken ct = default)
    {
        if (_composition is not { } composition)
        {
            return Status;
        }

        try
        {
            _ = await composition.Technicians.ListAsync(ct).ConfigureAwait(false);
            ReportWorking();
        }
        catch (OperationCanceledException)
        {
            // Der Aufrufer hat abgebrochen - das ist keine Aussage ueber TANSS. Der bisherige
            // Zustand bleibt stehen, statt eine Stoerung zu behaupten, die niemand gemessen hat.
            throw;
        }
        catch (Exception ex)
        {
            RuntimeFailure.Degrade(this, ex, composition.Config.Tanss.BaseUrl);
        }

        return Status;
    }

    /// <inheritdoc />
    public void ReportDegraded(DegradedCause cause, string reason, string advice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(advice);

        // Ohne Konfiguration gibt es keine Stoerung, sondern eine fehlende Einrichtung. Wer das
        // zusammenzoege, schickte den Techniker das Netz pruefen, wo nur eine Datei fehlt.
        if (_composition is null)
        {
            return;
        }

        SetStatus(new AppStatus
        {
            State = RuntimeState.Degraded,
            Headline = "Gestört",
            Reason = reason,
            Advice = advice,
            Cause = cause,
            ConfigPath = _store.Path,
            Warnings = _warnings,
            Since = Clock.GetLocalNow(),
        });
    }

    /// <inheritdoc />
    public void ReportWorking()
    {
        if (_composition is not { } composition)
        {
            return;
        }

        SetStatus(new AppStatus
        {
            State = RuntimeState.Working,
            Headline = "Arbeitend",
            Reason = string.Create(CultureInfo.CurrentCulture,
                $"TANSS unter {composition.Config.Tanss.BaseUrl} antwortet, das Token trägt, "
                + $"und die Beobachtung läuft im Takt von "
                + $"{composition.Config.Watcher.PollIntervalSeconds} Sekunden."),
            Advice = _warnings.Count == 0
                ? "Es ist nichts zu tun."
                : "Es ist nichts zu tun — bis auf die unten genannte Warnung.",
            ConfigPath = _store.Path,
            Warnings = _warnings,
            Since = Clock.GetLocalNow(),
        });
    }

    /// <summary>Gibt Dienste, Bausteine und die eigene Protokollfabrik frei.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (IBackgroundService service in Services)
        {
            if (service is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _composition?.Dispose();
        _composition = null;

        if (_ownsLoggers)
        {
            _loggers.Dispose();
        }
    }

    /// <summary>
    /// Liest die Konfiguration und baut daraus die Bausteine — ohne je zu werfen.
    /// </summary>
    /// <returns><c>true</c>, wenn danach eine gültige Konfiguration in Kraft ist.</returns>
    private bool Load()
    {
        RuntimeComposition? previous;

        lock (_gate)
        {
            previous = _composition;
            _composition = null;
        }

        // Erst abhaengen, dann freigeben: Ein Dienst, der gerade noch einen Takt laeuft, findet
        // sonst einen Zusammenbau vor, dessen Datenbankverbindung bereits geschlossen ist.
        previous?.Dispose();

        AppConfig config;
        try
        {
            config = _store.Load();
        }
        catch (ConfigValidationException ex)
        {
            SetStatus(NotConfigured(
                "Die Konfiguration liegt vor, verletzt aber mindestens eine Regel. Zu "
                + "berichtigen sind die unten genannten Stellen, nicht die ganze Datei.",
                $"Zu berichtigen ist {_store.Path}. Danach diese Anzeige neu laden.",
                ex.Problems));

            return false;
        }
        catch (StorageException ex)
        {
            // ConfigException und alles darunter: Fuer den Techniker macht es keinen
            // Unterschied, ob die Datei fehlt oder kaputt ist - er bekommt dieselbe Anleitung
            // mit dem jeweiligen Grund darin.
            SetStatus(NotConfigured(ex.Message,
                $"Erwartet wird die Datei unter {_store.Path}. Eine ausgefüllte Vorlage legt "
                + "diese Anwendung auf Wunsch genau dort an; darin sind dann Basisadresse, "
                + "Mitarbeiter-ID und die Fernwartungstypen zu berichtigen.",
                []));

            return false;
        }

        RuntimeComposition composition;
        try
        {
            composition = new RuntimeComposition(config, _loggers);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus(NotConfigured(
                "Die Konfiguration ist gültig, aus ihr liessen sich aber keine Bausteine bauen. "
                + "Das ist ein Programmfehler und keine Frage der Einrichtung. Meldung: "
                + Redaction.Scrub(ex.Message),
                "Die Meldung gehört in eine Fehlermeldung an den Hersteller.", []));

            return false;
        }

        lock (_gate)
        {
            _composition = composition;
        }

        _warnings = CollectWarnings(config);

        SetStatus(new AppStatus
        {
            State = RuntimeState.Working,
            Headline = "Arbeitend",
            Reason = string.Create(CultureInfo.CurrentCulture,
                $"Die Konfiguration ist geladen und geprüft: {config.Tanss.BaseUrl}, "
                + $"Mitarbeiter-ID {config.Tanss.EmployeeId}, {config.Monitoring.Count} "
                + $"Beobachtungsregel(n). Ob TANSS gerade antwortet, ist noch nicht geprüft."),
            // KEINE Zusage ueber einen laufenden Test: StartAsync stoesst ihn an, Reload()
            // nicht. Der Satz "der Verbindungstest laeuft gerade" stand vorher auf beiden Wegen
            // und war auf dem zweiten schlicht falsch - der Benutzer wartete dann auf ein
            // Ergebnis, das nie kam.
            Advice = "Ob TANSS antwortet, sagt die Schaltfläche „Verbindung prüfen“ unter "
                + "„Verbindung“.",
            ConfigPath = _store.Path,
            Warnings = _warnings,
            Since = Clock.GetLocalNow(),
        });

        return true;
    }

    /// <summary>Der Zustand „Nicht eingerichtet“, immer mit Ort, Grund und Angebot.</summary>
    private AppStatus NotConfigured(string reason, string advice, IReadOnlyList<string> problems) =>
        new()
        {
            State = RuntimeState.NotConfigured,
            Headline = "Nicht eingerichtet",
            Reason = reason,
            Advice = advice,
            ConfigPath = _store.Path,
            Problems = problems,
            Warnings = [],
            Since = Clock.GetLocalNow(),
        };

    /// <summary>
    /// Was läuft, aber nicht in Ordnung ist.
    /// </summary>
    /// <remarks>
    /// <b>Abgeschaltete Zertifikatsprüfung ist keine Nebensache.</b> Das Token wandert bei jedem
    /// Aufruf über die Leitung; ohne Prüfung genügt ein vorgeschalteter Mitleser, um es
    /// mitzunehmen — und TANSS 10.10.0 kann ein ausgestelltes Token nicht widerrufen. Bisher
    /// stand das nur als Kommentar im Quelltext; ein abgeschalteter Schutz, von dem niemand
    /// weiss, ist derselbe Fehler wie gar keiner.
    /// </remarks>
    private static List<RuntimeWarning> CollectWarnings(AppConfig config)
    {
        List<RuntimeWarning> warnings = [];

        if (!config.Tanss.VerifyTls)
        {
            warnings.Add(new RuntimeWarning(
                "Zertifikatsprüfung abgeschaltet",
                "In der Konfiguration steht tanss.verify_tls = false. Damit wird das Zertifikat "
                + "der Gegenstelle überhaupt nicht mehr geprüft — auch nicht auf den richtigen "
                + "Namen und auch nicht auf Ablauf. Das Arbeitstoken geht bei jedem Aufruf über "
                + "diese Leitung; ein vorgeschalteter Mitleser nimmt es damit mit, und TANSS "
                + "10.10.0 kann ein ausgestelltes Token nicht widerrufen.",
                "Vorgesehen ist der Schalter allein für Testinstanzen mit selbst ausgestelltem "
                + "Zertifikat. Im Betrieb gehört das Zertifikat der Instanz in den "
                + "Zertifikatspeicher dieses Rechners und tanss.verify_tls zurück auf true."));
        }

        if (config.Proxy.Enabled && !config.Tanss.VerifyTls)
        {
            warnings.Add(new RuntimeWarning(
                "Proxy ohne Zertifikatsprüfung",
                "Es ist ein Proxy eingetragen und zugleich die Zertifikatsprüfung abgeschaltet. "
                + "Das ist genau die Lage, in der ein Mitleser nicht einmal auffallen würde: Der "
                + "Proxy sieht den Datenstrom ohnehin, und ohne Zertifikatsprüfung ist nicht "
                + "mehr feststellbar, ob dahinter noch die richtige Instanz steht.",
                "tanss.verify_tls auf true setzen. Bricht daraufhin die Verbindung, ist das kein "
                + "Grund, den Schalter zurückzustellen — dann sitzt tatsächlich etwas dazwischen, "
                + "dessen Zertifikat dieser Rechner kennen muss."));
        }

        return warnings;
    }

    /// <summary>Übernimmt einen neuen Zustand und meldet ihn — nur, wenn er sich unterscheidet.</summary>
    /// <remarks>
    /// Der Vergleich ist kein Sparen am falschen Ort: Der Sendedienst meldet bei jedem
    /// geglückten Eintrag „arbeitend“. Ohne diesen Riegel liefe bei einem Stapel von zwanzig
    /// Einträgen zwanzigmal die gesamte Zustandsanzeige neu auf.
    /// </remarks>
    private void SetStatus(AppStatus status)
    {
        AppStatus previous = Status;

        if (previous.State == status.State && previous.Cause == status.Cause
            && string.Equals(previous.Reason, status.Reason, StringComparison.Ordinal))
        {
            return;
        }

        Status = status;
        Notifier.Raise(StatusChanged, this, status);
    }

    /// <summary>
    /// Die Protokollfabrik der Anwendung.
    /// </summary>
    /// <remarks>
    /// In den Debug-Ausgabestrom und nicht auf eine Konsole: Ein Fensterprogramm hat keine. Die
    /// Stufe steht fest auf <see cref="LogLevel.Warning"/>, bis die Konfiguration gelesen ist —
    /// sie zu lesen setzte ja voraus, was hier gerade entsteht. Der sichtbare Betriebsbericht
    /// ist ohnehin nicht dieses Protokoll, sondern das Änderungsprotokoll in <c>state.db</c>.
    /// </remarks>
    private static ILoggerFactory CreateLoggers() =>
        LoggerFactory.Create(builder =>
        {
            _ = builder.AddDebug();
            _ = builder.SetMinimumLevel(LogLevel.Warning);
        });
}
