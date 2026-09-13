using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.Services;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Recordings;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Die Seite zur Bildschirmaufzeichnung — samt Kenntnisnahme und Löschfrist.
/// </summary>
/// <remarks>
/// <para><b>Sie ist strenger als jede andere Einstellungsseite, und das mit Absicht.</b> Eine
/// Aufzeichnung zeigt den Bildschirm des Kunden — mit dessen Daten und den Daten Dritter — und
/// den Bildschirm des Technikers, was Leistungs- und Verhaltenskontrolle im Sinne von
/// §87 Abs. 1 Nr. 6 BetrVG ist. Ein Schalter allein genügt dafür nicht: Es braucht eine
/// Kenntnisnahme, und die zählt nur, wenn die Rechtsgrundlage benannt ist.</para>
///
/// <para><b>Die Rechtsgrundlage ist nicht vorbelegt.</b> Eine vorausgefüllte Auswahl wäre eine
/// Behauptung des Werkzeugs über einen Sachverhalt, den nur der Betrieb kennt — etwa das
/// Bestehen einer Betriebsvereinbarung. Hier steht, was jemand ausgewählt und verantwortet
/// hat.</para>
///
/// <para><b>Die Löschfrist lässt sich verkürzen, aber nicht rückwirkend verlängern.</b> Wer
/// sie heruntersetzt, verkürzt auch die bestehenden Aufzeichnungen; wer sie heraufsetzt,
/// verlängert die bestehenden nicht. Ein gegebenes Versprechen wird nicht nachträglich
/// gedehnt, und die Seite sagt das, statt es zu verschweigen.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class RecordingViewModel : RuntimeViewModel
{
    private readonly IConfigStore _store;

    private bool _loading;

    /// <summary>Baut die Seite aus der Konfiguration.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="store">Der Konfigurationsort; ohne Angabe der vorgesehene.</param>
    public RecordingViewModel(AppHost host, IConfigStore? store = null) : base(host)
    {
        _store = store ?? ConfigStore.Default();

        foreach (string basis in LegalBases)
        {
            Bases.Add(basis);
        }

        Load();
        Refresh();
    }

    /// <summary>
    /// Die Rechtsgrundlagen zur Auswahl.
    /// </summary>
    /// <remarks>
    /// Eine Auswahl und kein Freitextfeld allein: Wer „ja“ in ein leeres Feld schreibt, hat
    /// keine Rechtsgrundlage benannt, sondern ein Feld gefüllt. Die Liste nennt die Fälle, die
    /// in einem IT-Dienstleistungsbetrieb tatsächlich vorkommen — und lässt trotzdem eigene
    /// Eingaben zu, weil kein Werkzeug alle Häuser kennt.
    /// </remarks>
    public static IReadOnlyList<string> LegalBases { get; } =
    [
        "Betriebsvereinbarung",
        "Einwilligung des Technikers (Art. 6 Abs. 1 lit. a DSGVO)",
        "Vertragliche Vereinbarung mit dem Kunden (Art. 6 Abs. 1 lit. b DSGVO)",
        "Berechtigtes Interesse (Art. 6 Abs. 1 lit. f DSGVO)",
    ];

    /// <summary>Die Rechtsgrundlagen zur Auswahl, für die Anzeige.</summary>
    public ObservableCollection<string> Bases { get; } = [];

    /// <summary>Soll aufgezeichnet werden?</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAcknowledgement))]
    private bool _enabled;

    /// <summary>Wohin die Aufzeichnungen gehen; leer heisst: der vorgesehene Ort im Profil.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveDirectory))]
    private string _directory = string.Empty;

    /// <summary>Nach wie vielen Tagen gelöscht wird.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RetentionText))]
    private int _retentionDays = RecordingSection.DefaultRetentionDays;

    /// <summary>Bilder je Sekunde.</summary>
    [ObservableProperty]
    private int _framesPerSecond = 4;

    /// <summary>Nach welcher Ruhe trotzdem ein Bild geschrieben wird.</summary>
    [ObservableProperty]
    private int _heartbeatSeconds = 2;

    /// <summary>Nach wie vielen Minuten eine neue Datei begonnen wird.</summary>
    [ObservableProperty]
    private int _segmentMinutes = 10;

    /// <summary>Unterhalb wie vieler freier Megabyte nicht mehr aufgezeichnet wird.</summary>
    [ObservableProperty]
    private int _minimumFreeMegabytes = 2048;

    /// <summary>Die gewählte Rechtsgrundlage.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAcknowledgement))]
    private string _legalBasis = string.Empty;

    /// <summary>Der Beleg: Betriebsvereinbarung, Beschluss, Vertragsnummer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAcknowledgement))]
    private string _legalReference = string.Empty;

    /// <summary>Ist die Kenntnisnahme erteilt?</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAcknowledgement))]
    [NotifyPropertyChangedFor(nameof(AcknowledgementText))]
    private bool _acknowledged;

    /// <summary>Wann und von wem die Kenntnisnahme erteilt wurde.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AcknowledgementText))]
    private string _acknowledgedAt = string.Empty;

    /// <summary>Der Windows-Benutzer, der die Kenntnisnahme erteilt hat.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AcknowledgementText))]
    private string _acknowledgedBy = string.Empty;

    /// <summary>Die Rückmeldung der letzten Handlung.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    [NotifyPropertyChangedFor(nameof(HasGoodMessage))]
    private string _message = string.Empty;

    /// <summary>Ist die Rückmeldung eine Beanstandung?</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGoodMessage))]
    private bool _isProblem;

    /// <summary>Sind Änderungen offen?</summary>
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>Was gerade gilt — der Satz des Dienstes.</summary>
    [ObservableProperty]
    private string _stateText = string.Empty;

    /// <summary>Was auf der Platte liegt, in einem Satz.</summary>
    [ObservableProperty]
    private string _storageText = string.Empty;

    /// <summary>Gibt es eine Rückmeldung?</summary>
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Gibt es eine Rückmeldung, die keine Beanstandung ist?</summary>
    /// <remarks>
    /// Zwei Hinweisleisten statt einer mit wechselnder Färbung: Eine Leiste, die ihre Schwere
    /// über einen Auslöser umschaltet, bringt den Eindruck mit, es sei dieselbe Meldung nur in
    /// anderer Farbe — und beim Umschalten blieb die alte Überschrift stehen.
    /// </remarks>
    public bool HasGoodMessage => HasMessage && !IsProblem;

    /// <summary>
    /// Fehlt zum Einschalten noch etwas?
    /// </summary>
    /// <remarks>
    /// Die Seite lässt sich in diesem Zustand speichern — die Aufzeichnung bleibt dann eben
    /// aus. Das ist richtig so: Eine Konfiguration ohne Kenntnisnahme ist vollkommen gültig,
    /// sie zeichnet nur nicht auf.
    /// </remarks>
    public bool NeedsAcknowledgement => Enabled && !IsAcknowledgementComplete;

    /// <summary>Der Ordner, der tatsächlich benutzt wird.</summary>
    public string EffectiveDirectory =>
        RecordingPaths.Root(new RecordingSection { Directory = Directory });

    /// <summary>Der Satz zur Löschfrist.</summary>
    public string RetentionText
    {
        get
        {
            string days = RetentionDays.ToString(CultureInfo.CurrentCulture);
            string until = DateTimeOffset.Now.AddDays(RetentionDays)
                .ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);

            return $"Jede Aufzeichnung wird {days} Tage nach ihrem Beginn gelöscht — spätestens "
                + $"am {until}, wenn heute eine entstünde. Eine kürzere Frist wirkt sofort auch "
                + "auf bestehende Aufzeichnungen; eine längere verlängert die bestehenden nicht.";
        }
    }

    /// <summary>Der Satz zur Kenntnisnahme.</summary>
    public string AcknowledgementText =>
        !Acknowledged || AcknowledgedAt.Length == 0
            ? "Noch nicht erteilt."
            : $"Erteilt am {Format(AcknowledgedAt)}"
              + (AcknowledgedBy.Length > 0 ? $" von {AcknowledgedBy}" : string.Empty)
              + ".";

    private bool IsAcknowledgementComplete =>
        Acknowledged
        && !string.IsNullOrWhiteSpace(LegalBasis)
        && !string.IsNullOrWhiteSpace(LegalReference);

    /// <inheritdoc />
    protected override void OnStatusUpdated(AppStatus status)
    {
        Load();
        Refresh();
    }

    /// <summary>Merkt sich, dass etwas geändert wurde.</summary>
    partial void OnEnabledChanged(bool value) => Touch();

    /// <summary>Merkt sich, dass etwas geändert wurde.</summary>
    partial void OnDirectoryChanged(string value) => Touch();

    /// <summary>Merkt sich, dass etwas geändert wurde.</summary>
    partial void OnRetentionDaysChanged(int value) => Touch();

    /// <summary>Merkt sich, dass etwas geändert wurde.</summary>
    partial void OnFramesPerSecondChanged(int value) => Touch();

    /// <summary>Merkt sich, dass etwas geändert wurde.</summary>
    partial void OnHeartbeatSecondsChanged(int value) => Touch();

    /// <summary>Merkt sich, dass etwas geändert wurde.</summary>
    partial void OnSegmentMinutesChanged(int value) => Touch();

    /// <summary>Merkt sich, dass etwas geändert wurde.</summary>
    partial void OnMinimumFreeMegabytesChanged(int value) => Touch();

    /// <summary>Merkt sich, dass etwas geändert wurde.</summary>
    partial void OnLegalBasisChanged(string value) => Touch();

    /// <summary>Merkt sich, dass etwas geändert wurde.</summary>
    partial void OnLegalReferenceChanged(string value) => Touch();

    /// <summary>
    /// Setzt Zeitpunkt und Person, sobald die Kenntnisnahme gesetzt wird — und räumt sie fort,
    /// wenn sie zurückgenommen wird.
    /// </summary>
    /// <remarks>
    /// Der Zeitpunkt wird hier festgehalten und nicht beim Speichern: Er soll der Augenblick
    /// sein, in dem jemand das Häkchen gesetzt hat, und nicht der, in dem er die Seite
    /// verliess.
    /// </remarks>
    partial void OnAcknowledgedChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        if (value)
        {
            AcknowledgedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            AcknowledgedBy = Environment.UserName;
        }
        else
        {
            AcknowledgedAt = string.Empty;
            AcknowledgedBy = string.Empty;
        }

        Touch();
    }

    /// <summary>Wählt den Ordner für Aufzeichnungen.</summary>
    [RelayCommand]
    private void Browse()
    {
        Microsoft.Win32.OpenFolderDialog dialog = new()
        {
            Title = "Ordner für Bildschirmaufzeichnungen",
            InitialDirectory = System.IO.Directory.Exists(EffectiveDirectory)
                ? EffectiveDirectory
                : string.Empty,
        };

        if (dialog.ShowDialog() == true)
        {
            Directory = dialog.FolderName;
        }
    }

    /// <summary>Öffnet den Ordner im Explorer.</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            _ = System.IO.Directory.CreateDirectory(EffectiveDirectory);
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = EffectiveDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail("Der Ordner liess sich nicht öffnen: " + Redaction.Scrub(ex.Message));
        }
    }

    /// <summary>
    /// Löscht sofort, was seine Frist überschritten hat.
    /// </summary>
    /// <remarks>
    /// Der Dienst tut das stündlich von selbst. Diese Schaltfläche ist für den Fall, dass
    /// jemand die Frist gerade heruntergesetzt hat und sehen will, dass es wirkt — eine
    /// Löschfrist, deren Wirkung man erst am nächsten Tag sieht, wird nicht geglaubt.
    /// </remarks>
    [RelayCommand]
    private void CleanUpNow()
    {
        if (Host.Composition is not { } composition)
        {
            Fail("Ohne Einrichtung gibt es keine Buchführung, in der etwas fällig sein könnte.");
            return;
        }

        try
        {
            CleanupResult result = new RecordingCleaner(
                composition.Recordings, RecordingPaths.Root(composition.Config.Recording))
                .Run(TimeSpan.FromDays(composition.Config.Recording.RetentionDays));

            IsProblem = result.Failed > 0;
            Message = result.Summary;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail("Das Aufräumen ist fehlgeschlagen: " + Redaction.Scrub(ex.Message));
        }

        Refresh();
    }

    /// <summary>Verwirft die Änderungen und liest die Konfiguration neu.</summary>
    [RelayCommand]
    private void Reset()
    {
        Load();
        IsDirty = false;
        Message = string.Empty;
    }

    /// <summary>Schreibt die Einstellungen und lädt die Laufzeit neu.</summary>
    [RelayCommand]
    private void Save()
    {
        if (Host.Config is not { } current)
        {
            Fail("Ohne Einrichtung gibt es nichts zu speichern. Das Zahnrad unter "
                 + "„Verbindung“ öffnet den Assistenten.");
            return;
        }

        if (Directory.Length > 0 && !Path.IsPathFullyQualified(Directory))
        {
            Fail($"„{Directory}“ ist kein vollständiger Pfad. Ein relativer Pfad zeigte je nach "
                 + "Startverzeichnis woandershin — und gelöscht wird ausschliesslich unterhalb "
                 + "dieses Ordners.");
            return;
        }

        RecordingSection section = new()
        {
            Enabled = Enabled,
            Directory = Directory.Trim(),
            RetentionDays = RetentionDays,
            FramesPerSecond = FramesPerSecond,
            HeartbeatSeconds = HeartbeatSeconds,
            SegmentMinutes = SegmentMinutes,
            MinimumFreeMegabytes = MinimumFreeMegabytes,
            AcknowledgedAt = Empty(AcknowledgedAt),
            AcknowledgedBy = Empty(AcknowledgedBy),
            LegalBasis = Empty(LegalBasis),
            LegalReference = Empty(LegalReference),
        };

        // Dieselbe Pruefung, die auch beim Laden ueber die Datei laeuft. Liesse diese Seite es
        // lockerer, entstuende eine Datei, die beim naechsten Start als ungueltig
        // zurueckkommt - gespeichert, aber unbrauchbar.
        AppConfig candidate = current with { Recording = section };

        if (ConfigValidator.Collect(candidate) is { Count: > 0 } problems)
        {
            Fail(string.Join(" ", problems));
            return;
        }

        try
        {
            _store.Save(candidate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail("Das Speichern ist fehlgeschlagen: " + Redaction.Scrub(ex.Message));
            return;
        }

        IsDirty = false;
        IsProblem = false;

        Message = Host.Reload()
            ? Describe(section)
            : "Gespeichert, aber das Neuladen ist fehlgeschlagen. Die Meldung steht unter "
              + "„Verbindung“.";

        Refresh();
    }

    /// <summary>Der Satz nach dem Speichern — er sagt, was ab jetzt gilt.</summary>
    private static string Describe(RecordingSection section) =>
        RecordingGate.WhyNotRecording(section) is { } why
            ? "Gespeichert. Es wird nicht aufgezeichnet: " + why
            : string.Create(CultureInfo.CurrentCulture,
                $"Gespeichert. Ab sofort wird aufgezeichnet; jede Aufzeichnung wird nach "
                + $"{section.RetentionDays} Tagen gelöscht.");

    private static string? Empty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Formt einen gespeicherten Zeitpunkt für die Anzeige.</summary>
    private static string Format(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out DateTimeOffset when)
            ? when.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture)
            : iso;

    private void Fail(string message)
    {
        IsProblem = true;
        Message = message;
    }

    private void Touch()
    {
        if (!_loading)
        {
            IsDirty = true;
        }
    }

    /// <summary>Übernimmt den Stand aus der Konfiguration.</summary>
    private void Load()
    {
        _loading = true;

        try
        {
            RecordingSection section = Host.Config?.Recording ?? new RecordingSection();

            Enabled = section.Enabled;
            Directory = section.Directory;
            RetentionDays = section.RetentionDays;
            FramesPerSecond = section.FramesPerSecond;
            HeartbeatSeconds = section.HeartbeatSeconds;
            SegmentMinutes = section.SegmentMinutes;
            MinimumFreeMegabytes = section.MinimumFreeMegabytes;
            LegalBasis = section.LegalBasis ?? string.Empty;
            LegalReference = section.LegalReference ?? string.Empty;
            AcknowledgedAt = section.AcknowledgedAt ?? string.Empty;
            AcknowledgedBy = section.AcknowledgedBy ?? string.Empty;
            Acknowledged = section.HasAcknowledgement;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Holt, was der Dienst und die Buchführung gerade sagen.</summary>
    private void Refresh()
    {
        RecordingSection section = Host.Config?.Recording ?? new RecordingSection();

        StateText = RecordingGate.WhyNotRecording(section) ?? Running();

        string Running()
        {
            // Der Versionsriegel steht auch hier: Den Dienst gibt es vor Build 19041 nicht.
            // Dass dann gar nicht aufgezeichnet wird, hat die Torwache bereits gesagt.
            int active = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                         && Host.Recordings is { } service
                ? service.ActiveRecordings
                : 0;

            return active == 0
                ? "Eingeschaltet. Die nächste Sitzung wird aufgezeichnet."
                : $"Es werden gerade {active.ToString(CultureInfo.CurrentCulture)} Sitzung(en) "
                  + "aufgezeichnet.";
        }

        StorageText = DescribeStorage(section);
    }

    /// <summary>Was auf der Platte liegt — aus der Buchführung, nicht aus dem Ordner.</summary>
    /// <remarks>
    /// Aus der Buchführung, weil sie auch das kennt, was bereits gelöscht wurde. Ein
    /// Verzeichnisdurchlauf fände zusätzlich alles, was jemand von Hand hineinkopiert hat —
    /// und das gehört uns nicht.
    /// </remarks>
    private string DescribeStorage(RecordingSection section)
    {
        if (Host.Composition is not { } composition)
        {
            return string.Empty;
        }

        try
        {
            int kept = composition.Recordings.Count("kept")
                       + composition.Recordings.Count("recording");
            int purged = composition.Recordings.Count("purged");

            long free = RecordingGate.FreeMegabytes(RecordingPaths.Root(section));

            return string.Create(CultureInfo.CurrentCulture,
                $"{kept} Aufzeichnung(en) liegen im Ordner, {purged} wurden nach Ablauf der "
                + $"Frist gelöscht. Frei auf dem Datenträger: {free / 1024:N0} GB.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "Die Buchführung liess sich nicht lesen: " + Redaction.Scrub(ex.Message);
        }
    }
}
