using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TanssLogWatcher.App.Runtime;

/// <summary>
/// Sucht neue Fassungen bei GitHub, lädt sie und startet das Setup.
/// </summary>
/// <remarks>
/// <para><b>Herunterladen und ausführen ist das Gefährlichste, was dieses Werkzeug tut.</b>
/// Alles andere liest Fenster oder spricht mit einer Instanz im eigenen Haus; hier kommt eine
/// ausführbare Datei aus dem Netz und wird gestartet. Deshalb gelten drei Regeln, die nicht
/// verhandelbar sind:</para>
///
/// <list type="number">
/// <item><b>Nie ohne Zustimmung.</b> Geprüft wird von selbst, geladen und installiert nur auf
/// ausdrückliche Anweisung. Ein Werkzeug, das Fenstertitel mitliest und sich dabei
/// unbeaufsichtigt selbst austauscht, wäre nicht mehr nachvollziehbar.</item>
/// <item><b>Nur über HTTPS und nur von <c>github.com</c>.</b> Die Adresse kommt zwar aus der
/// Antwort von GitHub, aber geprüft wird sie trotzdem: Eine Veröffentlichung darf beliebige
/// Anhänge tragen, und <see cref="IsTrustedHost"/> ist die Stelle, an der eine Adresse
/// ausserhalb der eigenen Veröffentlichungen auffliegt.</item>
/// <item><b>Prüfsumme vor Ausführung.</b> Was nicht zum veröffentlichten SHA256 passt, wird
/// gelöscht statt gestartet.</item>
/// </list>
///
/// <para><b>Was die Prüfsumme leistet — und was nicht.</b> Sie stammt aus derselben
/// Veröffentlichung wie das Setup. Wer die Veröffentlichung selbst verändern kann, ändert beides
/// und die Prüfung schlägt nicht an. Sie fängt also einen abgebrochenen oder unterwegs
/// verfälschten Download, <b>nicht</b> eine übernommene Veröffentlichung. Der Schutz dagegen
/// wäre eine Code-Signatur, und die gibt es für dieses Projekt noch nicht — der Hinweis darauf
/// gehört deshalb sichtbar in die Oberfläche und nicht nur in diesen Kommentar.</para>
///
/// <para><b>Beendet wird geordnet und von uns selbst.</b> Das Setup schliesst laufende
/// Anwendungen über den Mutex; unser Hauptfenster verweigert das Schliessen aber, weil ein
/// Klick auf das Kreuz nur zuklappt. Der Installer bliebe hängen. Vor allem aber: Das geordnete
/// Ende sichert die gerade laufenden Fernwartungen — ein abgeschossener Prozess verlöre genau
/// die Sitzung, die noch offen war.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class UpdateService : IDisposable
{
    /// <summary>Das Verzeichnis der Veröffentlichungen.</summary>
    public const string Repository = "pronet-systems/tanss-log-watcher";

    /// <summary>Wie oft von selbst nachgesehen wird.</summary>
    /// <remarks>
    /// Täglich. Häufiger wäre für ein Werkzeug, das ein paarmal im Jahr eine neue Fassung
    /// bekommt, nur Verkehr; seltener hiesse, eine Fehlerbehebung wochenlang nicht zu bemerken.
    /// Die unangemeldete Schnittstelle von GitHub erlaubt 60 Abfragen je Stunde — eine am Tag
    /// bleibt weit darunter.
    /// </remarks>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly Timer _timer;
    private bool _disposed;

    /// <summary>Baut den Dienst und beginnt mit der ersten Prüfung nach kurzer Anlaufzeit.</summary>
    /// <param name="startDelay">
    /// Wartezeit bis zur ersten Prüfung; ohne Angabe eine Minute. Sie hält den Start frei: Beim
    /// Anmelden an Windows ist das Netz oft noch nicht da, und ein Fehlschlag in der ersten
    /// Sekunde wäre kein Befund, sondern ein Zufall.
    /// </param>
    public UpdateService(TimeSpan? startDelay = null)
    {
        _http = new HttpClient { Timeout = DownloadTimeout };

        // GitHub weist Anfragen ohne Kennung ab. Die Fassung steht mit drin, damit in den
        // Zugriffsprotokollen steht, welche Fassung draussen noch laeuft.
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("TanssLogWatcher", CurrentVersion.ToString()));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        _timer = new Timer(_ => _ = CheckSafelyAsync(), null,
                           startDelay ?? TimeSpan.FromMinutes(1), CheckInterval);
    }

    /// <summary>Eine Prüfung ist abgeschlossen; wird auf einem Hintergrundstrang ausgelöst.</summary>
    public event EventHandler<UpdateCheckState>? CheckCompleted;

    /// <summary>Die zuletzt gefundene neue Fassung, oder <c>null</c>.</summary>
    public AvailableUpdate? Available { get; private set; }

    /// <summary>Wie die letzte Prüfung ausgegangen ist.</summary>
    public UpdateCheckState State { get; private set; } = UpdateCheckState.Unknown;

    /// <summary>Der Grund, wenn die letzte Prüfung fehlschlug.</summary>
    public string? Problem { get; private set; }

    /// <summary>Die Fassung, die gerade läuft.</summary>
    /// <remarks>
    /// Aus <c>InformationalVersion</c> und nicht aus <c>Version</c>: Letztere trägt eine vierte
    /// Stelle, die hier nichts bedeutet, und schnitte einen Vorabzusatz ab.
    /// </remarks>
    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>
    /// Fragt GitHub nach der neuesten Veröffentlichung.
    /// </summary>
    /// <remarks>
    /// <c>/releases/latest</c> übergeht Entwürfe und Vorabfassungen von sich aus — genau das
    /// ist gewollt: Eine Betafassung soll niemandem angeboten werden, der nicht ausdrücklich
    /// danach gefragt hat.
    /// </remarks>
    /// <param name="ct">Abbruchmarke.</param>
    public async Task<UpdateCheckState> CheckAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        State = UpdateCheckState.Checking;
        Problem = null;

        try
        {
            Uri address = new($"https://api.github.com/repos/{Repository}/releases/latest");

            using HttpResponseMessage response =
                await _http.GetAsync(address, ct).ConfigureAwait(false);

            // 404 ist hier der haeufigste Fall und KEIN Fehler im Programm: Es gibt noch keine
            // Veroeffentlichung, oder das Verzeichnis ist nicht oeffentlich. Beides als
            // „Pruefung fehlgeschlagen" zu melden schickte den Techniker auf die Suche nach
            // einem Netzproblem, das es nicht gibt.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Finish(UpdateCheckState.UpToDate,
                    $"Für {Repository} liegt keine abrufbare Veröffentlichung vor. Entweder gibt "
                    + "es noch keine, oder das Verzeichnis ist nicht öffentlich — in beiden "
                    + "Fällen ist von hier aus nichts zu holen.");
            }

            if (response.StatusCode == (System.Net.HttpStatusCode)403
                || response.StatusCode == (System.Net.HttpStatusCode)429)
            {
                return Finish(UpdateCheckState.Failed,
                    "GitHub hat die Abfrage vorerst abgewiesen; unangemeldet sind 60 Abfragen je "
                    + "Stunde erlaubt. Die nächste selbsttätige Prüfung läuft morgen.");
            }

            _ = response.EnsureSuccessStatusCode();

            string payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            GitHubRelease? release = JsonSerializer.Deserialize<GitHubRelease>(payload);

            if (release is null || release.Draft || release.Prerelease)
            {
                return Finish(UpdateCheckState.UpToDate, null);
            }

            if (!TryParseTag(release.TagName, out Version? tagVersion) || tagVersion is not { } version)
            {
                return Finish(UpdateCheckState.Failed,
                    $"Die Marke „{release.TagName}“ ist keine Fassungsnummer, mit der sich "
                    + "vergleichen liesse.");
            }

            if (version <= CurrentVersion)
            {
                return Finish(UpdateCheckState.UpToDate, null);
            }

            GitHubAsset? setup = release.Assets.FirstOrDefault(
                a => a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase));

            if (setup is null)
            {
                return Finish(UpdateCheckState.Failed,
                    $"Die Veröffentlichung {release.TagName} hängt kein Setup an. Dann ist sie "
                    + "von Hand zu holen.");
            }

            GitHubAsset? checksum = release.Assets.FirstOrDefault(
                a => a.Name.Equals(setup.Name + ".sha256", StringComparison.OrdinalIgnoreCase));

            if (!Uri.TryCreate(setup.DownloadUrl, UriKind.Absolute, out Uri? setupUrl)
                || !IsTrustedHost(setupUrl))
            {
                return Finish(UpdateCheckState.Failed,
                    "Die Veröffentlichung verweist für das Setup auf eine Adresse ausserhalb von "
                    + "github.com. Das wird nicht geladen.");
            }

            Uri? checksumUrl = null;
            if (checksum is not null
                && Uri.TryCreate(checksum.DownloadUrl, UriKind.Absolute, out Uri? parsed)
                && IsTrustedHost(parsed))
            {
                checksumUrl = parsed;
            }

            _ = Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out Uri? page);

            Available = new AvailableUpdate(version, release.TagName, setupUrl, setup.Name,
                                            checksumUrl, setup.Size,
                                            page ?? new Uri($"https://github.com/{Repository}/releases"));

            return Finish(UpdateCheckState.UpdateAvailable, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Finish(UpdateCheckState.Failed,
                "Die Prüfung auf Aktualisierungen ist fehlgeschlagen: " + ex.Message);
        }
    }

    /// <summary>
    /// Lädt das Setup, prüft die Prüfsumme und gibt den Pfad zurück.
    /// </summary>
    /// <remarks>
    /// Geladen wird in einen eigenen Ordner unter <c>%TEMP%</c> und nicht neben das Programm:
    /// Dorthin zu schreiben, während es läuft, ist genau der Vorgang, den das Setup gleich
    /// selbst erledigen soll.
    /// </remarks>
    /// <param name="update">Die zu ladende Fassung.</param>
    /// <param name="progress">Fortschritt von 0 bis 1; darf <c>null</c> sein.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>
    /// Der Pfad des geladenen Setups <b>und</b> die Aussage, ob tatsächlich gegen eine
    /// Prüfsumme verglichen wurde.
    /// <para>Beides zusammen und nicht nur der Pfad: Ein Aufrufer, der nur den Pfad bekommt,
    /// kann „geladen“ nicht von „geladen und geprüft“ unterscheiden — und meldet dann das eine,
    /// während das andere gemeint war.</para>
    /// </returns>
    /// <exception cref="InvalidOperationException">Die Prüfsumme passt nicht.</exception>
    public async Task<DownloadedUpdate> DownloadAsync(AvailableUpdate update,
                                                      IProgress<double>? progress = null,
                                                      CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string folder = Path.Combine(Path.GetTempPath(), "TanssLogWatcher.Update");
        _ = Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, update.SetupName);

        using (HttpResponseMessage response = await _http
            .GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false))
        {
            _ = response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? update.Bytes;
            long done = 0;

            await using Stream from = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using FileStream to = File.Create(target);

            byte[] buffer = new byte[81920];
            int read;

            while ((read = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;

                if (total > 0)
                {
                    progress?.Report(Math.Min(1d, done / (double)total));
                }
            }
        }

        string? expected = await ReadChecksumAsync(update, ct).ConfigureAwait(false);

        if (expected is null)
        {
            // Kein Abbruch, aber auch kein Schweigen: Ohne veroeffentlichte Pruefsumme faellt
            // ein unterwegs verfaelschter Download nicht mehr auf. Verified BLEIBT FALSCH -
            // und die Oberflaeche sagt genau das, statt eine Pruefung zu behaupten, die
            // nicht stattgefunden hat.
            return new DownloadedUpdate(target, Verified: false);
        }

        string actual = await ComputeSha256Async(target, ct).ConfigureAwait(false);

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(target);

            throw new InvalidOperationException(
                "Die geladene Datei stimmt nicht mit der veröffentlichten Prüfsumme überein und "
                + "wurde gelöscht. Erwartet war " + expected + ", gemessen wurde " + actual
                + ". Der Download war unvollständig oder die Datei ist unterwegs verändert "
                + "worden; in beiden Fällen wird sie nicht ausgeführt.");
        }

        // Erst HIER ist der Vergleich wirklich gelaufen und ausgegangen.
        return new DownloadedUpdate(target, Verified: true);
    }

    /// <summary>
    /// Startet das geprüfte Setup und meldet, dass die Anwendung sich beenden soll.
    /// </summary>
    /// <remarks>
    /// <para>Das Setup läuft <b>nicht</b> still. Es ist unsigniert, SmartScreen wird warnen, und
    /// ein Fortschritt, den niemand sieht, sieht aus wie ein Werkzeug, das nichts tut. Der
    /// Benutzer soll dem Austausch zusehen können.</para>
    /// <para>Beendet wird von hier aus ausdrücklich nicht — das gehört der Anwendung, die dabei
    /// ihre laufenden Sitzungen sichert. Diese Methode gibt nur zurück, dass es so weit ist.</para>
    /// </remarks>
    /// <param name="setupPath">Der Pfad des geprüften Setups.</param>
    public static void StartInstaller(string setupPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupPath);

        if (!File.Exists(setupPath))
        {
            throw new FileNotFoundException(
                "Das geladene Setup liegt nicht mehr dort, wo es abgelegt wurde.", setupPath);
        }

        // Ohne Schalter, also mit sichtbarem Assistenten. /SILENT waere hier falsch: Die Datei
        // ist unsigniert, SmartScreen haelt sie ohnehin an, und ein Austausch, von dem der
        // Benutzer nichts sieht, ist bei einem Werkzeug dieser Art nicht angebracht.
        // UseShellExecute, damit die Rechteanhebung und SmartScreen ueberhaupt greifen koennen.
        ProcessStartInfo start = new()
        {
            FileName = setupPath,
            UseShellExecute = true,
        };

        _ = Process.Start(start);
    }

    /// <summary>Gibt Zeitgeber und Zugang frei.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        _http.Dispose();
    }

    /// <summary>
    /// Gehört die Adresse zu GitHub und läuft sie über HTTPS?
    /// </summary>
    /// <remarks>
    /// Geprüft wird auf die Domäne und nicht auf eine Zeichenkette am Anfang: <c>https://
    /// github.com.angreifer.example</c> beginnt ebenfalls mit dem erwarteten Text.
    /// </remarks>
    internal static bool IsTrustedHost(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (!address.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return false;
        }

        string host = address.Host;

        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Liest eine Marke wie <c>v0.2.0</c> als Fassungsnummer.</summary>
    /// <remarks>
    /// Ein Vorabzusatz wird abgeschnitten. Er taucht hier ohnehin nicht auf, weil
    /// <c>/releases/latest</c> Vorabfassungen übergeht; ein Wurf an dieser Stelle wäre trotzdem
    /// der falsche Weg, eine ungewohnte Marke zu behandeln.
    /// </remarks>
    internal static bool TryParseTag(string? tag, out Version? version)
    {
        version = null;

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        Match match = VersionPattern().Match(tag);

        return match.Success && Version.TryParse(match.Groups[1].Value, out version);
    }

    [GeneratedRegex(@"(\d+(?:\.\d+){1,3})")]
    private static partial Regex VersionPattern();

    private static Version ReadCurrentVersion()
    {
        string? raw = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return TryParseTag(raw, out Version? version) && version is not null
            ? version
            : new Version(0, 0);
    }

    private async Task<string?> ReadChecksumAsync(AvailableUpdate update, CancellationToken ct)
    {
        if (update.ChecksumUrl is null)
        {
            return null;
        }

        try
        {
            string raw = await _http.GetStringAsync(update.ChecksumUrl, ct).ConfigureAwait(false);

            // Format wie bei sha256sum: "<hash> *<dateiname>". Genommen wird das erste Wort.
            string first = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;

            return first.Length == 64 ? first : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Die Pruefsumme ist nicht zu holen. Das ist ein Mangel und kein Abbruch - die
            // Oberflaeche sagt, dass ohne sie nicht gegengeprueft werden konnte.
            return null;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);

        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task CheckSafelyAsync()
    {
        try
        {
            _ = await CheckAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Der Zeitgeber darf an einer misslungenen Pruefung nicht sterben.
            Debug.WriteLine("Aktualisierungsprüfung: " + ex.Message);
        }
    }

    private UpdateCheckState Finish(UpdateCheckState state, string? problem)
    {
        State = state;
        Problem = problem;

        if (state != UpdateCheckState.UpdateAvailable)
        {
            Available = null;
        }

        CheckCompleted?.Invoke(this, state);

        return state;
    }
}
