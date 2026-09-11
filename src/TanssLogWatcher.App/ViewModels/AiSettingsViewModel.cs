using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.App.Ai;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Storage;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Secrets;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Die Einstellungen zur Sprachmodell-Unterstützung samt Einwilligung.
/// </summary>
/// <remarks>
/// <para><b>Die Einwilligung ist hier kein Haken unter einem Absatz, sondern die Bedingung.</b>
/// Ohne sie lässt sich die Unterstützung nicht einschalten, und ohne sie sendet das Werkzeug
/// nichts — geprüft wird das nicht hier, sondern in <see cref="AiGateway"/>, also an der
/// Stelle, an der tatsächlich gesendet wird.</para>
///
/// <para><b>Festgehalten werden Zeitpunkt und Windows-Benutzer.</b> Nicht aus Misstrauen,
/// sondern weil eine Einwilligung, die sich nicht nachweisen lässt, im Streitfall keine ist.
/// Beides steht in <c>config.json</c> und ist damit im Klartext lesbar und sicherbar.</para>
///
/// <para><b>Der Widerruf löscht auch den Schlüssel.</b> Eine zurückgenommene Einwilligung, bei
/// der die Zugangsdaten liegen bleiben, ist ein Versprechen, das beim nächsten Einschalten
/// wieder bricht.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class AiSettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IConfigStore _store;
    private readonly DpapiSecretStore _keys = DpapiSecretStore.ForAi();

    /// <summary>Baut die Maske aus der laufenden Konfiguration.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="store">Der Konfigurationsort; ohne Angabe der vorgesehene.</param>
    public AiSettingsViewModel(AppHost host, IConfigStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
        _store = store ?? ConfigStore.Default();

        AiSection ai = host.Config?.Ai ?? new AiSection();

        _isOpenAi = string.Equals(ai.Provider, AiGateway.ProviderOpenAi,
                                  StringComparison.OrdinalIgnoreCase);
        _enabled = ai.Enabled;
        _redactIdentifiers = ai.RedactIdentifiers;
        _hasStoredKey = _keys.Exists();
        _consentAccepted = ai.HasConsent;

        ConsentGivenAt = ai.ConsentGivenAt;
        ConsentBy = ai.ConsentBy;

        if (!string.IsNullOrWhiteSpace(ai.Model))
        {
            Models.Add(new AiModel(ai.Model, ai.Model));
            _selectedModel = Models[0];
        }
    }

    /// <summary>Ist OpenAI gewählt? Sonst gilt Anthropic.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProviderName))]
    [NotifyPropertyChangedFor(nameof(ConsentText))]
    private bool _isOpenAi;

    /// <summary>Der Name des gewählten Anbieters.</summary>
    public string ProviderName => IsOpenAi ? "OpenAI" : "Anthropic (Claude)";

    /// <summary>Soll die Unterstützung angeboten werden?</summary>
    [ObservableProperty]
    private bool _enabled;

    /// <summary>Namen vor dem Senden unkenntlich machen?</summary>
    [ObservableProperty]
    private bool _redactIdentifiers = true;

    /// <summary>Liegt bereits ein Schlüssel im Profil?</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyHint))]
    private bool _hasStoredKey;

    /// <summary>Was zum Schlüssel zu sagen ist.</summary>
    public string KeyHint => HasStoredKey
        ? "Ein Schlüssel ist hinterlegt. Das Feld leer lassen, um ihn zu behalten."
        : "Es ist kein Schlüssel hinterlegt.";

    /// <summary>Die Modelle des Anbieters.</summary>
    public ObservableCollection<AiModel> Models { get; } = [];

    /// <summary>Das gewählte Modell.</summary>
    [ObservableProperty]
    private AiModel? _selectedModel;

    /// <summary>Läuft gerade ein Aufruf?</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Die Rückmeldung der letzten Handlung.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    /// <summary>Gibt es eine Rückmeldung?</summary>
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Wurde die Einwilligung im Fenster bestätigt?</summary>
    [ObservableProperty]
    private bool _consentAccepted;

    /// <summary>Wann die Einwilligung erteilt wurde; <c>null</c>, wenn nicht.</summary>
    public string? ConsentGivenAt { get; private set; }

    /// <summary>Wer sie erteilt hat.</summary>
    public string? ConsentBy { get; private set; }

    /// <summary>Liegt eine festgehaltene Einwilligung vor?</summary>
    public bool HasRecordedConsent => !string.IsNullOrWhiteSpace(ConsentGivenAt);

    /// <summary>Was die Einwilligung festhält; wird im Fenster angezeigt.</summary>
    public string ConsentRecord => HasRecordedConsent
        ? $"Erteilt am {ConsentGivenAt} durch {ConsentBy}."
        : "Bisher nicht erteilt.";

    /// <summary>
    /// Der Text, dem zugestimmt wird.
    /// </summary>
    /// <remarks>
    /// Er steht im Programm und nicht in einer Datei daneben: Was jemand bestätigt hat, muss
    /// sich der Programmfassung zuordnen lassen. Eine Textdatei liesse sich nachträglich ändern.
    /// </remarks>
    public string ConsentText =>
        $"Beim Prüfen oder Ausformulieren wird der Berichtstext an {ProviderName} übermittelt "
        + "und dort verarbeitet — in der Regel auf Servern in den Vereinigten Staaten.\n\n"
        + "Übermittelt wird der gesamte Inhalt des Berichtsfeldes. Ist die Schwärzung "
        + "eingeschaltet, werden Gegenstelle, Rechnername und Anmeldename vorher durch "
        + "Platzhalter ersetzt. Alles, was darüber hinaus im Feld steht — insbesondere frei "
        + "geschriebene Kunden- oder Personennamen — geht unverändert mit.\n\n"
        + "Damit liegt eine Übermittlung personenbezogener Daten an einen Auftragsverarbeiter "
        + "in einem Drittland vor. Erforderlich sind dafür eine Rechtsgrundlage, ein "
        + "Auftragsverarbeitungsvertrag mit dem Anbieter und regelmäßig die Beteiligung von "
        + "Datenschutzbeauftragtem und Mitbestimmung. Diese Bestätigung ersetzt das nicht — "
        + "sie hält nur fest, dass sie an diesem Arbeitsplatz erteilt wurde.\n\n"
        + "Ohne diese Bestätigung wird nichts übermittelt.";

    /// <summary>Lässt sich speichern?</summary>
    public bool CanSave => !IsBusy;

    /// <summary>
    /// Holt die Modelle beim Anbieter.
    /// </summary>
    /// <remarks>
    /// Der einzige Aufruf, der ohne Einwilligung hinausgeht — und er trägt keinen Inhalt, nur
    /// den Schlüssel. Ohne ihn liesse sich kein Modell wählen, und ohne Modell wäre die
    /// Einwilligung gegenstandslos.
    /// </remarks>
    /// <param name="key">Der eingetippte Schlüssel; leer heisst „den hinterlegten nehmen“.</param>
    [RelayCommand]
    private async Task LoadModelsAsync(string? key)
    {
        string? effective = string.IsNullOrWhiteSpace(key) ? _keys.Read() : key.Trim();

        if (string.IsNullOrWhiteSpace(effective))
        {
            Message = "Ohne Schlüssel lässt sich die Modellliste nicht holen.";
            return;
        }

        IsBusy = true;
        Message = "Die Modellliste wird geholt …";

        try
        {
            using IAiAssistant assistant = AiGateway.Create(
                IsOpenAi ? AiGateway.ProviderOpenAi : AiGateway.ProviderAnthropic, effective);

            IReadOnlyList<AiModel> models = await assistant.ListModelsAsync()
                .ConfigureAwait(true);

            string? previous = SelectedModel?.Id;

            Models.Clear();
            foreach (AiModel model in models)
            {
                Models.Add(model);
            }

            SelectedModel = Models.FirstOrDefault(m => m.Id == previous) ?? Models.FirstOrDefault();

            Message = models.Count == 0
                ? "Der Anbieter hat kein Modell gemeldet, das für Text taugt."
                : string.Create(CultureInfo.CurrentCulture,
                    $"{models.Count} Modell(e) geladen.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = "Die Modellliste liess sich nicht holen: " + Redaction.Scrub(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Übernimmt die Einstellungen und hält die Einwilligung fest.
    /// </summary>
    /// <param name="key">Der eingetippte Schlüssel; leer heisst „den hinterlegten behalten“.</param>
    [RelayCommand]
    private void Save(string? key)
    {
        if (_host.Config is not { } current)
        {
            Message = "Ohne Einrichtung gibt es keine Konfiguration, in die das gehörte.";
            return;
        }

        // Einschalten setzt drei Dinge voraus. Sie werden einzeln gemeldet, damit niemand
        // raten muss, welches davon fehlt.
        if (Enabled)
        {
            if (!ConsentAccepted)
            {
                Message = "Ohne die Bestätigung der Datenschutzhinweise lässt sich die "
                    + "Unterstützung nicht einschalten.";
                return;
            }

            if (SelectedModel is null)
            {
                Message = "Es ist kein Modell gewählt. Zuerst die Modellliste laden.";
                return;
            }

            if (string.IsNullOrWhiteSpace(key) && !_keys.Exists())
            {
                Message = "Es ist kein Schlüssel hinterlegt und keiner eingetragen.";
                return;
            }
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                _keys.Write(key.Trim());
                HasStoredKey = true;
            }
        }
        catch (TokenStoreException ex)
        {
            Message = "Der Schlüssel liess sich nicht ablegen: " + Redaction.Scrub(ex.Message);
            return;
        }

        // Die Einwilligung wird beim ERSTEN Einschalten festgehalten und danach nicht neu
        // datiert: Ein Datum, das sich bei jedem Speichern erneuert, belegt nichts.
        string? givenAt = ConsentGivenAt;
        string? givenBy = ConsentBy;

        if (ConsentAccepted && !HasRecordedConsent)
        {
            givenAt = _host.Clock.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss zzz",
                                                          CultureInfo.InvariantCulture);
            givenBy = Environment.UserName;
        }

        AppConfig updated = current with
        {
            Ai = new AiSection
            {
                Enabled = Enabled,
                Provider = IsOpenAi ? AiGateway.ProviderOpenAi : AiGateway.ProviderAnthropic,
                Model = SelectedModel?.Id ?? string.Empty,
                RedactIdentifiers = RedactIdentifiers,
                ConsentGivenAt = ConsentAccepted ? givenAt : null,
                ConsentBy = ConsentAccepted ? givenBy : null,
            },
        };

        try
        {
            _store.Save(updated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = "Das Speichern ist fehlgeschlagen: " + Redaction.Scrub(ex.Message);
            return;
        }

        ConsentGivenAt = ConsentAccepted ? givenAt : null;
        ConsentBy = ConsentAccepted ? givenBy : null;
        OnPropertyChanged(nameof(ConsentRecord));
        OnPropertyChanged(nameof(HasRecordedConsent));

        _ = _host.Reload();

        Message = Enabled
            ? "Gespeichert. Die Unterstützung steht im Abschlussdialog bereit."
            : "Gespeichert. Die Unterstützung ist abgeschaltet; es wird nichts übermittelt.";
    }

    /// <summary>
    /// Nimmt die Einwilligung zurück und löscht den Schlüssel.
    /// </summary>
    /// <remarks>
    /// Beides zusammen, und zwar sofort: Ein Widerruf, nach dem die Zugangsdaten liegen
    /// bleiben, ist keiner.
    /// </remarks>
    [RelayCommand]
    private void Revoke()
    {
        if (_host.Config is not { } current)
        {
            return;
        }

        // Der Rueckgabewert wird ausgewertet: Ein Widerruf, der nur behauptet wird, ist
        // schlimmer als gar keiner. Liegt der Schluessel noch, muss der Benutzer das erfahren.
        bool keyGone = _keys.Clear();

        try
        {
            _store.Save(current with { Ai = new AiSection() });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = "Das Zurücknehmen ist fehlgeschlagen: " + Redaction.Scrub(ex.Message);
            return;
        }

        Enabled = false;
        ConsentAccepted = false;
        HasStoredKey = false;
        ConsentGivenAt = null;
        ConsentBy = null;
        Models.Clear();
        SelectedModel = null;

        OnPropertyChanged(nameof(ConsentRecord));
        OnPropertyChanged(nameof(HasRecordedConsent));

        _ = _host.Reload();

        // Auch ohne gelöschten Schlüssel wird nichts mehr übermittelt — das Tor prüft zuerst die
        // Einwilligung, und die ist weg. Aber die Datei liegt dann noch da, und das gehört gesagt.
        Message = keyGone
            ? "Einwilligung zurückgenommen, Schlüssel gelöscht. Es wird nichts mehr übermittelt."
            : "Einwilligung zurückgenommen — es wird nichts mehr übermittelt. Die "
              + $"Schlüsseldatei liess sich aber nicht löschen; sie liegt weiterhin unter "
              + $"{_keys.Path} und ist von Hand zu entfernen.";
    }
}
