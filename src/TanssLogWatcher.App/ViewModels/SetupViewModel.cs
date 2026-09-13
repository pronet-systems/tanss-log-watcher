using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api.Auth;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Api.Repository;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Monitoring;
using TanssLogWatcher.Monitoring.Model;
using TanssLogWatcher.Storage;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Secrets;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Der Einrichtungsassistent: Anmelden, zuordnen, ablegen.
/// </summary>
/// <remarks>
/// <para><b>Zwei Schritte, und zwar zwingend.</b> Die Prüfung lässt in <c>config.json</c> nur
/// Fernwartungstypen ab 1000 zu — die Kennung „nicht überwachen“ darf dort gar nicht stehen.
/// Eine Einrichtung, die nur Adresse und Anmeldung erfragte, schriebe deshalb eine Datei, die
/// beim nächsten Laden als ungültig zurückkäme. Der zweite Schritt ist kein Komfort, sondern
/// die Bedingung dafür, dass am Ende überhaupt etwas Gültiges entsteht.</para>
///
/// <para><b>Das Kennwort wird nicht abgelegt.</b> Es dient einmalig der Anmeldung; abgelegt
/// wird allein das Arbeitstoken, und zwar über DPAPI an den Windows-Benutzer gebunden. Ein
/// Kennwort in einer Konfigurationsdatei wäre genau der Fehler, den dieses Werkzeug bei
/// anderen anprangert.</para>
///
/// <para><b>Die Reihenfolge am Ende ist nicht beliebig.</b> Erst das Token, dann die
/// Konfiguration: Läge die Konfiguration schon vor und das Token noch nicht, meldete der
/// nächste Start „eingerichtet, aber kein Token“ — ein Zustand, den niemand herbeiführen
/// wollte.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IConfigStore _store;

    private string? _token;

    /// <summary>Baut den Assistenten für eine laufende Anwendung.</summary>
    /// <param name="host">Die Laufzeit; sie wird am Ende neu geladen.</param>
    /// <param name="store">Der Konfigurationsort; ohne Angabe der vorgesehene.</param>
    public SetupViewModel(AppHost host, IConfigStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
        _store = store ?? ConfigStore.Default();

        // Eine vorhandene Konfiguration wird als Vorbelegung gelesen, nicht ueberschrieben:
        // Wer den Assistenten zum Berichtigen der Adresse oeffnet, soll nicht alles neu tippen.
        if (_host.Config is { } existing)
        {
            _baseUrl = existing.Tanss.BaseUrl;
            _verifyTls = existing.Tanss.VerifyTls;
        }
    }

    /// <summary>Die Basisadresse der Instanz; sie muss auf <c>/backend</c> enden.</summary>
    [ObservableProperty]
    private string _baseUrl = string.Empty;

    /// <summary>Der Anmeldename in TANSS.</summary>
    [ObservableProperty]
    private string _userName = string.Empty;

    /// <summary>
    /// Der zweite Faktor, falls die Instanz einen verlangt.
    /// </summary>
    /// <remarks>
    /// Leer lassen, wenn keiner eingerichtet ist. Ein Feld, das nur bei Bedarf auszufüllen
    /// ist, steht sichtbar da, statt erst nach einer abgewiesenen Anmeldung aufzutauchen.
    /// </remarks>
    [ObservableProperty]
    private string _twoFactorToken = string.Empty;

    /// <summary>Wird das Zertifikat der Gegenstelle geprüft?</summary>
    /// <remarks>
    /// Vorbelegt mit „ja“ und im Betrieb auch so zu lassen. Das Arbeitstoken geht bei jedem
    /// Aufruf über diese Leitung, und TANSS kann ein ausgestelltes Token nicht widerrufen.
    /// </remarks>
    [ObservableProperty]
    private bool _verifyTls = true;

    /// <summary>Der Schritt, auf dem der Assistent gerade steht.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnLogin))]
    [NotifyPropertyChangedFor(nameof(IsOnMapping))]
    private SetupStep _step = SetupStep.Login;

    /// <summary>Steht der Assistent auf der Anmeldung?</summary>
    public bool IsOnLogin => Step == SetupStep.Login;

    /// <summary>Steht der Assistent auf der Zuordnung?</summary>
    public bool IsOnMapping => Step == SetupStep.Mapping;

    /// <summary>Läuft gerade ein Netzaufruf?</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Der Fehlertext der letzten Handlung; leer, wenn alles ging.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    /// <summary>Gibt es etwas zu melden?</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Was gerade gelungen ist — die Rückmeldung nach der Anmeldung.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuccess))]
    private string _success = string.Empty;

    /// <summary>Gibt es eine Erfolgsmeldung?</summary>
    public bool HasSuccess => !string.IsNullOrEmpty(Success);

    /// <summary>Die Mitarbeiterkennung, die die Anmeldung geliefert hat.</summary>
    [ObservableProperty]
    private int _employeeId;

    /// <summary>Die zuzuordnenden Anwendungen, mit den Anbindungen der Instanz.</summary>
    public ObservableCollection<MappingRow> Mappings { get; } = [];

    /// <summary>Die Anbindungen aus TANSS, vorangestellt „Nicht überwachen“.</summary>
    public ObservableCollection<SystemRow> Systems { get; } = [];

    /// <summary>Ist mindestens eine Anwendung zugeordnet?</summary>
    /// <remarks>
    /// Die Schaltfläche „Fertigstellen“ hängt daran, weil eine Konfiguration ohne jede Regel
    /// die Prüfung nicht besteht. Der Satz daneben sagt das, statt den Benutzer raten zu lassen,
    /// warum nichts passiert.
    /// </remarks>
    public bool CanFinish => Mappings.Any(m => m.IsMapped);

    /// <summary>Wurde die Einrichtung abgeschlossen?</summary>
    public bool IsFinished { get; private set; }

    /// <summary>
    /// Meldet sich an, holt das Arbeitstoken und die Anbindungen.
    /// </summary>
    /// <remarks>
    /// Abgelegt wird hier noch nichts. Erst wenn auch die Zuordnung steht, darf etwas auf die
    /// Platte — sonst bliebe bei einem Abbruch ein Token ohne Konfiguration zurück.
    /// </remarks>
    /// <param name="password">Das Kennwort; es wird nicht aufbewahrt.</param>
    [RelayCommand]
    private async Task LoginAsync(string? password)
    {
        Error = string.Empty;
        Success = string.Empty;

        string url = BaseUrl.Trim().TrimEnd('/');

        if (string.IsNullOrWhiteSpace(url))
        {
            Error = "Die Basisadresse fehlt. Erwartet wird etwa https://tanss.kunde.de/backend.";
            return;
        }

        if (!url.EndsWith("/backend", StringComparison.OrdinalIgnoreCase))
        {
            Error = $"Die Basisadresse muss auf /backend enden — also „{url}/backend“. "
                + "Ohne diesen Teil zeigt sie auf die Weboberfläche und nicht auf die Schnittstelle.";
            return;
        }

        if (string.IsNullOrWhiteSpace(UserName))
        {
            Error = "Der Anmeldename fehlt.";
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            Error = "Das Kennwort fehlt.";
            return;
        }

        IsBusy = true;
        try
        {
            BaseUrl = url;

            // Der Schalter muss schon fuer die Anmeldung gelten und nicht erst danach: Bei einer
            // Testinstanz mit selbst ausgestelltem Zertifikat scheitert sonst bereits dieser
            // erste Aufruf - und zwar mit einer Zertifikatsmeldung, obwohl der Benutzer die
            // Pruefung sichtbar abgeschaltet hat.
            using HttpClientHandler? handler = VerifyTls ? null : new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };

            LoginResult login;
            try
            {
                login = await TanssAuth.LoginAsync(
                    url, UserName, password,
                    string.IsNullOrWhiteSpace(TwoFactorToken) ? null : TwoFactorToken.Trim(),
                    handler)
                    .ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Error = "Die Anmeldung selbst ist fehlgeschlagen: " + Redaction.Scrub(ex.Message);
                return;
            }

            if (login.EmployeeId <= 0)
            {
                Error = "TANSS hat die Anmeldung angenommen, aber keine Mitarbeiterkennung "
                    + "geliefert. Ohne sie lässt sich keine Fernwartung zuordnen.";
                return;
            }

            EmployeeId = login.EmployeeId;

            // Das Praefix bleibt dran. Der Kopfzeileneintrag apiToken muss es tragen, und der
            // Wert kommt von TANSS bereits damit - abschneiden und spaeter wieder ansetzen
            // waere ein Umweg mit genau einer Stelle, an der es vergessen werden kann.
            string session = login.ApiKey?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(session))
            {
                Error = "TANSS hat die Anmeldung angenommen, aber kein Sitzungstoken geliefert.";
                return;
            }

            // Ab hier traegt der Zugang das Sitzungstoken. Er dient genau zwei Aufrufen:
            // dem Praegen des Arbeitstokens und danach dem Holen der Anbindungen.
            using TanssClient client = new(ProbeConfig(url).ToTanssOptions(),
                                           new FixedTokenStore(session));

            // NUR EINMAL PRAEGEN. Jeder Praegevorgang stellt ein neues Jahrestoken aus, das
            // TANSS 10.10.0 nicht widerrufen kann. Wer nach einem Fehlschlag im zweiten Schritt
            // auf "Zurueck" und wieder auf "Anmelden" drueckt, hinterliesse sonst bei jedem
            // Durchgang ein weiteres gueltiges Jahrestoken in der Instanz, von dem niemand mehr
            // weiss. Liegt schon eines aus diesem Durchlauf vor, wird es weiterverwendet.
            if (string.IsNullOrWhiteSpace(_token))
            {
                try
                {
                    _token = await TanssAuth.MintAsync(client, durationDays: 365)
                        .ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Error = "Die Anmeldung hat geklappt, aber das Prägen des Arbeitstokens nicht: "
                        + Redaction.Scrub(ex.Message)
                        + " — In TANSS ist das ein eigenes Recht am Mitarbeiter. Ohne dieses "
                        + "Recht lässt sich das Werkzeug nicht einrichten.";
                    return;
                }

                if (string.IsNullOrWhiteSpace(_token))
                {
                    Error = "TANSS hat das Prägen quittiert, aber kein Token geliefert.";
                    return;
                }
            }

            try
            {
                await LoadSystemsAsync(url).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Error = "Anmeldung und Token stehen, aber die Fernwartungsanbindungen liessen "
                    + "sich nicht holen: " + Redaction.Scrub(ex.Message);
                return;
            }

            // In den Versuch hinein: Ein Wert, der kein JWT ist, wirft hier. Ohne Fangen risse
            // das die ganze Anwendung ab - unmittelbar nach einer geglueckten Anmeldung, mit
            // einem frisch gepraegten Jahrestoken, das dann niemand mehr ablegen koennte.
            DateTimeOffset? expiry = null;
            try
            {
                expiry = TanssAuth.ExpiresAt(_token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("Ablaufdatum nicht lesbar: " + ex.Message);
            }

            Success = expiry is { } until
                ? string.Create(CultureInfo.CurrentCulture,
                    $"Angemeldet als Mitarbeiter {EmployeeId}. Das Arbeitstoken ist geprägt und "
                    + $"trägt bis {Texts.Day(until)}.")
                : string.Create(CultureInfo.CurrentCulture,
                    $"Angemeldet als Mitarbeiter {EmployeeId}. Das Arbeitstoken ist geprägt.");

            Step = SetupStep.Mapping;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Legt Token und Konfiguration ab und lädt die Laufzeit neu.
    /// </summary>
    /// <remarks>
    /// Geschrieben wird erst hier, und in genau dieser Reihenfolge: Token, Konfiguration,
    /// Neuladen. Schlägt das Schreiben der Konfiguration fehl, liegt zwar ein Token im Profil —
    /// das ist folgenlos, weil ohne Konfiguration nichts damit geschieht.
    /// </remarks>
    [RelayCommand]
    private void Finish()
    {
        Error = string.Empty;

        if (_token is null)
        {
            Error = "Es liegt kein Arbeitstoken vor. Der Schritt „Anmelden“ ist noch offen.";
            return;
        }

        List<MonitoringEntry> entries = Mappings
            .Where(m => m.IsMapped)
            .Select(m => new MonitoringEntry
            {
                Key = m.Key,
                RemoteSupportTypeId = m.SelectedSystem!.Id,
            })
            .ToList();

        if (entries.Count == 0)
        {
            Error = "Mindestens eine Anwendung muss einem Fernwartungstyp zugeordnet sein. "
                + "Ohne eine einzige Regel beobachtet das Werkzeug nichts und schriebe nie "
                + "eine Fernwartung.";
            return;
        }

        // Auf dem Vorhandenen aufsetzen statt neu bauen: Proxy, Protokollierung und der Takt
        // der Beobachtung haben in diesem Assistenten keine Eingabefelder. Ein von Grund auf
        // neues Stueck setzte sie beim zweiten Durchlauf stillschweigend auf die Vorgabe
        // zurueck - und ein Proxy, der ploetzlich weg ist, ist beim Kunden eine Stoerung ohne
        // erkennbare Ursache.
        AppConfig existing = _host.Config ?? new AppConfig
        {
            Tanss = new TanssSection { BaseUrl = BaseUrl, EmployeeId = EmployeeId },
        };

        AppConfig config = existing with
        {
            Tanss = existing.Tanss with
            {
                BaseUrl = BaseUrl,
                EmployeeId = EmployeeId,
                VerifyTls = VerifyTls,
            },
            Monitoring = entries,
        };

        // Erst pruefen, dann ablegen - und die Konfiguration vor dem Token. Andersherum bliebe
        // nach einer abgewiesenen Konfiguration ein frisch gepraegtes Jahrestoken im Profil
        // liegen, das das bisherige ueberschrieben hat und das TANSS nicht widerrufen kann.
        try
        {
            ConfigValidator.Validate(config, _store.Path);
        }
        catch (ConfigValidationException ex)
        {
            Error = "Die Angaben ergeben keine gültige Konfiguration: "
                + string.Join(" ", ex.Problems);
            return;
        }

        try
        {
            _store.Save(config);
            DpapiTokenStore.Default().Write(_token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = "Das Ablegen ist fehlgeschlagen: " + Redaction.Scrub(ex.Message);
            return;
        }

        if (!_host.Reload())
        {
            // Geschrieben ist geschrieben; nur in Betrieb ist es nicht. Der Grund steht im
            // Betriebszustand, den die Verbindungsseite zeigt.
            Error = "Alles ist abgelegt, liess sich aber nicht in Betrieb nehmen. Der Grund "
                + "steht unter „Verbindung“.";
            return;
        }
        IsFinished = true;
    }

    /// <summary>Holt die Anbindungen der Instanz und baut daraus die Zuordnungsliste.</summary>
    /// <remarks>
    /// <para>Der Zugang entsteht hier eigens und trägt das frisch geprägte Token: Die Laufzeit
    /// hat noch keines — sie ist ja gerade nicht eingerichtet, und der Tokenspeicher im Profil
    /// ist noch leer.</para>
    /// <para>Gebaut wird nur der Zugang und nicht der ganze Zusammenbau. Jener legte
    /// <c>state.db</c> an und rührte den Tokenspeicher an; beides hat auf einem Rechner, auf
    /// dem die Einrichtung womöglich gleich abgebrochen wird, nichts verloren.</para>
    /// </remarks>
    private async Task LoadSystemsAsync(string url)
    {
        using TanssClient client = new(ProbeConfig(url).ToTanssOptions(),
                                       new FixedTokenStore(_token!));

        IReadOnlyList<RemoteSupportSystem> systems =
            await new RemoteSupportRepository(client, EmployeeId)
                .ListSystemsAsync().ConfigureAwait(true);

        Systems.Clear();
        Systems.Add(SystemRow.None);
        foreach (RemoteSupportSystem system in systems.OrderBy(s => s.Name, StringComparer.CurrentCulture))
        {
            Systems.Add(SystemRow.From(system));
        }

        BuildMappings();
    }

    /// <summary>
    /// Die Verbindungsdaten als Konfiguration — nur für die Aufrufe während der Einrichtung.
    /// </summary>
    /// <remarks>
    /// <para>Dieses Stück geht nie auf die Platte. Es trägt allein, was der Zugang braucht:
    /// Adresse, Mitarbeiterkennung und die Zertifikatsprüfung. Die Mitarbeiterkennung ist dabei
    /// nicht bloß Beiwerk — der Zugang hängt sie auf den Routen unter <c>/api/v1</c> als
    /// <c>loggedInUserId</c> an, und ohne sie scheitert bereits das Prägen.</para>
    /// <para><c>Monitoring</c> bleibt leer: Die Prüfung, die mindestens eine Regel verlangt,
    /// läuft erst beim Speichern — und bis dahin hat der zweite Schritt die Regeln geliefert.</para>
    /// </remarks>
    private AppConfig ProbeConfig(string url) => new()
    {
        Tanss = new TanssSection
        {
            BaseUrl = url,
            EmployeeId = EmployeeId,
            VerifyTls = VerifyTls,
        },
        Monitoring = [],
    };

    /// <summary>
    /// Stellt den Profilkatalog neben die Anbindungen.
    /// </summary>
    /// <remarks>
    /// Vorbelegt wird nichts geraten: Eine automatische Zuordnung nach Namensähnlichkeit
    /// („AnyDesk“ auf „AnyDesk“) läge meistens richtig und gelegentlich falsch — und eine
    /// falsche Zuordnung bucht Arbeitszeit auf den falschen Typ, ohne dass es jemand bemerkt.
    /// Übernommen wird allein, was bereits in einer vorhandenen Konfiguration steht.
    /// </remarks>
    private void BuildMappings()
    {
        Dictionary<string, int> existing = _host.Config?.Monitoring
            .ToDictionary(e => e.Key, e => e.RemoteSupportTypeId, StringComparer.OrdinalIgnoreCase)
            ?? [];

        Mappings.Clear();

        foreach (MonitoringSetting setting in MonitoringProfiles.Reconcile([]))
        {
            MonitoringProfile? profile = MonitoringProfiles.Find(setting.Key);
            SystemRow selected = SystemRow.None;

            if (existing.TryGetValue(setting.Key, out int typeId))
            {
                selected = Systems.FirstOrDefault(s => s.Id == typeId) ?? SystemRow.None;
            }

            MappingRow row = new(setting.Key, profile?.TypeDescription ?? setting.Key, selected);
            row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanFinish));
            Mappings.Add(row);
        }

        OnPropertyChanged(nameof(CanFinish));
    }
}

/// <summary>Die Schritte des Assistenten.</summary>
public enum SetupStep
{
    /// <summary>Adresse und Anmeldung.</summary>
    Login,

    /// <summary>Anwendungen den Fernwartungstypen zuordnen.</summary>
    Mapping,
}

/// <summary>Eine Zeile der Zuordnung: eine Anwendung und der Typ, auf den sie bucht.</summary>
/// <remarks>
/// Dieselbe Zeile trägt den Assistenten und die Überwachungsseite. Beide stellen dieselbe
/// Frage — welche Anwendung bucht worauf —, und zwei Zeilentypen für eine Frage liefen
/// auseinander, sobald jemand die eine erweitert und die andere vergisst. Die Überwachungsseite
/// zeigt zusätzlich Verfahren und Ausschlussliste; im Assistenten bleiben sie leer, weil dort
/// noch nichts eingestellt sein kann.
/// </remarks>
public sealed partial class MappingRow : ObservableObject
{
    /// <summary>Baut eine Zeile.</summary>
    /// <param name="key">Der Profilschlüssel, etwa <c>mstsc</c>.</param>
    /// <param name="application">Der sprechende Name der Anwendung.</param>
    /// <param name="selected">Die vorbelegte Anbindung.</param>
    /// <param name="method">Wie das Ziel ermittelt wird; leer, wenn unbekannt.</param>
    /// <param name="excludedIps">Die ausgeschlossenen Adressbereiche.</param>
    public MappingRow(string key, string application, SystemRow selected,
                      string method = "", string excludedIps = "")
    {
        Key = key;
        Application = application;
        Method = method;
        _excludedIps = excludedIps;
        _selectedSystem = selected;
    }

    /// <summary>Der Profilschlüssel.</summary>
    public string Key { get; }

    /// <summary>Der sprechende Name der Anwendung.</summary>
    public string Application { get; }

    /// <summary>Wie das Ziel ermittelt wird — Titel, Muster im Titel oder Netzwerkverbindung.</summary>
    public string Method { get; }

    /// <summary>
    /// Die ausgeschlossenen Adressen und Netze, durch Semikolon getrennt.
    /// </summary>
    /// <remarks>
    /// <para>Beschreibbar, und das ist der Punkt: Hier stand erst ein Feld, dessen Eingaben
    /// nirgends ankamen, danach eine reine Anzeige. Beides war falsch — ein Ausschluss ist die
    /// einzige Möglichkeit, das eigene Netz von der Buchung auszunehmen, und ohne ihn wird jede
    /// Sitzung zum Kunden gebucht, auch die zum Server im Nebenraum.</para>
    /// <para>Semikolon als Trennzeichen, weil <see cref="Monitoring.IpFilter.ExcludeSeparator"/>
    /// es so erwartet — dieselbe Schreibweise wie in <c>config.json</c>. Geprüft wird beim
    /// Speichern, nicht beim Tippen: Wer mitten in „192.168." steht, hat noch keinen Fehler
    /// gemacht.</para>
    /// </remarks>
    [ObservableProperty]
    private string _excludedIps;

    /// <summary>Die gewählte Anbindung.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMapped))]
    private SystemRow? _selectedSystem;

    /// <summary>Ist diese Anwendung einem echten Fernwartungstyp zugeordnet?</summary>
    public bool IsMapped => SelectedSystem is { } system
        && system.Id != MonitoringSetting.NotMonitored;
}
