using System.Windows;
using System.Windows.Threading;
using TanssLogWatcher.App.Design;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.ViewModels;
using TanssLogWatcher.App.Views;
using Wpf.Ui.Appearance;

namespace TanssLogWatcher.App;

/// <summary>
/// Einstiegspunkt der Anwendung.
/// </summary>
/// <remarks>
/// <para><b>Einzelinstanz mit <c>Local\</c>-Präfix.</b> Das Original benutzte ein maschinenweites
/// Wartehandle (<c>MachineName + GUID</c>). Auf einem Terminalserver sperrt damit der erste
/// angemeldete Benutzer alle weiteren aus — sie starten das Werkzeug und es passiert nichts.
/// <c>Local\</c> bindet die Sperre an die Anmeldesitzung, wo sie hingehört: das Werkzeug ist
/// ein Einzelarbeitsplatzwerkzeug je Benutzer, nicht je Maschine.</para>
/// <para><b>Startargumente.</b> Neben <c>--minimized</c> nimmt der Start drei Schalter, die
/// die Oberfläche prüfbar machen, ohne am Rechner des Benutzers etwas zu verstellen:</para>
/// <list type="bullet">
/// <item><c>--theme=light</c> | <c>--theme=dark</c> | <c>--theme=system</c> — erzwingt die
/// Darstellung, statt der Systemeinstellung zu folgen. Ohne den Schalter gilt
/// <c>system</c>. Eine Einstellung „Darstellung“ in der Oberfläche wird später denselben
/// Weg gehen; deshalb steht die Auswertung hier und nicht in einem Prüfgerüst.</item>
/// <item><c>--page=sessions|queue|timers|monitoring|recording|diagnostics|connection</c> — wählt die
/// Startseite. Ohne den Schalter sind es die Sitzungen.</item>
/// <item><c>--show-save-dialog</c> — zeigt den Abschlussdialog einmalig beim Start. Er
/// erscheint sonst nur am Ende einer echten Sitzung und wäre nicht zu begutachten.</item>
/// </list>
/// </remarks>
public partial class App : IDisposable
{
    private const string InstanceName = @"Local\TanssLogWatcher.SingleInstance";

    /// <summary>Wie lange das geordnete Beenden höchstens dauern darf.</summary>
    /// <remarks>
    /// Grosszügig, weil in dieser Zeit die laufenden Sitzungen abgeschlossen und eingereiht
    /// werden — das ist der Grund, aus dem dieses Werkzeug gebaut wurde. Eine Obergrenze gibt es
    /// trotzdem: Ein Windows, das sich abmeldet, wartet nicht ewig, und ein Prozess, der beim
    /// Herunterfahren hängt, wird abgeschossen — dann wäre alles verloren statt nur das Letzte.
    /// </remarks>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(10);

    private readonly Queue<SessionClosed> _pendingDialogs = new();
    private Mutex? _instanceLock;
    private AppHost? _host;
    private bool _dialogOpen;

    /// <summary>
    /// Die Laufzeit: Konfiguration, Bausteine, Betriebszustand und die drei Hintergrunddienste.
    /// </summary>
    /// <remarks>
    /// <b>Der eine Zugang für die Ansichtsmodelle.</b> Sie binden gegen
    /// <see cref="AppHost.Status"/> und die Dienste darunter; erzeugt und beendet wird der Host
    /// ausschliesslich hier. Ein zweiter Host hätte eine zweite Zustandsdatenbank — und zwei
    /// Läufe, die dieselbe Datei halten wollen, sind genau der Fehler, den der Betriebszustand
    /// sonst meldet.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Vor <see cref="OnStartup"/> oder nach <see cref="OnExit"/> abgerufen.
    /// </exception>
    public AppHost Host => _host ?? throw new InvalidOperationException(
        "Die Laufzeit steht erst ab OnStartup bereit und nach OnExit nicht mehr. Wird sie "
        + "früher abgerufen, ist die Reihenfolge im Start falsch — nicht die Laufzeit.");

    /// <summary>Die Laufzeit der laufenden Anwendung.</summary>
    /// <remarks>
    /// Der Griff für Ansichtsmodelle, die keinen Verweis durchgereicht bekommen. Ausdrücklich
    /// eine Bequemlichkeit und kein Entwurfsmuster: Wo ein Verweis durchgereicht werden kann,
    /// ist er die bessere Wahl — er lässt sich in einem Test ersetzen.
    /// </remarks>
    public static AppHost Runtime => ((App)Current).Host;

    /// <summary>
    /// Der gemeinsame Nachschlag „Fernwartungstyp 1003 heisst PuTTY und ist schwarz“.
    /// </summary>
    /// <remarks>
    /// Einer für die ganze Anwendung: Vier Seiten zeigen den Typ, und jede für sich abzufragen
    /// hiesse, beim Blättern durch die Navigation viermal dieselbe Liste zu holen. Die Seiten
    /// entstehen bei jedem Wechsel neu — ein Nachschlag je Seite wäre ein Netzaufruf je Klick.
    /// </remarks>
    public static SystemLookup Systems { get; } = new();

    /// <summary>
    /// Die Suche nach neuen Fassungen bei GitHub.
    /// </summary>
    /// <remarks>
    /// Hängt ausdrücklich <b>nicht</b> an <see cref="AppHost"/>: Eine Aktualisierung ist auch
    /// dann zu holen, wenn nichts eingerichtet ist — gerade dann, wenn die fehlende Einrichtung
    /// an einem Fehler liegt, den die neue Fassung behebt.
    /// </remarks>
    public static UpdateService Updates { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceLock = new Mutex(initiallyOwned: true, InstanceName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Eine zweite Instanz sagt das und geht. Das Original rief hier Activate() auf einem
            // Fenster auf, das nie gezeigt worden war - der erneute Start wirkte folgenlos.
            MessageBox.Show(
                "Der TANSS Log-Watcher läuft bereits. Das Symbol finden Sie im Infobereich "
                + "der Taskleiste, rechts unten neben der Uhr.",
                "TANSS Log-Watcher", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        ApplyTheme(Option(e.Args, "--theme"));

        // Muss nach dem Setzen der Darstellung stehen: die eigenen Bedeutungsfarben werden an
        // der gerade gesetzten Darstellung ausgerichtet, bevor das erste Fenster entsteht.
        ThemeTokens.Track();

        bool startMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        // VOR dem Fenster, und zwar nur der Konstruktor: Er rührt weder Platte noch Netz an
        // (siehe AppHost) und verzögert das erste Fenster deshalb nicht. Gelesen und gestartet
        // wird weiter unten. Umgekehrt käme das Fenster zuerst — und sein Loaded-Ereignis
        // griffe über App.Runtime auf eine Laufzeit zu, die es noch nicht gibt. Genau daran
        // blieb der Start hängen: ein Fenster, das nie sichtbar wurde.
        _host = new AppHost();

        var window = new MainWindow(Option(e.Args, "--page"));
        MainWindow = window;

        if (startMinimized)
        {
            // Beim Autostart bleibt das Fenster zu. Sichtbar ist das Symbol im Infobereich -
            // verdeckter Betrieb ist bei einem Werkzeug, das Fenstertitel mitliest, keine Option.
            window.Hide();
        }
        else
        {
            window.Show();
        }

        // Der Abschlussdialog haengt am Ende einer Sitzung und nicht an einer Seite: Im
        // Normalbetrieb ist das Fenster zu, und genau dann muss er trotzdem aufgehen.
        _host.Sessions.SessionEnded += OnSessionEnded;

        // Erst nach dem Fenster und ausdruecklich ohne await: Der Start soll nicht an einer
        // toten Leitung haengen. Liegt keine Konfiguration vor - heute der Normalfall -, tut
        // der Host genau das Richtige und sagt es ueber seinen Betriebszustand.
        _ = _host.StartAsync();

        // Erst wenn das Fenster steht: Der Assistent ist ein Kindfenster und braucht einen
        // Besitzer, sonst verschwindet er beim ersten Klick hinter das Hauptfenster. Die
        // niedrige Dringlichkeit sorgt dafuer, dass StartAsync die Konfiguration vorher gelesen
        // hat - sie wird gleich zu Beginn synchron geladen, der Netzaufruf laeuft erst danach.
        if (!startMinimized)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, OfferSetup);
        }
    }

    /// <summary>
    /// Eine Sitzung ist zu Ende — den Abschlussdialog anbieten.
    /// </summary>
    /// <remarks>
    /// <para><b>Nur für eingereihte Sitzungen.</b> Ohne Zuordnung auf einen Fernwartungstyp und
    /// im Trockenlauf entsteht gar kein Eintrag, an dem sich etwas ändern liesse; ein Fenster
    /// dafür wäre ein Fenster ohne Handlungsmöglichkeit. Wer wissen will, warum nichts gebucht
    /// wurde, findet es unter „Überwachung“.</para>
    ///
    /// <para><b>Einer nach dem anderen.</b> Enden zwei Sitzungen im selben Takt — beim
    /// Abmelden durchaus der Normalfall —, stapelten sich sonst zwei Fenster übereinander, von
    /// denen der Techniker nur das oberste sieht und das darunter blind wegklickt.</para>
    /// </remarks>
    private void OnSessionEnded(object? sender, SessionClosed closed)
    {
        if (closed.Disposition is not (SessionDisposition.Enqueued
            or SessionDisposition.AlreadyQueued))
        {
            return;
        }

        _pendingDialogs.Enqueue(closed);
        ShowNextDialog();
    }

    /// <summary>Zeigt den nächsten wartenden Abschlussdialog, wenn gerade keiner offen ist.</summary>
    private void ShowNextDialog()
    {
        if (_dialogOpen || _host is not { } host || !_pendingDialogs.TryDequeue(out SessionClosed? closed))
        {
            return;
        }

        _dialogOpen = true;

        SaveSessionWindow dialog = new(host, closed,
                                       Systems.NameFor(closed.Session.RemoteSupportTypeId),
                                       Systems.ColorFor(closed.Session.RemoteSupportTypeId),
                                       Systems.OwnTechnicianName);

        dialog.Closed += (_, _) =>
        {
            _dialogOpen = false;
            ShowNextDialog();
        };

        dialog.Show();
        _ = dialog.Activate();
    }

    /// <summary>
    /// Bietet den Einrichtungsassistenten an, wenn noch nichts eingerichtet ist.
    /// </summary>
    /// <remarks>
    /// <para><b>Beim Autostart ausdrücklich nicht.</b> Ein Assistent, der sich beim Anmelden an
    /// Windows ungefragt vor alles andere schiebt, ist eine Zumutung — und beim Autostart
    /// steht der Techniker meistens gar nicht vor dem Rechner. Dort bleibt es beim Symbol im
    /// Infobereich, dessen Hinweistext „nicht eingerichtet“ sagt.</para>
    /// <para>Wird abgebrochen, passiert nichts weiter: Das Fenster steht dann im Zustand
    /// „nicht eingerichtet“ und bietet unter „Verbindung“ dasselbe noch einmal an.</para>
    /// </remarks>
    private void OfferSetup()
    {
        if (_host is not { } host || host.Status.IsConfigured)
        {
            return;
        }

        SetupWindow setup = new(host) { Owner = MainWindow };
        _ = setup.ShowDialog();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Hier wird mit Absicht gewartet. Das geordnete Ende schliesst laufende Fernwartungen
        // ab und reiht sie ein; wer an dieser Stelle durchliefe, verlöre genau die Sitzung, die
        // gerade noch lief - der Mangel der Vorlage, der bares Geld kostete.
        try
        {
            _ = _host?.StopAsync().Wait(ShutdownGrace);
        }
        catch (AggregateException)
        {
            // Was hier ankommt, ist im geordneten Ende bereits gemeldet worden. Ein Absturz
            // beim Beenden verhindert nur noch das Aufraeumen darunter.
        }

        Dispose();
        base.OnExit(e);
    }

    /// <summary>Gibt Laufzeit und Einzelinstanz-Sperre frei.</summary>
    public void Dispose()
    {
        _host?.Dispose();
        _host = null;
        Updates.Dispose();
        _instanceLock?.Dispose();
        _instanceLock = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Liest den Wert eines Startarguments der Form <c>--name=wert</c>.
    /// </summary>
    /// <returns><see langword="null"/>, wenn das Argument fehlt.</returns>
    private static string? Option(string[] args, string name)
    {
        string prefix = name + "=";
        foreach (string arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[prefix.Length..];
            }
        }

        return null;
    }

    private static void ApplyTheme(string? wanted)
    {
        // Ein unbekannter Wert folgt dem System, statt zu scheitern: der Schalter ist ein
        // Werkzeug zum Nachsehen, kein Teil des Betriebs.
        switch (wanted?.ToLowerInvariant())
        {
            case "light":
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
            case "dark":
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;
            default:
                // Einmalig beim Start. Ein Wechsel der Systemeinstellung zur Laufzeit wird
                // derzeit NICHT nachgezogen - dafuer fehlt SystemThemeWatcher.
                ApplicationThemeManager.ApplySystemTheme();
                break;
        }
    }
}
