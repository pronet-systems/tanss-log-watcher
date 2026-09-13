using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Storage.History;
using TanssLogWatcher.Storage.Recordings;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Der Verlauf: was gelaufen ist, ob es in TANSS steht und was von der Aufzeichnung übrig ist.
/// </summary>
/// <remarks>
/// <para><b>Diese Seite ist die einzige mit Gedächtnis.</b> „Sitzungen“ zeigt, was jetzt läuft;
/// „Warteschlange“ zeigt, was noch hinausgeht, und räumt ihre erledigten Zeilen nach einer
/// Frist weg. Was danach bleibt, steht hier — und nur hier lässt sich ein halbes Jahr später
/// noch beantworten, ob eine Fernwartung berechnet wurde.</para>
///
/// <para><b>Es wird nichts stillschweigend abgeschnitten.</b> Geladen wird ein Ausschnitt, und
/// über der Tabelle steht, wie gross er im Verhältnis zum Ganzen ist. Eine Liste, die bei
/// hundert Zeilen aufhört und so tut, als wären es alle, lässt den Techniker nach einer Sitzung
/// suchen, die er nie finden wird. Aus demselben Grund sagt jede Zeile, welchen Abschnitt der
/// Abspielknopf öffnet, wenn es mehr als einen gibt.</para>
///
/// <para><b>Gelesen wird auf einem Nebenstrang.</b> Der Aufzeichnungsordner liegt oft auf einem
/// Netzlaufwerk; ein <c>File.Exists</c> auf eine tote Freigabe blockiert sekundenlang. Auf dem
/// Strang der Oberfläche wäre das ein eingefrorenes Fenster. Deshalb wird zuerst <b>einmal</b>
/// geprüft, ob die Wurzel überhaupt erreichbar ist — ist sie es nicht, unterbleibt jede weitere
/// Frage an die Platte, und die Zeilen sagen ausdrücklich „unbekannt“ statt „gelöscht“.</para>
///
/// <para><b>Diese Seite schreibt nichts.</b> Weder trägt sie <c>missing</c> ein, noch löscht
/// sie, noch legt sie eine Verlaufszeile an. Eine Anzeige, die Grabsteine setzt, setzt sie
/// irgendwann für ein Laufwerk, das nur schlief.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class HistoryViewModel : RuntimeViewModel
{
    /// <summary>Wie viele Zeilen ein Ausschnitt umfasst.</summary>
    /// <remarks>
    /// Hundert ist die Zahl, die einen Arbeitstag samt der Tage davor abdeckt, ohne dass das
    /// erste Bild auf sich warten lässt. Wer weiter zurück muss, lädt nach; wer alles will,
    /// bekommt es auch — nur eben auf Ansage.
    /// </remarks>
    private const int PageSize = 100;

    /// <summary>Die längste Vorwarnung, bevor eine Löschfrist greift.</summary>
    private static readonly TimeSpan LongestWarning = TimeSpan.FromDays(7);

    private readonly SystemLookup _systems;
    private int _wanted = PageSize;
    private bool _loadedConfigured;

    /// <summary>Baut die Seite und holt den ersten Ausschnitt.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="systems">Der gemeinsame Nachschlag für Typnamen und Farben.</param>
    public HistoryViewModel(AppHost host, SystemLookup systems) : base(host)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(systems);

        _systems = systems;
        _loadedConfigured = host.Status.IsConfigured;

        // Eine Sitzung, die gerade zu Ende ging, gehoert in den Verlauf - aber nicht, indem
        // die Liste unter den Haenden des Lesenden neu aufgebaut wird. Gemeldet wird sie,
        // nachgeladen wird auf Klick.
        host.Sessions.SessionEnded += OnSessionEnded;

        _ = LoadAsync(PageSize);
        _ = InitializeAsync();
    }

    /// <summary>Die Sitzungen des Ausschnitts, jüngste zuerst.</summary>
    public ObservableCollection<HistoryRow> Entries { get; } = [];

    /// <summary>Wie viele Sitzungen der Verlauf insgesamt führt — unter demselben Filter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountsText))]
    [NotifyPropertyChangedFor(nameof(MoreText))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _total;

    /// <summary>Gibt es mehr, als der Ausschnitt zeigt?</summary>
    [ObservableProperty]
    private bool _hasMore;

    /// <summary>Wird gerade gelesen?</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Ist der Verlauf in der Konfiguration abgeschaltet?
    /// </summary>
    /// <remarks>
    /// Eine leere Tabelle sähe genauso aus wie ein Verlauf, in dem noch nichts steht. Das ist
    /// aber ein anderer Zustand, und die Seite sagt ihn, statt ihn zu verschweigen —
    /// <c>history.enabled: false</c> heisst, dass nichts festgehalten <i>wird</i>, nicht, dass
    /// nichts gelaufen ist.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isDisabled;

    /// <summary>Nur Sitzungen zeigen, zu denen eine Aufzeichnung gehört.</summary>
    /// <remarks>
    /// Der einzige Filter dieser Seite, und er beantwortet die einzige Frage, die das Blättern
    /// nicht schon beantwortet: „wo ist das Video von neulich“. Nach einem Zeitraum wird nicht
    /// gefiltert — die Liste ist nach Zeit sortiert, das Blättern <i>ist</i> der Zeitfilter.
    /// Eine Suche nach der Gegenstelle gibt es ebenfalls nicht: Sie fände nichts, sobald die
    /// Fensterbeschriftungen geschwärzt sind, und eine Suche, die manchmal nichts findet, ist
    /// schlimmer als keine.
    /// </remarks>
    [ObservableProperty]
    private bool _onlyWithRecording;

    /// <summary>Wie viele Sitzungen seit dem letzten Laden dazugekommen sind.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    [NotifyPropertyChangedFor(nameof(PendingText))]
    private int _pending;

    /// <summary>Was zuletzt schiefging; leer, solange nichts schiefging.</summary>
    /// <remarks>
    /// Nur Beanstandungen. Eine Rückmeldung für gelungene Handlungen gibt es hier nicht: Dass
    /// der Abspieler aufgeht, sieht man daran, dass er aufgeht.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    /// <summary>Gibt es eine Beanstandung?</summary>
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Die Beschriftung der Nachladeschaltfläche.</summary>
    /// <remarks>
    /// Mit der Zahl darin, und zwar mit der tatsächlichen: „Mehr laden“ verschweigt, ob danach
    /// zehn oder tausend Zeilen stehen. Sind es weniger als ein voller Ausschnitt, steht die
    /// kleinere Zahl da — dann ist auf denselben Klick hin auch wirklich alles zu sehen.
    /// </remarks>
    public string MoreText => string.Create(CultureInfo.CurrentCulture,
        $"Weitere {Math.Min(PageSize, Math.Max(Total - Entries.Count, 0))} laden");

    /// <summary>Sind seit dem Laden Sitzungen dazugekommen?</summary>
    public bool HasPending => Pending > 0;

    /// <summary>Führt der Verlauf überhaupt nichts?</summary>
    public bool IsEmpty => !IsDisabled && IsConfigured && Total == 0;

    /// <summary>Der Satz über der Tabelle: was gezeigt wird und wovon.</summary>
    public string CountsText
    {
        get
        {
            if (Total == 0)
            {
                return "Noch keine abgeschlossene Sitzung im Verlauf.";
            }

            return Entries.Count >= Total
                ? string.Create(CultureInfo.CurrentCulture,
                    $"Alle {Total} Sitzungen, jüngste zuerst.")
                : string.Create(CultureInfo.CurrentCulture,
                    $"Die {Entries.Count} jüngsten von {Total} Sitzungen. Die älteren sind da, "
                    + $"sie stehen nur noch nicht in der Liste.");
        }
    }

    /// <summary>Der Satz zur Meldung „es ist etwas dazugekommen“.</summary>
    public string PendingText => Pending == 1
        ? "Eine Sitzung wurde beendet, seit diese Liste geladen wurde."
        : string.Create(CultureInfo.CurrentCulture,
            $"{Pending} Sitzungen wurden beendet, seit diese Liste geladen wurde.");

    /// <summary>Liest den Ausschnitt neu.</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(_wanted);

    /// <summary>Holt den nächsten Ausschnitt dazu.</summary>
    [RelayCommand]
    private Task LoadMoreAsync() => LoadAsync(_wanted + PageSize);

    /// <summary>
    /// Öffnet die Aufzeichnung im Standardabspieler.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Riegel ist derselbe wie beim Löschen.</b> Geöffnet wird ausschliesslich,
    /// was unterhalb der eingestellten Wurzel liegt — ein <c>..</c> in einem Eintrag der
    /// Buchführung würde sonst zu einem Programmstart mit fremdem Pfad. Der Handgriff ist
    /// billig und die Alternative teuer.</para>
    /// <para>Vor dem Start wird noch einmal nachgesehen. Zwischen dem Laden der Liste und dem
    /// Klick kann der stündliche Aufräumlauf gelöscht haben, und eine Fehlermeldung des
    /// Abspielers sagt „Datei nicht gefunden“, wo hier „die Frist hat gegriffen“ steht.</para>
    /// </remarks>
    /// <param name="row">Die Zeile, deren Aufzeichnung gemeint ist.</param>
    [RelayCommand]
    private void Play(HistoryRow? row)
    {
        if (row is null || !row.CanPlay || row.PlayRelativePath.Length == 0)
        {
            return;
        }

        if (Host.Composition is not { } composition)
        {
            Fail("Ohne Einrichtung ist kein Aufzeichnungsordner bekannt.");
            return;
        }

        string root = RecordingPaths.Root(composition.Config.Recording);
        string full;

        try
        {
            full = Path.GetFullPath(Path.Combine(root, row.PlayRelativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                   or PathTooLongException or IOException)
        {
            Fail("Der Pfad dieser Aufzeichnung lässt sich nicht auflösen: "
                 + Redaction.Scrub(ex.Message));
            return;
        }

        if (!RecordingPaths.IsInside(root, full))
        {
            Fail("Diese Aufzeichnung liegt nach der Buchführung ausserhalb des eingestellten "
                 + "Ordners. Geöffnet wird sie deshalb nicht — weder hier noch vom Aufräumer.");
            return;
        }

        if (!File.Exists(full))
        {
            Fail("Die Aufzeichnung liegt nicht mehr an ihrem Platz. „Neu laden“ zeigt, was die "
                 + "Buchführung inzwischen dazu sagt.");
            return;
        }

        try
        {
            // UseShellExecute, weil der Abspieler des Benutzers gemeint ist und nicht einer,
            // den dieses Werkzeug aussucht.
            _ = Process.Start(new ProcessStartInfo { FileName = full, UseShellExecute = true });

            Message = string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Ein fehlender Abspieler kostet diesen Klick, nicht die Seite.
            Fail("Die Aufzeichnung liess sich nicht öffnen: " + Redaction.Scrub(ex.Message));
        }
    }

    /// <inheritdoc />
    protected override void OnStatusUpdated(AppStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        OnPropertyChanged(nameof(IsEmpty));

        // Ausdruecklich nur beim Wechsel der Einrichtung und nicht bei jeder Zustandsmeldung:
        // Ein Neuaufbau der Liste wirft den Bildlauf an den Anfang zurueck, und "TANSS
        // antwortet gerade nicht" aendert am Verlauf nichts.
        if (status.IsConfigured == _loadedConfigured)
        {
            return;
        }

        _loadedConfigured = status.IsConfigured;
        _ = LoadAsync(_wanted);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Host.Sessions.SessionEnded -= OnSessionEnded;
        }

        base.Dispose(disposing);
    }

    /// <summary>Der Filter wirkt sofort und beginnt wieder beim ersten Ausschnitt.</summary>
    /// <param name="value">Der neue Stand des Umschalters.</param>
    partial void OnOnlyWithRecordingChanged(bool value) => _ = LoadAsync(PageSize);

    private async Task InitializeAsync()
    {
        // Ohne die Namen der Anbindungen stuende in jeder Zeile "Typ 1003". Der Nachschlag
        // gehoert der Anwendung und holt hoechstens einmal.
        if (await _systems.RefreshAsync(Host.Composition).ConfigureAwait(true))
        {
            await LoadAsync(_wanted).ConfigureAwait(true);
        }
    }

    private void OnSessionEnded(object? sender, SessionClosed closed) => Pending++;

    private async Task LoadAsync(int count)
    {
        _wanted = count;

        if (Host.Composition is not { } composition)
        {
            Clear();
            IsDisabled = false;
            return;
        }

        if (!composition.Config.History.Enabled)
        {
            // Hausregel 2: Eine leere Liste, die nicht sagt, warum sie leer ist, behauptet,
            // es sei nichts gelaufen.
            Clear();
            IsDisabled = true;
            return;
        }

        IsDisabled = false;
        IsLoading = true;

        try
        {
            bool onlyRecorded = OnlyWithRecording;
            string root = RecordingPaths.Root(composition.Config.Recording);
            TimeSpan warn = WarnWindow(
                TimeSpan.FromDays(composition.Config.Recording.RetentionDays));
            DateTimeOffset now = Host.Clock.GetLocalNow();

            Slice slice = await Task.Run(
                () => Read(composition, root, count, onlyRecorded, warn, now))
                .ConfigureAwait(true);

            Entries.Clear();

            foreach (HistoryRow row in slice.Rows)
            {
                Entries.Add(row);
            }

            Total = slice.Total;
            HasMore = Entries.Count < slice.Total;
            Pending = 0;
            Message = string.Empty;

            // Beide haengen an der Zahl der geladenen Zeilen, und die ist keine beobachtbare
            // Eigenschaft: ObservableCollection meldet ihre Aenderungen der Liste, nicht dem
            // Seitenmodell.
            OnPropertyChanged(nameof(CountsText));
            OnPropertyChanged(nameof(MoreText));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Ein unlesbarer Verlauf kostet die Anzeige, nicht den Dienst.
            Fail("Der Verlauf liess sich nicht lesen: " + Redaction.Scrub(ex.Message));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Liest den Ausschnitt und sieht auf der Platte nach — beides auf dem Nebenstrang.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Verlauf kommt aus dem Zusammenbau</b> — <see cref="RuntimeComposition"/>
    /// führt ihn, träge und auf derselben Datenbank wie Warteschlange und Protokoll. Die
    /// Seite baut sich keinen zweiten: Jeder zweite Bauweg wäre der Ort, an dem beim nächsten
    /// Umbau <c>history.redact_destination</c> nicht mehr gegen
    /// <c>logging.redact_window_titles</c> aufgelöst wird. Der Zusammenbau nimmt dafür
    /// ausschliesslich <see cref="SessionHistoryStore.FromConfig"/>.</para>
    /// <para>Freigegeben wird hier nichts: Der Speicher gehört dem Zusammenbau und wird mit
    /// ihm geschlossen. Ein <c>using</c> an dieser Stelle schlösse den Verlauf, den die
    /// Dienste weiterschreiben.</para>
    /// <para>Die Wurzel wird <b>einmal</b> geprüft und danach keine tote Freigabe mehr
    /// befragt. Ein nicht verbundenes Netzlaufwerk kostet damit einen Aufruf und nicht
    /// hundert, und keine Zeile behauptet einen Verlust, den niemand festgestellt hat.</para>
    /// </remarks>
    private Slice Read(RuntimeComposition composition, string root, int count,
                       bool onlyRecorded, TimeSpan warn, DateTimeOffset now)
    {
        SessionHistoryPage page = composition.History.Recent(count, onlyRecorded);

        bool reachable = Reachable(root);

        List<HistoryRow> rows = new(page.Items.Count);

        foreach (SessionHistoryEntry entry in page.Items)
        {
            rows.Add(new HistoryRow(entry,
                                    _systems.NameFor(entry.RemoteSupportTypeId),
                                    _systems.ColorFor(entry.RemoteSupportTypeId),
                                    Look(root, reachable, entry), warn, now));
        }

        return new Slice(rows, page.Total);
    }

    /// <summary>Ist der Aufzeichnungsordner überhaupt erreichbar?</summary>
    /// <remarks>
    /// Einmal je Lesevorgang. Ein Fehlschlag heisst „nicht nachgesehen“ und nicht „nichts da“:
    /// Ein Laufwerk, das schläft, hat nichts gelöscht.
    /// </remarks>
    private static bool Reachable(string root)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sieht nach, welche Abschnitte dieser Sitzung wirklich liegen.
    /// </summary>
    /// <remarks>
    /// Gefragt wird nur nach den Abschnitten, die die Buchführung als liegend führt — für einen
    /// gelöschten Abschnitt gibt es nichts nachzusehen. Der Riegel
    /// <see cref="RecordingPaths.IsInside"/> steht schon hier und nicht erst am Abspielknopf:
    /// Ein Pfad ausserhalb der Wurzel wird gar nicht erst als „vorhanden“ gezählt.
    /// </remarks>
    private static RecordingSighting Look(string root, bool reachable, SessionHistoryEntry entry)
    {
        SessionRecordingSummary recording = entry.Recording;

        if (recording.KeptCount == 0)
        {
            return RecordingSighting.Nothing;
        }

        if (!reachable)
        {
            return RecordingSighting.NotLooked;
        }

        List<int> present = [];

        foreach (SessionRecordingSegment segment in recording.Segments)
        {
            if (segment.State != RecordingSegmentState.Kept)
            {
                continue;
            }

            try
            {
                string full = Path.GetFullPath(Path.Combine(root, segment.RelativePath));

                if (RecordingPaths.IsInside(root, full) && File.Exists(full))
                {
                    present.Add(segment.Number);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
            {
                // Ein Pfad, der sich nicht einmal pruefen laesst, ist keine Aussage ueber die
                // Datei - und ganz sicher kein Beleg dafuer, dass sie fort ist.
                return RecordingSighting.NotLooked;
            }
        }

        return new RecordingSighting { Looked = true, Present = present };
    }

    /// <summary>
    /// Ab welcher Restfrist gewarnt wird.
    /// </summary>
    /// <remarks>
    /// Eine Woche ist die Vorwarnung, mit der sich noch etwas anfangen lässt. Bei einer kurzen
    /// Aufbewahrung wäre sie sinnlos — stünde bei drei Tagen Frist jede Aufzeichnung von Anfang
    /// an in Warnfarbe, warnte die Farbe vor nichts mehr. Deshalb höchstens ein Viertel der
    /// eingestellten Frist, und mindestens ein Tag.
    /// </remarks>
    private static TimeSpan WarnWindow(TimeSpan retention)
    {
        TimeSpan quarter = retention / 4;

        if (quarter > LongestWarning)
        {
            return LongestWarning;
        }

        return quarter < TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : quarter;
    }

    private void Clear()
    {
        Entries.Clear();
        Total = 0;
        HasMore = false;
        OnPropertyChanged(nameof(CountsText));
        OnPropertyChanged(nameof(MoreText));
    }

    private void Fail(string message) => Message = message;

    /// <summary>Was ein Lesevorgang zurückbringt: die Zeilen und die Gesamtzahl.</summary>
    private sealed record Slice(List<HistoryRow> Rows, int Total);
}
