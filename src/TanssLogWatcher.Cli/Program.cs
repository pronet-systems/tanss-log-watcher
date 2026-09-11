using System.Text;
using Microsoft.Extensions.Logging;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Cli.CommandLine;
using TanssLogWatcher.Cli.Commands;
using TanssLogWatcher.Cli.Output;
using TanssLogWatcher.Monitoring.Native;
using TanssLogWatcher.Storage;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Secrets;

namespace TanssLogWatcher.Cli;

/// <summary>
/// Der Einstiegspunkt: Befehlszeile auswerten, Bausteine bauen, Befehl ausführen.
/// </summary>
/// <remarks>
/// <para><b>Hier stürzt nichts ab.</b> Jeder Weg endet mit einem Rückgabewert und einem Satz,
/// der sagt, was los ist — auch und gerade der erste Aufruf auf einem frischen Rechner, auf dem
/// noch keine Konfiguration liegt. Ein Stapelaufruf, der mit einer Ausnahme abbricht, sieht aus
/// wie ein kaputtes Werkzeug und nicht wie eine fehlende Einrichtung.</para>
/// <para><b>Strg-C ist der vorgesehene Weg hinaus</b>, nicht der Notausgang: Es setzt die
/// Abbruchmarke, und <c>watch</c> schliesst daraufhin seine laufenden Sitzungen ab, statt sie
/// zu verlieren.</para>
/// </remarks>
internal static class Program
{
    /// <summary>Startet das Werkzeug.</summary>
    /// <param name="args">Die Befehlszeile ohne den Programmnamen.</param>
    private static async Task<int> Main(string[] args)
    {
        UseUtf8Console();

        using CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // Cancel = true: Das Werkzeug beendet sich selbst und geordnet. Ohne diese Zeile
            // risse Windows den Prozess ab, und jede laufende Sitzung waere fort.
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        return await RunAsync(args, ConfigStore.Default(), Console.Out, Console.Error,
                              shutdown.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Der eigentliche Ablauf — mit hereingereichtem Konfigurationsort und hereingereichten
    /// Ausgaben.
    /// </summary>
    /// <remarks>
    /// Getrennt von <c>Main</c>, damit sich prüfen lässt, was ohne Konfiguration geschieht:
    /// Diese Zusage — kein Befehl stürzt ab, jeder nennt die Einrichtung — ist nur etwas wert,
    /// wenn sie geprüft wird, und prüfen liesse sie sich sonst nur, indem ein Test das
    /// Benutzerprofil des Ausführenden leerräumt.
    /// </remarks>
    /// <param name="args">Die Befehlszeile ohne den Programmnamen.</param>
    /// <param name="store">Der Konfigurationsort.</param>
    /// <param name="output">Die gewöhnliche Ausgabe.</param>
    /// <param name="error">Die Fehlerausgabe; dorthin gehen Hinweise und Aufruffehler.</param>
    /// <param name="ct">Abbruchmarke.</param>
    internal static async Task<int> RunAsync(string[] args, ConfigStore store, TextWriter output,
                                             TextWriter error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        CliArgs parsed = CliArgs.Parse(args);

        if (parsed.HelpRequested)
        {
            HelpText.Write(output);
            return ExitCode.Healthy;
        }

        if (parsed.VersionRequested)
        {
            output.WriteLine("tanss-logwatch " + HelpText.Version);
            return ExitCode.Healthy;
        }

        if (parsed.Error is { Length: > 0 } problem)
        {
            foreach (string line in Report.Wrap(problem, 92))
            {
                error.WriteLine(line);
            }

            error.WriteLine();
            HelpText.Write(error);
            return ExitCode.Usage;
        }

        try
        {
            return await DispatchAsync(parsed, store, output, error, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("Abgebrochen.");
            return ExitCode.Healthy;
        }
        catch (Exception ex)
        {
            // Der letzte Riegel. Was hier ankommt, ist ein Programmfehler - er soll trotzdem
            // als Satz erscheinen und nicht als Stapelabzug, und mit einem Rueckgabewert, den
            // eine Ueberwachung lesen kann.
            error.WriteLine("Unerwarteter Fehler. Das ist ein Programmfehler und keine Frage der "
                + "Einrichtung; die folgende Meldung gehört in die Fehlermeldung an den "
                + "Hersteller.");
            error.WriteLine(Redaction.Scrub(ex.Message));
            return ExitCode.Broken;
        }
    }

    /// <summary>Führt den erkannten Befehl aus.</summary>
    private static async Task<int> DispatchAsync(CliArgs parsed, ConfigStore store,
                                                 TextWriter output, TextWriter error,
                                                 CancellationToken ct)
    {
        AppConfig? config = TryLoad(store, out string? configProblem);

        // "windows" ist ausdruecklich auch ohne Einrichtung zu gebrauchen: Es ist das Werkzeug,
        // mit dem ein Muster ueberhaupt erst gebaut wird.
        if (parsed.Command == CliCommand.Windows)
        {
            return RunWindows(parsed, config, output, error);
        }

        if (config is null)
        {
            HelpText.WriteMissingConfiguration(error, store.Path,
                configProblem ?? "Die Konfiguration liess sich nicht laden.");
            return ExitCode.Broken;
        }

        using ILoggerFactory loggers = CreateLoggers(config);
        using Composition composition = new(config, loggers);

        return parsed.Command switch
        {
            CliCommand.Doctor => await DoctorCommand.RunAsync(new DoctorInputs
            {
                Config = config,
                Client = composition.Client,
                RemoteSupports = composition.RemoteSupports,
                Technicians = composition.Technicians,
                Tokens = composition.Tokens,
                Queue = () => composition.Queue,
                ConfigPath = store.Path,
            }, output, ct).ConfigureAwait(false),

            CliCommand.Watch => await WatchCommand.RunAsync(new WatchInputs
            {
                Config = config,
                Engine = composition.Engine,
                Queue = composition.Queue,
                Settings = composition.Settings,
                Log = composition.Log,
                Keys = FingerprintKeyStore.BesideDatabase(composition.Database.Path),
            }, parsed.DryRun, output, ct).ConfigureAwait(false),

            CliCommand.Queue => await QueueCommand.RunAsync(new QueueInputs
            {
                Queue = composition.Queue,
                RemoteSupports = composition.RemoteSupports,
                Log = composition.Log,
            }, parsed.Flush, output, ct).ConfigureAwait(false),

            CliCommand.Token => await TokenCommand.RunAsync(new TokenInputs
            {
                Config = config,
                Client = composition.Client,
                Tokens = composition.Tokens,
                ClientFactory = composition.CreateClientWith,
            }, parsed.SubCommand ?? "status", output, ct).ConfigureAwait(false),

            _ => ExitCode.Usage,
        };
    }

    /// <summary>
    /// Listet die Fenster — mit den Einstellungen der Konfiguration, falls es eine gibt.
    /// </summary>
    /// <remarks>
    /// Ohne Konfiguration läuft der Befehl trotzdem, nur ohne Zuordnungen. Der Hinweis darauf
    /// geht auf die Fehlerausgabe, damit eine weitergeleitete Fensterliste davon frei bleibt.
    /// </remarks>
    private static int RunWindows(CliArgs parsed, AppConfig? config, TextWriter output,
                                  TextWriter error)
    {
        using ILoggerFactory loggers = CreateLoggers(config);

        if (config is null)
        {
            error.WriteLine("Hinweis: Es liegt keine Konfiguration vor. Die Fensterliste ist "
                + "trotzdem vollständig; angezeigt wird nur keine Zuordnung auf einen "
                + "TANSS-Fernwartungstyp. Anzulegen ist sie mit „tanss-logwatch setup“ "
                + "beziehungsweise vorerst aus config.example.json.");
            error.WriteLine();

            return WindowsCommand.Run(new WindowsInputs
            {
                Windows = new WindowSource(loggers.CreateLogger<WindowSource>()),
                Processes = new ProcessSource(loggers.CreateLogger<ProcessSource>()),
                TcpConnections = new TcpConnectionSource(loggers.CreateLogger<TcpConnectionSource>()),
            }, parsed.ProfileKey, output);
        }

        using Composition composition = new(config, loggers);
        return WindowsCommand.Run(new WindowsInputs
        {
            Windows = composition.Windows,
            Processes = composition.Processes,
            TcpConnections = composition.TcpConnections,
            Settings = composition.Settings,

            // Die Namensauflösung gehört dazu, und ihr Fehlen war kein Detail: Ohne sie fiel
            // dieser Befehl auf NullHostNames zurück und zeigte Ziele, die der laufende Dienst
            // gerade ausschliesst — der Befehl zum Nachsehen, warum eine Anwendung nicht
            // erkannt wird, zeigte damit etwas anderes als die Erkennung selbst.
            HostNames = composition.HostNames,
        }, parsed.ProfileKey, output);
    }

    /// <summary>
    /// Lädt die Konfiguration, ohne zu werfen.
    /// </summary>
    /// <returns>Die Konfiguration, oder <c>null</c> samt Begründung in <paramref name="problem"/>.</returns>
    private static AppConfig? TryLoad(ConfigStore store, out string? problem)
    {
        try
        {
            AppConfig config = store.Load();
            problem = null;
            return config;
        }
        catch (StorageException ex)
        {
            // ConfigException und ConfigValidationException sind beide StorageException: Fuer
            // den Aufrufer macht es keinen Unterschied, ob die Datei fehlt, kaputt ist oder
            // eine Regel verletzt - er soll dieselbe Anleitung bekommen, mit dem jeweiligen
            // Grund darin.
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Die Protokollfabrik auf die Konsole.
    /// </summary>
    /// <remarks>
    /// Die Stufe kommt aus <c>logging.level</c>; ohne Konfiguration gilt <c>warning</c>. Das
    /// ist mit Absicht nicht <c>info</c>: Die Anzeige dieser Befehle ist der Bericht selbst,
    /// und Protokollzeilen mittendrin machen ihn unlesbar.
    /// </remarks>
    private static ILoggerFactory CreateLoggers(AppConfig? config) =>
        LoggerFactory.Create(builder =>
        {
            _ = builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
                options.UseUtcTimestamp = false;
            });

            _ = builder.SetMinimumLevel(config?.Logging.Level switch
            {
                "debug" => LogLevel.Debug,
                "info" => LogLevel.Information,
                "warning" => LogLevel.Warning,
                "error" => LogLevel.Error,
                _ => LogLevel.Warning,
            });
        });

    /// <summary>
    /// Stellt die Konsole auf UTF-8.
    /// </summary>
    /// <remarks>
    /// Ohne das erscheinen Umlaute in der klassischen Eingabeaufforderung als Kästchen. Der
    /// Versuch darf scheitern — bei umgeleiteter Ausgabe gibt es keine Konsole, und daran soll
    /// kein Befehl hängenbleiben.
    /// </remarks>
    private static void UseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
