using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api.Auth;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Die Seite „Einstellungen“: Adresse, Mitarbeiter, Token, Anbindungen — und der Weg zur
/// Einrichtung.
/// </summary>
/// <remarks>
/// <para><b>Diese Seite trägt den Ausweg.</b> Sie ist die einzige, die auch im Zustand „nicht
/// eingerichtet“ etwas Sinnvolles zu tun anbietet, statt eine leere Tabelle zu zeigen: Das
/// Zahnrad öffnet den Assistenten. Ohne diesen Weg käme ein frisch installiertes Werkzeug nur
/// über die Kommandozeile in Betrieb — und den Befehl dafür gibt es noch nicht.</para>
///
/// <para><b>Der Verbindungstest ist folgenlos und darf deshalb an einer Schaltfläche
/// hängen.</b> <c>GET /technicians</c> liest und ändert nichts; er beantwortet Netz, Adresse,
/// Token und Recht auf einmal. Ein Test, der etwas schriebe, gehörte nicht unter eine
/// Schaltfläche, die jemand aus Neugier drückt.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class ConnectionViewModel : RuntimeViewModel
{
    private readonly SystemLookup _systems;
    private readonly UpdateService _updates;
    private string? _downloadedSetup;

    /// <summary>Baut die Seite und holt, was ohne Zutun zu holen ist.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="systems">Der gemeinsame Nachschlag für Typnamen und Farben.</param>
    /// <param name="updates">Die Suche nach neuen Fassungen.</param>
    public ConnectionViewModel(AppHost host, SystemLookup systems, UpdateService updates)
        : base(host)
    {
        ArgumentNullException.ThrowIfNull(systems);
        ArgumentNullException.ThrowIfNull(updates);

        _systems = systems;
        _updates = updates;

        host.TokenRotation.TokenChanged += OnTokenChanged;
        updates.CheckCompleted += OnUpdateCheckCompleted;

        ReadConfig();
        ApplyToken(host.TokenRotation.Token);
        ApplyUpdate();
        _ = InitializeAsync();
    }

    /// <summary>Die Basisadresse der Instanz.</summary>
    [ObservableProperty]
    private string _baseUrl = "—";

    /// <summary>Die Mitarbeiterkennung aus der Konfiguration.</summary>
    [ObservableProperty]
    private int _employeeId;

    /// <summary>Der Name des Mitarbeiters, sobald TANSS ihn genannt hat.</summary>
    [ObservableProperty]
    private string _employeeName = "—";

    /// <summary>Wird das Zertifikat der Gegenstelle geprüft?</summary>
    [ObservableProperty]
    private bool _verifyTls = true;

    /// <summary>Wann das Arbeitstoken abläuft.</summary>
    [ObservableProperty]
    private string _tokenExpiry = "—";

    /// <summary>Wie viele Tage das Token noch trägt.</summary>
    [ObservableProperty]
    private int _tokenDaysLeft;

    /// <summary>Darf der Mitarbeiter Token prägen?</summary>
    /// <remarks>
    /// <para>Nur zur Anzeige. Die Schaltfläche „Jetzt erneuern“ hängt ausdrücklich
    /// <b>nicht</b> daran, sondern an <see cref="RuntimeViewModel.IsConfigured"/>: Der Wert
    /// liesse sich allein über einen Prägeversuch mit <c>isForTesting=true</c> füllen, und der
    /// stellt in TANSS ein echtes — wenn auch nur 60 Sekunden gültiges — Token aus und
    /// hinterlässt dort aller Wahrscheinlichkeit nach einen Eintrag. Ihn bei jedem Öffnen der
    /// Seite abzusetzen hiesse, für eine Anzeige Token zu erzeugen, die niemand bestellt hat.</para>
    /// <para>Fehlt das Recht, sagt es der Prägeversuch selbst — im Klartext und erst dann,
    /// wenn ihn jemand ausgelöst hat.</para>
    /// </remarks>
    [ObservableProperty]
    private bool _canRotate;

    /// <summary>Trägt das Token überhaupt einen Ablaufanspruch?</summary>
    /// <remarks>
    /// Die Restlaufzeit und der Balken werden ausgeblendet, wenn nicht — sonst stünde dort eine
    /// Zahl, die nichts bedeutet.
    /// </remarks>
    [ObservableProperty]
    private bool _hasExpiry;

    /// <summary>Der Klartextgrund, wenn mit dem Token etwas nicht stimmt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTokenProblem))]
    private string? _tokenProblem;

    /// <summary>Stimmt mit dem Token etwas nicht?</summary>
    public bool HasTokenProblem => !string.IsNullOrEmpty(TokenProblem);

    /// <summary>Läuft gerade ein Netzaufruf?</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Die Rückmeldung der letzten Handlung.</summary>
    /// <remarks>
    /// Gesetzt wird sie ausschliesslich über <see cref="Melde"/> und
    /// <see cref="MeldeErfolg"/> — nie unmittelbar. Sonst bliebe
    /// <see cref="MessageIsSuccess"/> von der vorigen Handlung stehen, und eine Fehlermeldung
    /// trüge ein grünes Häkchen.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    [NotifyPropertyChangedFor(nameof(HasPlainMessage))]
    private string _message = string.Empty;

    /// <summary>
    /// War die letzte Handlung ein Erfolg?
    /// </summary>
    /// <remarks>
    /// <b>Ein eigenes Kennzeichen und nicht aus dem Text abgeleitet.</b> Aus einer Zeichenkette
    /// zu erraten, ob sie etwas Gutes meldet, ginge beim ersten umformulierten Satz schief —
    /// und ein grünes Häkchen über einer Fehlermeldung ist schlimmer als gar kein Zeichen.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuccess))]
    [NotifyPropertyChangedFor(nameof(HasPlainMessage))]
    private bool _messageIsSuccess;

    /// <summary>Gibt es überhaupt eine Rückmeldung?</summary>
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Ist die Rückmeldung eine Erfolgsmeldung — grün, mit Häkchen?</summary>
    public bool HasSuccess => HasMessage && MessageIsSuccess;

    /// <summary>Ist die Rückmeldung eine gewöhnliche Auskunft — blau, mit „i“?</summary>
    /// <remarks>
    /// Die beiden schliessen einander aus. Zwei feste Anzeigen statt einer mit gebundener
    /// Severity: Ein gebundener Stil auf einem <c>ui:</c>-Typ hat in diesem Haus schon einmal
    /// die Navigation <b>still</b> scheitern lassen.
    /// </remarks>
    public bool HasPlainMessage => HasMessage && !MessageIsSuccess;

    /// <summary>Meldet eine gewöhnliche Auskunft.</summary>
    /// <param name="text">Der Satz für die Oberfläche.</param>
    private void Melde(string text)
    {
        MessageIsSuccess = false;
        Message = text;
    }

    /// <summary>Meldet einen Erfolg — grün und mit Häkchen.</summary>
    /// <param name="text">Der Satz für die Oberfläche.</param>
    private void MeldeErfolg(string text)
    {
        MessageIsSuccess = true;
        Message = text;
    }

    /// <summary>Die Fernwartungsanbindungen der Instanz.</summary>
    public ObservableCollection<SystemRow> Systems { get; } = [];

    /// <summary>
    /// Die Befunde der letzten Verbindungsprüfung.
    /// </summary>
    /// <remarks>
    /// <para><b>Leer, bis wirklich geprüft wurde.</b> Hier stand vorher eine Karte mit vier fest
    /// eingebauten grünen Haken — darunter „Token trägt und lässt sich erneuern“. Sie stand auch
    /// dann grün da, wenn gar nichts eingerichtet war, wenn die Prüfung fehlschlug und, wie sich
    /// gezeigt hat, während das Token in Wahrheit auf jeder Anfrage eine 403 erzeugte. Eine
    /// Anzeige, die einen Befund behauptet, den niemand erhoben hat, ist schlimmer als gar
    /// keine: Sie beendet die Fehlersuche, bevor sie anfängt.</para>
    /// <para>Jede Zeile hier entsteht aus einer tatsächlichen Antwort oder aus einem Wert, den
    /// die Konfiguration wirklich trägt. Was nicht gemessen wurde, steht als „nicht geprüft“ da.</para>
    /// </remarks>
    public ObservableCollection<CheckRow> Checks { get; } = [];

    /// <summary>Wurde überhaupt schon geprüft?</summary>
    public bool HasChecks => Checks.Count > 0;

    /// <summary>Der Ort, an dem die Konfiguration erwartet wird.</summary>
    public string ConfigPath => Host.ConfigPath;

    /// <summary>Wurde die Einrichtung abgeschlossen? Löst das Neuaufbauen der Seite aus.</summary>
    public event EventHandler? SetupRequested;

    /// <summary>
    /// Das Setup läuft; die Anwendung soll sich jetzt geordnet beenden.
    /// </summary>
    /// <remarks>
    /// Das Beenden gehört der Anwendung und nicht diesem Stück: Dabei werden die laufenden
    /// Fernwartungen abgeschlossen und eingereiht. Ein Ansichtsmodell, das
    /// <c>Application.Shutdown</c> selbst riefe, wäre ohne Oberfläche nicht mehr prüfbar.
    /// </remarks>
    public event EventHandler? ShutdownRequested;

    // --- Aktualisierung ----------------------------------------------------

    /// <summary>Die Fassung, die gerade läuft.</summary>
    /// <remarks>
    /// Als Eigenschaft mit Sicherungsfeld und nicht als statischer Ausdruck: WPF bindet gegen
    /// Instanzmember, und ein <c>{x:Static}</c> in der Ansicht wäre die einzige Stelle, an der
    /// diese Seite anders bindet als alle anderen.
    /// </remarks>
    public string CurrentVersionText { get; } = UpdateService.CurrentVersion.ToString();

    /// <summary>Liegt eine neuere Fassung vor?</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateHeadline))]
    private bool _hasUpdate;

    /// <summary>Der Satz zum Stand der Aktualisierungsprüfung.</summary>
    [ObservableProperty]
    private string _updateText = "Noch nicht nach Aktualisierungen gesucht.";

    /// <summary>Die Überschrift der Aktualisierungskarte.</summary>
    public string UpdateHeadline => HasUpdate ? "Neue Version verfügbar" : "Aktualisierung";

    /// <summary>Läuft gerade ein Download?</summary>
    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>Der Fortschritt des Downloads in Prozent.</summary>
    [ObservableProperty]
    private double _downloadPercent;

    /// <summary>
    /// Konnte die geladene Datei gegen die veröffentlichte Prüfsumme geprüft werden?
    /// </summary>
    /// <remarks>
    /// Steht sichtbar in der Oberfläche, weil es den Unterschied macht: Ohne Prüfsumme fällt
    /// ein unterwegs verfälschter Download nicht auf.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChecksumWarning))]
    private bool _checksumVerified;

    /// <summary>Wurde ohne Gegenprüfung geladen?</summary>
    public bool HasChecksumWarning => _downloadedSetup is not null && !ChecksumVerified;

    /// <summary>Steht ein geprüftes Setup bereit?</summary>
    [ObservableProperty]
    private bool _isReadyToInstall;

    /// <summary>Sucht von Hand nach einer neuen Fassung.</summary>
    /// <remarks>
    /// Von selbst geschieht das täglich. Diese Schaltfläche gibt es trotzdem: Wer eine
    /// Fehlerbehebung erwartet, soll nicht bis morgen warten müssen.
    /// </remarks>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        UpdateText = "Es wird nachgesehen …";
        _ = await _updates.CheckAsync().ConfigureAwait(true);
        ApplyUpdate();
    }

    /// <summary>
    /// Lädt die neue Fassung und prüft sie gegen die veröffentlichte Prüfsumme.
    /// </summary>
    /// <remarks>
    /// Installiert wird hier noch nichts. Herunterladen und Ausführen sind ausdrücklich zwei
    /// Schritte: Zwischen ihnen steht die Prüfsumme, und dazwischen darf der Benutzer es sich
    /// noch anders überlegen.
    /// </remarks>
    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (_updates.Available is not { } update)
        {
            return;
        }

        IsDownloading = true;
        DownloadPercent = 0;
        IsReadyToInstall = false;
        _downloadedSetup = null;

        try
        {
            Progress<double> progress = new(value => DownloadPercent = value * 100d);

            // Die Antwort sagt BEIDES: wo die Datei liegt und ob wirklich verglichen wurde.
            // Vorher stand hier `update.ChecksumUrl is not null` - das beantwortet nur, ob es
            // eine Pruefsummen-ADRESSE gibt, nicht ob die Pruefsumme geholt und gerechnet
            // wurde. Liess sie sich nicht holen, meldete die Oberflaeche trotzdem
            // "stimmt ueberein".
            DownloadedUpdate result = await _updates
                .DownloadAsync(update, progress)
                .ConfigureAwait(true);

            _downloadedSetup = result.Path;
            ChecksumVerified = result.Verified;
            IsReadyToInstall = true;

            UpdateText = ChecksumVerified
                ? string.Create(CultureInfo.CurrentCulture,
                    $"Version {update.Version} ist geladen und stimmt mit der veröffentlichten "
                    + $"Prüfsumme überein.")
                : string.Create(CultureInfo.CurrentCulture,
                    $"Version {update.Version} ist geladen, aber NICHT gegengeprüft: Die "
                    + $"veröffentlichte Prüfsumme fehlt oder liess sich nicht holen.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _downloadedSetup = null;
            UpdateText = Redaction.Scrub(ex.Message);
        }
        finally
        {
            IsDownloading = false;
            OnPropertyChanged(nameof(HasChecksumWarning));
        }
    }

    /// <summary>
    /// Startet das geprüfte Setup und beendet die Anwendung geordnet.
    /// </summary>
    /// <remarks>
    /// Die Reihenfolge ist nicht beliebig: Erst das Setup starten, dann beenden. Andersherum
    /// wäre dieses Stück beim Start des Setups bereits abgeräumt. Das Beenden selbst schliesst
    /// die laufenden Fernwartungen ab und reiht sie ein — deshalb wird beendet und nicht
    /// abgeschossen.
    /// </remarks>
    [RelayCommand]
    private void InstallUpdate()
    {
        if (_downloadedSetup is null)
        {
            return;
        }

        try
        {
            UpdateService.StartInstaller(_downloadedSetup);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateText = "Das Setup liess sich nicht starten: " + Redaction.Scrub(ex.Message);
            return;
        }

        ShutdownRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Die Einstellungen zur Sprachmodell-Unterstützung sollen geöffnet werden.</summary>
    public event EventHandler? AiSettingsRequested;

    /// <summary>Der Stand der Sprachmodell-Unterstützung, für die Anzeige.</summary>
    public string AiStateText
    {
        get
        {
            if (Host.Config?.Ai is not { } ai)
            {
                return "Nicht eingerichtet.";
            }

            if (!ai.HasConsent)
            {
                return "Abgeschaltet — es liegt keine Einwilligung vor, es wird nichts übermittelt.";
            }

            return ai.IsUsable
                ? $"Eingeschaltet: {ai.Provider}, Modell {ai.Model}"
                  + (ai.RedactIdentifiers ? ", Namen werden geschwärzt." : ", ohne Schwärzung.")
                : "Einwilligung liegt vor, die Unterstützung ist aber abgeschaltet.";
        }
    }

    /// <summary>Öffnet die Einstellungen zur Sprachmodell-Unterstützung.</summary>
    [RelayCommand]
    private void OpenAiSettings() => AiSettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Öffnet den Einrichtungsassistenten.</summary>
    /// <remarks>
    /// Das Zahnrad. Es steht auch dann bereit, wenn schon alles eingerichtet ist — die Adresse
    /// einer Instanz ändert sich, und dafür soll niemand eine Datei von Hand bearbeiten müssen.
    /// Der Assistent liest die vorhandenen Werte als Vorbelegung.
    /// </remarks>
    [RelayCommand]
    private void OpenSetup() => SetupRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Fragt TANSS, ob gerade alles zusammen trägt.</summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (!IsConfigured)
        {
            Melde("Ohne Einrichtung gibt es nichts zu prüfen. Das Zahnrad oben öffnet den "
                  + "Assistenten.");
            return;
        }

        IsBusy = true;
        try
        {
            AppStatus status = await Host.CheckConnectionAsync().ConfigureAwait(true);

            if (status.State == RuntimeState.Working)
            {
                MeldeErfolg("Verbindung erfolgreich.");
            }
            else
            {
                Melde(status.Reason);
            }

            await LoadEmployeeAsync().ConfigureAwait(true);
            BuildChecks(status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Melde(Redaction.Scrub(ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Erneuert das Arbeitstoken.
    /// </summary>
    /// <remarks>
    /// Das neue Token wird gegengeprüft, bevor es das bisherige ablöst. Misslingt die Probe,
    /// bleibt alles, wie es war — ein halb gewechseltes Token wäre schlimmer als ein altes.
    /// </remarks>
    [RelayCommand]
    private async Task RotateAsync()
    {
        if (!IsConfigured)
        {
            Melde("Ohne Einrichtung gibt es kein Token zu erneuern.");
            return;
        }

        IsBusy = true;
        try
        {
            TokenRotationResult result = await Host.TokenRotation.RotateNowAsync().ConfigureAwait(true);

            // Das Erneuern meldet seinen Grund in beiden Faellen; gruen wird er nur, wenn es
            // wirklich geklappt hat.
            if (result.Rotated)
            {
                MeldeErfolg(result.Reason);
            }
            else
            {
                Melde(result.Reason);
            }
            ApplyToken(Host.TokenRotation.RefreshToken());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Melde(Redaction.Scrub(ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <inheritdoc />
    protected override void OnStatusUpdated(AppStatus status)
    {
        ReadConfig();
        OnPropertyChanged(nameof(AiStateText));

        // Neu LESEN und nicht das Zwischengespeicherte nehmen: Nach einer frischen Einrichtung
        // liegt ein anderes Token im Profil, aber der Tokendienst laeuft nur einmal taeglich.
        // Ohne dieses Neulesen zeigte die Seite bis zu 24 Stunden lang die Werte des alten.
        ApplyToken(Host.TokenRotation.RefreshToken());
        _ = InitializeAsync();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Host.TokenRotation.TokenChanged -= OnTokenChanged;
            _updates.CheckCompleted -= OnUpdateCheckCompleted;
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Der Zeitgeber hat geprüft; die Anzeige nachziehen.
    /// </summary>
    /// <remarks>
    /// Das Ereignis kommt von einem Hintergrundstrang — anders als bei den Diensten der
    /// Laufzeit gibt es hier keine Umlenkung an der Quelle, weil dieser Dienst ohne die
    /// Laufzeit auskommt. Umgelenkt wird deshalb hier.
    /// </remarks>
    private void OnUpdateCheckCompleted(object? sender, UpdateCheckState state) =>
        _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(ApplyUpdate);

    private void ApplyUpdate()
    {
        HasUpdate = _updates.State == UpdateCheckState.UpdateAvailable
            && _updates.Available is not null;

        UpdateText = _updates.State switch
        {
            UpdateCheckState.UpdateAvailable when _updates.Available is { } update =>
                string.Create(CultureInfo.CurrentCulture,
                    $"Version {update.Version} steht bereit ({update.SizeText}). "
                    + $"Installiert ist {CurrentVersionText}."),
            // Der Grund wird mitgenommen, wenn es einen gibt: „nichts Neues“ und „es gibt dort
            // gar nichts abzuholen“ sind beides kein Fehler, bedeuten aber Verschiedenes.
            UpdateCheckState.UpToDate => _updates.Problem
                ?? string.Create(CultureInfo.CurrentCulture,
                    $"Version {CurrentVersionText} ist die neueste."),
            UpdateCheckState.Checking => "Es wird nachgesehen …",
            UpdateCheckState.Failed => _updates.Problem ?? "Die Prüfung ist fehlgeschlagen.",
            _ => "Noch nicht nach Aktualisierungen gesucht.",
        };
    }

    /// <summary>
    /// Baut die Befundliste aus dem, was tatsächlich gemessen wurde.
    /// </summary>
    /// <remarks>
    /// <para>Jede Zeile nennt ihre Quelle. Wo keine Messung vorliegt, steht ausdrücklich „nicht
    /// geprüft“ — das ist eine eigene Stufe und nicht dasselbe wie „in Ordnung“. Genau diese
    /// Unterscheidung fehlte der Vorgängerfassung, und sie ist der Grund, aus dem ein kaputtes
    /// Token dort als „trägt“ erschien.</para>
    /// <para>Das Prägerecht steht bewusst NICHT darin: Es liesse sich nur über einen
    /// Prägeversuch feststellen, und der erzeugt in TANSS ein echtes, nicht widerrufbares
    /// Token. Eine Prüfanzeige, die bei jedem Öffnen Token ausstellt, wäre teurer als ihr
    /// Erkenntniswert.</para>
    /// </remarks>
    /// <param name="status">Der Betriebszustand nach dem Verbindungstest.</param>
    private void BuildChecks(AppStatus status)
    {
        Checks.Clear();

        bool reachable = status.State == RuntimeState.Working;

        Checks.Add(new CheckRow("TANSS erreichbar",
            reachable ? CheckLevel.Ok : CheckLevel.Fail,
            reachable
                ? $"GET /api/tanss.x/v1/technicians hat geantwortet ({BaseUrl})."
                : status.Reason));

        // Die Zahl kommt aus der Liste, die darunter steht - nicht aus einer Annahme.
        Checks.Add(new CheckRow("Fernwartungsanbindungen",
            Systems.Count > 0 ? CheckLevel.Ok : CheckLevel.Warn,
            Systems.Count > 0
                ? Texts.Count(Systems.Count, "Anbindung gelesen", "Anbindungen gelesen") + "."
                : "Keine gelesen. Entweder ist keine eingerichtet, oder die Abfrage schlug fehl "
                  + "— das Modul Fernwartung könnte auf dieser Instanz fehlen."));

        Checks.Add(new CheckRow("Arbeitstoken",
            HasTokenProblem ? CheckLevel.Fail : reachable ? CheckLevel.Ok : CheckLevel.Unknown,
            HasTokenProblem
                ? TokenProblem!
                : reachable
                    ? $"Der Aufruf wurde angenommen; das Token trägt. Ablauf: {TokenExpiry}."
                    : "Nicht geprüft — ohne beantworteten Aufruf ist darüber nichts bekannt."));

        Checks.Add(new CheckRow("Zertifikatsprüfung",
            VerifyTls ? CheckLevel.Ok : CheckLevel.Fail,
            VerifyTls
                ? "Eingeschaltet (tanss.verify_tls = true)."
                : "ABGESCHALTET. Das Arbeitstoken geht bei jedem Aufruf ungeprüft über diese "
                  + "Leitung, und TANSS kann ein ausgestelltes Token nicht widerrufen."));

        OnPropertyChanged(nameof(HasChecks));
    }

    private void ReadConfig()
    {
        if (Host.Config is not { } config)
        {
            BaseUrl = "—";
            EmployeeId = 0;
            EmployeeName = "—";
            return;
        }

        BaseUrl = config.Tanss.BaseUrl;
        EmployeeId = config.Tanss.EmployeeId;
        VerifyTls = config.Tanss.VerifyTls;
    }

    private void OnTokenChanged(object? sender, TokenSnapshot token) => ApplyToken(token);

    private void ApplyToken(TokenSnapshot token)
    {
        TokenProblem = token.Problem;
        CanRotate = token.CanRotate ?? false;
        HasExpiry = token.ExpiresAt is not null;

        // Ein Token ohne Ablaufanspruch liefert unendlich viele Resttage. Der Wurf auf int
        // saettigt das auf 2.147.483.647 - die Anzeige stuende dann auf „2147483647 Tage“, was
        // wie ein Rechenfehler aussieht und nicht wie „laeuft nicht ab“.
        TokenDaysLeft = double.IsFinite(token.DaysRemaining)
            ? (int)Math.Floor(token.DaysRemaining)
            : 0;

        TokenExpiry = token.ExpiresAt is { } expiry ? Texts.Day(expiry) : "kein Ablauf";
    }

    private async Task InitializeAsync()
    {
        if (!await _systems.RefreshAsync(Host.Composition).ConfigureAwait(true))
        {
            return;
        }

        Systems.Clear();
        foreach (SystemRow row in _systems.Systems)
        {
            Systems.Add(row);
        }

        await LoadEmployeeAsync().ConfigureAwait(true);
    }

    /// <summary>Löst die Mitarbeiterkennung in einen Namen auf.</summary>
    /// <remarks>
    /// Beiwerk wie die Anbindungen: Misslingt es, steht die Kennung da. Eine Kennung ohne Namen
    /// ist unschön, aber brauchbar — eine Fehlermeldung an dieser Stelle wäre es nicht.
    /// </remarks>
    private async Task LoadEmployeeAsync()
    {
        if (Host.Composition is not { } composition || EmployeeId <= 0)
        {
            return;
        }

        try
        {
            IReadOnlyList<Technician> technicians =
                await composition.Technicians.ListAsync().ConfigureAwait(true);

            Technician? mine = technicians.FirstOrDefault(t => t.Id == EmployeeId);
            if (mine is not null)
            {
                EmployeeName = string.IsNullOrWhiteSpace(mine.Name)
                    ? string.Create(CultureInfo.CurrentCulture, $"{mine.LastName}, {mine.FirstName}")
                    : mine.Name;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Siehe Kommentar: der Name ist Beiwerk.
            System.Diagnostics.Debug.WriteLine("Technikerliste nicht lesbar: " + ex.Message);
        }
    }
}
