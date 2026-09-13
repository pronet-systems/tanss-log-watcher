using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Woran die Firmenauswahl gerade ist.
/// </summary>
/// <remarks>
/// <para><b>Neun Zustände und kein einziger, der zwei Dinge zusammenwirft.</b> Der ganze Grund
/// für diese Aufzählung ist der Unterschied zwischen <see cref="NoMatch"/> und
/// <see cref="Undetermined"/>: TANSS antwortet auf eine Suche mit zu vielen Treffern mit einer
/// <b>leeren</b> Liste — genau wie auf eine Suche ohne Treffer. Wer beides zu „keine Firma
/// gefunden“ verschmilzt, sagt in einem der beiden Fälle die Unwahrheit, und zwar unbemerkt.</para>
/// <para>Die Aufzählung dient der <b>Darstellung</b> — welche Farbe, welches Zeichen, welche
/// Schaltfläche. Der <b>Text</b> kommt aus <see cref="CompanySearchResult.Explanation"/> und
/// wird nicht aus dem Zustand neu erfunden.</para>
/// </remarks>
public enum CompanyPickerState
{
    /// <summary>Das Feld ist leer. Es wurde nichts gesucht.</summary>
    Idle,

    /// <summary>Zu wenige Zeichen. <b>TANSS wurde gar nicht gefragt</b>, und die Auswahl sagt es.</summary>
    TooShort,

    /// <summary>Die Eingabe steht noch nicht still; die Suche wartet die Entprellung ab.</summary>
    Pending,

    /// <summary>Die Anfrage läuft.</summary>
    Searching,

    /// <summary>Treffer, und zwar vollzählig.</summary>
    Found,

    /// <summary>Nachweislich keine Firma — über die Gegenprobe der Suche belegt, nicht geraten.</summary>
    NoMatch,

    /// <summary>
    /// <b>Nicht ermittelt.</b> TANSS hat nichts geliefert, und ob es nichts gibt oder zu viele,
    /// ließ sich nicht klären.
    /// </summary>
    Undetermined,

    /// <summary>Die Anfrage ist gescheitert — Netz, Anmeldung, TANSS. Gesperrt wird nichts.</summary>
    Failed,

    /// <summary>Es gibt keine Verbindung zu TANSS; die Auswahl kann nicht arbeiten.</summary>
    Unavailable,
}

/// <summary>
/// Die Firmenauswahl: tippen, warten, wählen — und an keiner Stelle etwas behaupten.
/// </summary>
/// <remarks>
/// <para><b>Der Fall, um den es hier wirklich geht.</b> <c>maxResults</c> der TANSS-Suche ist
/// nachgemessen eine <i>Schwelle</i> und keine Begrenzung: Übersteigt die Trefferzahl den Wert,
/// kommt eine leere Liste statt einer gekürzten. Eine leere Antwort heißt deshalb entweder „es
/// gibt nichts“ oder „es sind zu viele“. <see cref="ICompanyRepository"/> unterscheidet beides,
/// so weit es sich beweisen lässt, und legt das Ergebnis samt fertigem Satz in
/// <see cref="CompanySearchResult.Explanation"/>. Diese Seite zeigt diesen Satz <b>wörtlich</b>.
/// Würde sie sich aus <c>Companies.Count</c> einen eigenen Text bauen, wäre die Unwahrheit
/// „keine Firma gefunden“ sofort zurück — über womöglich 540 Firmen.</para>
///
/// <para><b>Entprellt, weil jede Anfrage TANSS kostet.</b> Eine Suche je Tastendruck schickt für
/// „Mustermann“ zehn Anfragen los, von denen neun schon überholt sind, wenn sie ankommen. Der
/// <see cref="DispatcherTimer"/> ist derselbe Griff, den Sitzungs- und Timerseite benutzen; jeder
/// weitere Tastendruck setzt ihn zurück. Erst wenn die Eingabe kurz stillsteht, wird gefragt.</para>
///
/// <para><b>Prüfbar ohne Oberfläche.</b> Der ganze Zustand steht hier und nicht im Fenster, und
/// <see cref="SearchNowAsync"/> ist derselbe Weg, den auch die Entprellung geht — ein Test muss
/// also weder eine Nachrichtenschleife drehen noch warten. Ohne das bliebe diese Zusage an der
/// Aufmerksamkeit dessen hängen, der sie liest.</para>
///
/// <para><b>Getippt ist nicht gewählt.</b> Die Oberfläche ist ein beschreibbares Auswahlfeld;
/// dort sind <i>Text</i> und <i>Auswahl</i> zwei verschiedene Dinge, und hier sind es zwei
/// verschiedene Eigenschaften: <see cref="Query"/> ist, was im Feld steht, <see cref="Selected"/>
/// ist, worauf gebucht wird. Ein halb getippter Name setzt <see cref="Selected"/> <b>nicht</b> —
/// gewählt wird allein über <see cref="Picked"/> (die Zeile der Aufklappliste) oder über
/// <see cref="SelectCommand"/>. Andernfalls bucht jemand auf eine Firma, die er nur angetippt
/// hat, und der Unterschied ist im Feld nicht zu sehen.</para>
///
/// <para><b>Warum der Text festgehalten wird.</b> Ein beschreibbares Auswahlfeld schreibt den
/// Text seiner Auswahl selbst ins Feld — fällt die Auswahl weg, weil die Zeilen getauscht
/// wurden, schreibt es einen <b>leeren</b> Text zurück. Der käme hier als „der Benutzer hat das
/// Feld geräumt“ an und nähme dem Techniker mitten in der Suche seinen Begriff. Ob jede WPF-
/// Fassung das tut, ist nicht gemessen; <see cref="Pinned"/> hält den Begriff deshalb fest,
/// statt sich darauf zu verlassen.</para>
///
/// <para><b>Hausregel 5.</b> Ein Netzfehler kostet die Suche, nicht die Auswahl: Das Feld bleibt
/// offen, die vorherige Wahl bleibt stehen, und was gescheitert ist, steht im Text. Gesperrt
/// wird nichts — auch nicht bei <see cref="CompanyPickerState.Undetermined"/>.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class CompanyPickerViewModel : ObservableObject, IDisposable
{
    /// <summary>Wie lange die Eingabe stillstehen muss, bevor gefragt wird.</summary>
    /// <remarks>
    /// 350 ms ist die Spanne, in der ein flüssiger Tipper keine Pause macht, ein Suchender aber
    /// schon aufblickt. Kürzer heißt: Anfragen für halbe Wörter. Länger heißt: Die Liste hinkt
    /// spürbar hinterher.
    /// </remarks>
    public static TimeSpan DefaultDebounce { get; } = TimeSpan.FromMilliseconds(350);

    private const string IdleText =
        "Firmennamen eingeben — gesucht wird ab drei Zeichen. Der Begriff darf mitten im Namen "
        + "stehen: „roN“ findet „ProNet“.";

    private const string PendingText = "Die Suche startet, sobald die Eingabe kurz stillsteht.";

    private const string SearchingText = "TANSS wird gefragt …";

    private const string UnavailableText =
        "Ohne eingerichtete Verbindung zu TANSS kann keine Firma gesucht werden. Ob es die Firma "
        + "gibt, ist damit nicht ermittelt.";

    private readonly ICompanyRepository? _companies;
    private readonly DispatcherTimer _debounce;

    private CancellationTokenSource? _running;
    private bool _disposed;

    /// <summary>Wie viele Firmen die letzte beantwortete Suche insgesamt gefunden hat.</summary>
    /// <remarks>
    /// Nur dafür da, „alles da“ von „gekürzt“ zu unterscheiden: Steht die Liste vollzählig in
    /// der Aufklappliste, hat die Meldezeile nichts zu sagen und bleibt weg.
    /// </remarks>
    private int _totalFound;

    /// <summary>Wird der Text im Feld gerade festgehalten?</summary>
    /// <remarks>Siehe <see cref="Pinned"/>. Solange das gilt, ist eine Textänderung kein Tippen.</remarks>
    private bool _pinned;

    /// <summary>Baut die Auswahl mit der vorgesehenen Entprellung.</summary>
    /// <param name="companies">
    /// Die Firmensuche, oder <see langword="null"/>, wenn nichts eingerichtet ist. Ohne sie wird
    /// nicht gesucht und ausdrücklich gesagt, warum — statt eine leere Liste zu zeigen, die wie
    /// „keine Firma“ aussieht.
    /// </param>
    public CompanyPickerViewModel(ICompanyRepository? companies)
        : this(companies, DefaultDebounce)
    {
    }

    /// <summary>Baut die Auswahl mit abweichender Entprellung.</summary>
    /// <param name="companies">Die Firmensuche, oder <see langword="null"/>.</param>
    /// <param name="debounce">
    /// Die Ruhezeit vor der Anfrage. Muss größer als null sein — eine Entprellung von null wäre
    /// keine.
    /// </param>
    public CompanyPickerViewModel(ICompanyRepository? companies, TimeSpan debounce)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(debounce, TimeSpan.Zero);

        _companies = companies;
        Debounce = debounce;

        // Hintergrundrang wie bei den uebrigen Takten des Hauses: Die Suche darf das Zeichnen
        // der Liste nicht verdraengen, an der der Techniker gerade entlangliest.
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = debounce };
        _debounce.Tick += OnDebounceElapsed;

        if (companies is null)
        {
            _state = CompanyPickerState.Unavailable;
            _message = UnavailableText;
        }
        else
        {
            _message = IdleText;
        }
    }

    /// <summary>Ab wie vielen Zeichen überhaupt gefragt wird.</summary>
    /// <remarks>
    /// Kommt aus dem Vertrag der Suche und wird hier nicht zweitverwaltet: Eine zweite Zahl liefe
    /// irgendwann auseinander, und dann sagte die Oberfläche „ab drei“, während die Suche vier
    /// verlangt.
    /// </remarks>
    public static int MinimumQueryLength => CompanyRepositoryDefaults.MinimumQueryLength;

    /// <summary>Die eingestellte Ruhezeit vor der Anfrage.</summary>
    public TimeSpan Debounce { get; }

    /// <summary>Die Trefferzeilen der letzten beantworteten Suche.</summary>
    /// <remarks>
    /// Gesperrte Firmen stehen mit drin — sie sind nur nicht wählbar. Sie wegzulassen hieße, auf
    /// die Suche nach einer gesperrten Firma „gibt es nicht“ zu antworten.
    /// </remarks>
    public ObservableCollection<CompanyRow> Companies { get; } = [];

    /// <summary>
    /// Was im Feld steht — Suchbegriff <b>oder</b> der Name der gewählten Firma.
    /// </summary>
    /// <remarks>
    /// <b>Keine Auswahl.</b> Hier steht Text und sonst nichts; worauf gebucht wird, steht in
    /// <see cref="Selected"/>. Nach einer Wahl trägt das Feld den Namen der Firma, damit sie
    /// abzulesen ist — geschrieben wird er über <see cref="ShowText"/> und gilt deshalb nicht
    /// als Tippen: Er löst weder eine neue Suche aus noch nimmt er die Wahl zurück.
    /// </remarks>
    [ObservableProperty]
    private string _query = string.Empty;

    /// <summary>
    /// Die Zeile, die im Auswahlfeld markiert ist — der Weg der Aufklappliste zur Wahl.
    /// </summary>
    /// <remarks>
    /// <para>Eigene Eigenschaft und nicht <see cref="Selected"/> selbst, aus zwei Gründen. Erstens
    /// nimmt ein Auswahlfeld nur Zeilen an, die auch in seiner Liste stehen — der Aufrufer setzt
    /// aber <see cref="Selected"/> auch auf eine Firma, die nie gesucht wurde (die am Gerät
    /// erkannte). Über dieselbe Eigenschaft gebunden, würde das Feld diese Firma <b>stillschweigend
    /// verwerfen</b>. Zweitens tauscht jede Suche die Zeilen, und das Feld meldet dabei „nichts
    /// mehr markiert“ — das ist keine Rücknahme der Wahl.</para>
    /// <para>Deshalb: <see langword="null"/> hier bedeutet nichts. Eine Zeile hier bedeutet
    /// „diese ist jetzt gewählt“ und läuft durch dieselbe Prüfung wie jeder andere Weg.</para>
    /// </remarks>
    [ObservableProperty]
    private CompanyRow? _picked;

    /// <summary>Steht die Aufklappliste des Feldes offen?</summary>
    /// <remarks>
    /// Die Treffer stehen in der Aufklappliste. Ginge sie nicht von selbst auf, wären sie zwar
    /// da, aber unsichtbar — der Techniker sähe auf ein Feld, in dem scheinbar nichts passiert.
    /// </remarks>
    [ObservableProperty]
    private bool _isDropDownOpen;

    /// <summary>Die gewählte Zeile; <see langword="null"/> heißt „noch keine Firma gewählt“.</summary>
    /// <remarks>
    /// Eine gesperrte Zeile wird hier <b>abgewiesen</b> und nicht bloß in der Anzeige gedämpft:
    /// Die Liste ist an dieses Feld gebunden, und ein Klick, eine Tastaturauswahl oder ein
    /// Bindungsfehler dürfen nicht an einer Regel vorbeikommen, die in der Vorlage steht.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCompany))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private CompanyRow? _selected;

    /// <summary>Woran die Auswahl gerade ist.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUncertain))]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private CompanyPickerState _state;

    /// <summary>
    /// Der Satz unter dem Suchfeld. <b>Wird unverändert angezeigt.</b>
    /// </summary>
    /// <remarks>
    /// Bei einer beantworteten Suche ist das wörtlich
    /// <see cref="CompanySearchResult.Explanation"/>. Dieser Satz trägt die Unterscheidung
    /// zwischen „es gibt keine“ und „nicht ermittelt“; wer ihn umformuliert, wirft sie weg.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    /// <summary>
    /// Gibt es überhaupt etwas zu melden?
    /// </summary>
    /// <remarks>
    /// <para>Die Meldezeile bleibt sonst unsichtbar. Sie stand früher immer da und
    /// wiederholte, was ohnehin im Platzhalter des Suchfeldes steht — zwei Zeilen, die nach
    /// dem ersten Lesen nichts mehr erklären und das Fenster über den Bildschirmrand
    /// schoben.</para>
    /// <para>Der Ruhetext zählt deshalb ausdrücklich <b>nicht</b> als Meldung: Er ist eine
    /// Anleitung und keine Auskunft über die Suche.</para>
    /// <para>Und eine <b>vollzählige</b> Trefferliste zählt ebenfalls nicht: „2 Firmen gefunden“
    /// steht dann schon in der Aufklappliste, zweimal dasselbe ist keine Auskunft. Zu melden
    /// bleibt genau das, was <b>nicht</b> in der Liste zu sehen ist — dass sie gekürzt ist, dass
    /// TANSS nichts geliefert hat, dass die Suche gescheitert ist, dass gar nicht gefragt
    /// wurde.</para>
    /// </remarks>
    public bool HasMessage =>
        !string.IsNullOrWhiteSpace(Message)
        && !string.Equals(Message, IdleText, StringComparison.Ordinal)
        && !ResultsComplete;

    /// <summary>
    /// Steht die Trefferliste vollzählig in der Aufklappliste?
    /// </summary>
    /// <remarks>
    /// <para><b>Nur bei belegten Treffern.</b> Eine leere Antwort ist hier nie „vollzählig“ —
    /// ihr Satz trägt den Unterschied zwischen „es gibt keine“ und „es sind zu viele“ und darf
    /// unter keinen Umständen wegfallen.</para>
    /// <para><b>Ausgeblendete zählen mit.</b> <c>TotalFound</c> nennt alles, was TANSS genannt
    /// hat, auch die inaktiven Treffer, die nicht in der Liste stehen. Wurde etwas
    /// ausgeblendet, ist die Liste also nicht vollzählig, die Meldezeile bleibt stehen und
    /// nennt die Zahl. Genau so soll es sein: Ausblenden ist erlaubt, Verschweigen nicht.</para>
    /// </remarks>
    private bool ResultsComplete =>
        State == CompanyPickerState.Found
        && Companies.Count > 0
        && _totalFound <= Companies.Count;

    /// <summary>
    /// Warum die zuletzt angeklickte Zeile nicht übernommen wurde — oder <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Ein Klick, der nichts tut, sieht aus wie ein Fehler des Werkzeugs. Steht dort „gesperrt“,
    /// ist es eine Auskunft.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectionProblem))]
    private string? _selectionProblem;

    /// <summary>Die gewählte Firma, wie der Aufrufer sie weiterreicht.</summary>
    public Company? SelectedCompany => Selected?.Company;

    /// <summary>Steht eine Firma fest?</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>Steht ein Hinweis zur abgewiesenen Auswahl an?</summary>
    public bool HasSelectionProblem => SelectionProblem is not null;

    /// <summary>Wartet die Auswahl gerade auf die Entprellung oder auf TANSS?</summary>
    public bool IsWorking => State is CompanyPickerState.Pending or CompanyPickerState.Searching;

    /// <summary>
    /// Ist die Aussage <b>nicht</b> belastbar?
    /// </summary>
    /// <remarks>
    /// Der Fall, für den die Oberfläche einen eigenen Blickfang braucht: nichts gefunden, aber
    /// auch nicht belegt, dass es nichts gibt.
    /// </remarks>
    public bool IsUncertain => State is CompanyPickerState.Undetermined or CompanyPickerState.Failed;

    /// <summary>Steht eine Trefferliste zum Ansehen bereit?</summary>
    public bool HasResults => State == CompanyPickerState.Found && Companies.Count > 0;

    /// <summary>
    /// Sucht sofort, ohne die Entprellung abzuwarten.
    /// </summary>
    /// <remarks>
    /// <para>Der Weg der Eingabetaste — und derselbe Weg, den die Entprellung geht. Deshalb ist
    /// die Auswahl ohne Oberfläche prüfbar: Ein Test schreibt in <see cref="Query"/> und ruft
    /// dies; eine Nachrichtenschleife braucht es nicht.</para>
    /// <para><b>Wirft nicht.</b> Ein Fehlschlag wird zu <see cref="CompanyPickerState.Failed"/>
    /// samt Text. Ausgenommen ist der Abbruch durch den Aufrufer: Er ist kein Fehler und
    /// hinterlässt keinen.</para>
    /// </remarks>
    /// <param name="ct">Abbruchmarke des Aufrufers.</param>
    /// <returns>Der Vorgang.</returns>
    public async Task SearchNowAsync(CancellationToken ct = default)
    {
        _debounce.Stop();

        if (_companies is null)
        {
            ClearRows();
            Show(CompanyPickerState.Unavailable, UnavailableText);
            return;
        }

        string term = Trimmed;
        if (term.Length < MinimumQueryLength)
        {
            ClearRows();
            Show(term.Length == 0 ? CompanyPickerState.Idle : CompanyPickerState.TooShort,
                 term.Length == 0 ? IdleText : ShortfallText(term));
            return;
        }

        CancelRunning();
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = cts;

        Show(CompanyPickerState.Searching, SearchingText);

        try
        {
            CompanySearchResult result =
                await _companies.SearchAsync(term, cts.Token).ConfigureAwait(true);

            // Eine ueberholte Antwort darf die Liste nicht mehr anfassen: Wer waehrend der
            // Anfrage weitergetippt hat, bekaeme sonst Treffer zu einem Begriff, der nicht
            // mehr im Feld steht - und saehe den Unterschied nicht.
            if (IsStale(term, cts))
            {
                return;
            }

            Apply(result);
        }
        catch (OperationCanceledException)
        {
            // Eine abgeloeste Suche ist kein Fehlschlag. Der Zustand gehoert dem Nachfolger.
        }
        catch (Exception ex)
        {
            if (IsStale(term, cts))
            {
                return;
            }

            Fail(ex);
        }
        finally
        {
            if (ReferenceEquals(_running, cts))
            {
                _running = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Übernimmt eine Zeile als Auswahl.
    /// </summary>
    /// <remarks>
    /// <para>Der Weg, den auch das Auswahlfeld über <see cref="Picked"/> nimmt — und den ein
    /// Aufrufer nimmt, der eine Firma von sich aus setzt.</para>
    /// <para>Eine gesperrte Zeile kommt hier nicht durch: Die Prüfung sitzt im Setzer von
    /// <see cref="Selected"/> und damit auf <b>jedem</b> Weg zur Auswahl, nicht nur auf diesem.
    /// Die abgeschaltete Zeile in der Aufklappliste ist die zweite Sperre, nicht die
    /// einzige.</para>
    /// </remarks>
    /// <param name="row">Die gewählte Zeile.</param>
    [RelayCommand]
    public void Select(CompanyRow? row) => Selected = row;

    /// <summary>Leert Feld, Liste und Auswahl.</summary>
    /// <remarks>
    /// <b>Ohne eigene Schaltfläche in der Ansicht.</b> Seit die Auswahl ein beschreibbares
    /// Auswahlfeld ist, tut das Leeren des Feldes genau dasselbe — der Weg über
    /// <c>OnQueryChanged</c> endet in demselben Ruhezustand. Eine Schaltfläche daneben
    /// wäre ein viertes Bedienelement für eine Auswahl. Der Befehl bleibt, weil Aufrufer die
    /// Auswahl auch von sich aus zurücksetzen können müssen.
    /// </remarks>
    [RelayCommand]
    public void Clear()
    {
        Query = string.Empty;

        // Stand das Feld schon leer da, hat die Zuweisung nichts ausgeloest: Der Haken laeuft
        // nur bei einer Aenderung. Ein "Zuruecksetzen", das eine Fehlermeldung stehen laesst,
        // sieht aber aus wie eine Schaltflaeche ohne Wirkung.
        if (State is CompanyPickerState.Idle or CompanyPickerState.Unavailable)
        {
            return;
        }

        _debounce.Stop();
        CancelRunning();
        Selected = null;
        SelectionProblem = null;
        ClearRows();
        IsDropDownOpen = false;
        Show(_companies is null ? CompanyPickerState.Unavailable : CompanyPickerState.Idle,
             _companies is null ? UnavailableText : IdleText);
    }

    /// <summary>
    /// Der Befehl hinter „Noch einmal versuchen“.
    /// </summary>
    /// <remarks>
    /// <para>Führt auf denselben Weg wie die Entprellung. Zwei Wege zur selben Suche liefen
    /// auseinander, sobald einer von beiden geändert wird.</para>
    /// <para>Die Ansicht zeigt ihn nur, wenn die letzte Suche <b>gescheitert</b> oder unklar
    /// geblieben ist. Ohne ihn hätte ein Netzaussetzer keinen Ausweg außer „Text ändern“, und die
    /// Eingabetaste ist im Auswahlfeld belegt: Dort übernimmt sie die markierte Zeile.</para>
    /// </remarks>
    /// <returns>Der Vorgang.</returns>
    [RelayCommand]
    private Task Search() => SearchNowAsync();

    /// <summary>Hält die Entprellung an und bricht eine laufende Anfrage ab.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _debounce.Stop();
        _debounce.Tick -= OnDebounceElapsed;
        CancelRunning();
    }

    /// <summary>Der Suchbegriff ohne umgebende Leerzeichen.</summary>
    private string Trimmed => Query.Trim();

    /// <summary>
    /// Reagiert auf jede Änderung des Suchbegriffs.
    /// </summary>
    /// <remarks>
    /// <para>Hier wird <b>nicht</b> gefragt, sondern nur die Uhr zurückgestellt. Das ist die
    /// Entprellung: Zehn Tastendrücke ergeben eine Anfrage und nicht zehn.</para>
    /// <para>Und hier wird <b>nicht</b> gewählt. Getippter Text ist ein Suchbegriff, auch wenn er
    /// zufällig genau so heißt wie eine Firma aus der Liste. Wer auf eine Firma buchen will, nimmt
    /// sie aus der Aufklappliste.</para>
    /// </remarks>
    /// <param name="value">Der neue Suchbegriff.</param>
    partial void OnQueryChanged(string value)
    {
        // Der Text wird gerade festgehalten: Diese Aenderung kommt vom Auswahlfeld und nicht von
        // einem Tastendruck. Sie zurueckzudrehen ist Sache von Pinned.
        if (_pinned)
        {
            return;
        }

        _debounce.Stop();
        CancelRunning();

        // Eine Auswahl, die nicht mehr zu dem passt, was im Feld steht, behauptet etwas Falsches
        // - und zwar genau in dem Augenblick, in dem gebucht wird.
        Selected = null;
        SelectionProblem = null;

        if (Picked is not null)
        {
            Pinned(() => Picked = null);
        }

        if (_companies is null)
        {
            Show(CompanyPickerState.Unavailable, UnavailableText);
            return;
        }

        string term = value.Trim();
        if (term.Length == 0)
        {
            ClearRows();
            IsDropDownOpen = false;
            Show(CompanyPickerState.Idle, IdleText);
            return;
        }

        if (term.Length < MinimumQueryLength)
        {
            ClearRows();
            IsDropDownOpen = false;
            Show(CompanyPickerState.TooShort, ShortfallText(term));
            return;
        }

        Show(CompanyPickerState.Pending, PendingText);
        _debounce.Start();
    }

    /// <summary>Übernimmt die Zeile, die im Auswahlfeld markiert wurde.</summary>
    /// <remarks>
    /// <see langword="null"/> heißt hier <b>nicht</b> „nichts mehr gewählt“: Das Feld meldet das
    /// auch, wenn nur seine Zeilen getauscht wurden. Welche Firma gilt, steht in
    /// <see cref="Selected"/>, und die überlebt eine neue Suche.
    /// </remarks>
    /// <param name="value">Die markierte Zeile.</param>
    partial void OnPickedChanged(CompanyRow? value)
    {
        if (value is null)
        {
            return;
        }

        Select(value);

        // Abgewiesen - gesperrt. Das Feld darf die Firma dann nicht als seine Auswahl
        // stehenlassen: Es zeigte sonst eine Firma an, auf die nicht gebucht wird.
        if (Selected is null)
        {
            Pinned(() => Picked = null);
        }
    }

    /// <summary>Weist eine gesperrte Firma ab und sagt, warum.</summary>
    /// <param name="value">Die neu gewählte Zeile.</param>
    partial void OnSelectedChanged(CompanyRow? value)
    {
        if (value is null || value.IsSelectable)
        {
            SelectionProblem = null;
            Mark(value);

            // Die gewaehlte Firma steht danach IM Feld - das ist der ganze Zweck des
            // beschreibbaren Auswahlfeldes. Ueber ShowText und nicht ueber Query, weil eine
            // Zuweisung an Query als Tippen gaelte: Sie naehme die Wahl im selben Atemzug
            // wieder zurueck und schickte eine Suche nach dem eigenen Namen los.
            if (value is not null)
            {
                ShowText(NameFor(value));
            }

            return;
        }

        string note = $"„{value.Name}“ ist in TANSS gesperrt. Für eine gesperrte Firma darf keine "
            + "Leistung erfasst werden.";

        // Die Zuweisung durchlaeuft diesen Haken ein zweites Mal, dort aber mit null - kein
        // Kreisel. Der Hinweis wird danach gesetzt, weil der zweite Durchlauf ihn loeschen wuerde.
        Selected = null;
        SelectionProblem = note;
    }

    /// <summary>
    /// Schreibt einen Text ins Feld, ohne ihn als Tippen zu werten.
    /// </summary>
    /// <remarks>
    /// <para>Setzt <see cref="Query"/> unter derselben Marke, unter der auch
    /// <see cref="Pinned"/> arbeitet: Die Änderung läuft durch den Setzer, aber nicht durch
    /// Entprellung, Suche und Rücknahme der Wahl. Genau dafür ist der Weg da — der Name der
    /// eben gewählten Firma darf nicht dieselbe Wirkung haben wie ein Tastendruck.</para>
    /// <para>Steht der Text schon da, wird er trotzdem noch einmal gemeldet: Das Feld kann ihn
    /// verloren haben, ohne den Verlust zurückzumelden, und eine Meldung ohne Änderung kostet
    /// nichts.</para>
    /// </remarks>
    /// <param name="text">Was im Feld stehen soll.</param>
    private void ShowText(string text)
    {
        if (string.Equals(Query, text, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Query));
            return;
        }

        bool outer = _pinned;
        _pinned = true;

        try
        {
            Query = text;
        }
        finally
        {
            _pinned = outer;
        }
    }

    /// <summary>
    /// Führt etwas aus, das dem Auswahlfeld seine Zeilen oder seine Markierung wegnimmt — und
    /// hält dabei den Text im Feld fest.
    /// </summary>
    /// <remarks>
    /// <para>Ein beschreibbares Auswahlfeld schreibt den Text seiner Markierung ins Feld. Fällt
    /// die Markierung weg, schreibt es einen <b>leeren</b> Text zurück, und der käme hier als
    /// „der Benutzer hat das Feld geräumt“ an: Suchbegriff weg, Treffer weg, Zustand
    /// <see cref="CompanyPickerState.Idle"/> — mitten in einer laufenden Suche.</para>
    /// <para>Solange dies läuft, gilt eine Textänderung deshalb nicht als Tippen, und hinterher
    /// steht der Begriff wieder da. Wurde nichts angefasst, geschieht auch nichts.</para>
    /// </remarks>
    /// <param name="work">Der Eingriff, der die Zeilen oder die Markierung anfasst.</param>
    private void Pinned(Action work)
    {
        string keep = Query;
        bool outer = _pinned;
        _pinned = true;

        try
        {
            work();
        }
        finally
        {
            _pinned = outer;
            ShowText(keep);
        }
    }

    /// <summary>Leert die Trefferzeilen, ohne dass das Feld darüber seinen Text verliert.</summary>
    private void ClearRows() => Pinned(Companies.Clear);

    /// <summary>
    /// Der Text, der für eine gewählte Zeile im Feld stehen soll.
    /// </summary>
    /// <remarks>
    /// Der Name — und wenn keiner da ist, die TANSS-Kennung. Ein leeres Feld bei gewählter Firma
    /// sähe aus wie „keine Firma gewählt“, und das wäre gelogen: Der Aufrufer setzt eine am Gerät
    /// erkannte Firma, deren Namen TANSS erst mit der Ticketabfrage nachliefert.
    /// </remarks>
    /// <param name="row">Die gewählte Zeile.</param>
    /// <returns>Was im Feld stehen soll.</returns>
    private static string NameFor(CompanyRow row) =>
        string.IsNullOrWhiteSpace(row.Name)
            ? string.Create(CultureInfo.CurrentCulture, $"TANSS-Kennung {row.Id}")
            : row.Name;

    /// <summary>Setzt die Marke „gewählt“ auf genau eine Zeile.</summary>
    private void Mark(CompanyRow? chosen)
    {
        foreach (CompanyRow row in Companies)
        {
            row.IsChosen = ReferenceEquals(row, chosen);
        }
    }

    private void OnDebounceElapsed(object? sender, EventArgs e)
    {
        _debounce.Stop();

        // SearchNowAsync wirft nicht; ein unbeobachteter Vorgang kann hier also nichts
        // mitreissen. Warten kann der Takt nicht - er laeuft auf dem Strang der Oberflaeche.
        _ = SearchNowAsync();
    }

    /// <summary>Übernimmt eine beantwortete Suche.</summary>
    private void Apply(CompanySearchResult result)
    {
        int previous = Selected?.Id ?? 0;
        _totalFound = result.TotalFound;

        // Festgehalten, weil das Auswahlfeld beim Tausch seiner Zeilen den Begriff aus dem Feld
        // wirft - siehe Pinned.
        Pinned(() =>
        {
            Companies.Clear();
            foreach (Company company in result.Companies)
            {
                Companies.Add(new CompanyRow(company));
            }
        });

        // Der Satz kommt WOERTLICH aus dem Vertrag. Er ist die einzige Stelle, an der der
        // Unterschied zwischen "es gibt keine" und "es sind zu viele" ueberhaupt noch steht.
        Show(
            result.Outcome switch
            {
                CompanySearchOutcome.Found => CompanyPickerState.Found,
                CompanySearchOutcome.NoMatch => CompanyPickerState.NoMatch,
                CompanySearchOutcome.QueryTooShort => CompanyPickerState.TooShort,
                _ => CompanyPickerState.Undetermined,
            },
            result.Explanation);

        // Wer schon gewaehlt hatte und weitergetippt hat, behaelt seine Firma, solange sie noch
        // in der Liste steht. Sie stillschweigend fallen zu lassen hiesse, beim Buchen ohne
        // Firma dazustehen.
        Selected = previous == 0
            ? null
            : Companies.FirstOrDefault(row => row.Id == previous && row.IsSelectable);

        // Die Treffer stehen in der Aufklappliste des Feldes. Klappt sie nicht auf, sind sie da
        // und trotzdem unsichtbar - das Feld saehe aus, als taete es nichts.
        IsDropDownOpen = HasResults;
    }

    /// <summary>Meldet einen Fehlschlag, ohne etwas zu sperren.</summary>
    /// <remarks>
    /// Hausregel 5: Die Suche ist hin, die Auswahl nicht. Das Feld bleibt offen, der Befehl
    /// bleibt anklickbar, und was fehlschlug, steht im Text — samt der Aussage, dass damit nichts
    /// über die Firma feststeht.
    /// </remarks>
    private void Fail(Exception error)
    {
        ClearRows();
        IsDropDownOpen = false;
        Show(CompanyPickerState.Failed,
             "Die Firmensuche ist gescheitert: " + error.Message
             + " Ob es die Firma gibt, ist damit nicht ermittelt. Die Eingabe bleibt stehen — "
             + "bitte noch einmal versuchen.");
    }

    /// <summary>Setzt Zustand und Text in einem Zug.</summary>
    private void Show(CompanyPickerState state, string message)
    {
        State = state;
        Message = message;

        // HasResults und HasMessage haengen an Zustand UND Liste; die Liste meldet ihre
        // Aenderung nicht an die abgeleiteten Eigenschaften.
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasMessage));
    }

    /// <summary>Ist diese Anfrage von einer neueren überholt worden?</summary>
    private bool IsStale(string term, CancellationTokenSource cts) =>
        cts.IsCancellationRequested
        || !ReferenceEquals(_running, cts)
        || !string.Equals(term, Trimmed, StringComparison.Ordinal);

    private void CancelRunning()
    {
        CancellationTokenSource? running = _running;
        _running = null;
        running?.Cancel();
    }

    /// <summary>Sagt, wie viele Zeichen noch fehlen — und dass TANSS nicht gefragt wurde.</summary>
    private static string ShortfallText(string term)
    {
        int missing = MinimumQueryLength - term.Length;

        return string.Create(
            CultureInfo.CurrentCulture,
            $"Noch {missing} Zeichen: Gesucht wird erst ab {MinimumQueryLength} Zeichen. TANSS wurde nicht gefragt.");
    }
}
