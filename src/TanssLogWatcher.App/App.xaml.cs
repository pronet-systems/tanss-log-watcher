using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.App.Design;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.Services;
using TanssLogWatcher.App.ViewModels;
using TanssLogWatcher.App.Views;
using TanssLogWatcher.Storage.Logging;
using Wpf.Ui.Appearance;

namespace TanssLogWatcher.App;

/// <summary>
/// Einstiegspunkt der Anwendung.
/// </summary>
/// <remarks>
/// <para><b>Einzelinstanz mit <c>Local\</c>-Präfix.</b> Ein maschinenweites Wartehandle
/// (<c>MachineName + GUID</c>) sperrt auf einem Terminalserver mit dem ersten angemeldeten
/// Benutzer alle weiteren aus — sie starten das Werkzeug und es passiert nichts.
/// <c>Local\</c> bindet die Sperre an die Anmeldesitzung, wo sie hingehört: das Werkzeug ist
/// ein Einzelarbeitsplatzwerkzeug je Benutzer, nicht je Maschine.</para>
/// <para><b>Startargumente.</b> Neben <c>--minimized</c> nimmt der Start drei Schalter, die
/// die Oberfläche prüfbar machen, ohne am Rechner des Benutzers etwas zu verstellen:</para>
/// <list type="bullet">
/// <item><c>--theme=light</c> | <c>--theme=dark</c> | <c>--theme=system</c> — erzwingt die
/// Darstellung, statt der Systemeinstellung zu folgen. Ohne den Schalter gilt
/// <c>system</c>. Eine Einstellung „Darstellung“ in der Oberfläche wird später denselben
/// Weg gehen; deshalb steht die Auswertung hier und nicht in einem Prüfgerüst.</item>
/// <item><c>--page=sessions|queue|timers|history|monitoring|recording|diagnostics|</c>
/// <c>connection|about</c> — wählt die Startseite. Ohne den Schalter sind es die Sitzungen.
/// <c>connection</c> ist die Seite, die in der Navigation „Einstellungen“ heisst — der
/// Schalterwert bleibt, damit eine Verknüpfung aus einem frueheren Stand weiter trifft.</item>
/// <item><c>--show-save-dialog</c> — zeigt den Abschlussdialog einmalig beim Start. Er
/// erscheint sonst nur am Ende einer echten Sitzung und wäre nicht zu begutachten. Gezeigt
/// wird eine <b>erfundene</b> Sitzung, und sie sagt das auch — an jeder Stelle, an der sie im
/// Fenster auftaucht. Bedienbar ist das Fenster vollständig, und es geht denselben Weg wie
/// eine echte Sitzung: <b>Wer darin bucht, legt in TANSS eine echte Fernwartung an</b>, deren
/// Kommentar sie als Vorschau ausweist.</item>
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

    private Mutex? _instanceLock;
    private AppHost? _host;
    private DialogGate? _dialogs;

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
            // Eine zweite Instanz sagt das und geht. Ein Activate() traefe hier ein Fenster, das
            // nie gezeigt worden ist - der erneute Start wirkte folgenlos.
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

        // Stand bisher nur in der Dokumentation darueber und wurde nirgends ausgewertet - der
        // Schalter tat buchstaeblich nichts. Er laeuft ueber denselben Weg wie eine echte
        // Sitzung (DialogGate), damit er auch das prueft, was im Betrieb schiefgehen kann.
        if (e.Args.Contains("--show-save-dialog", StringComparer.OrdinalIgnoreCase))
        {
            SessionClosed preview = PreviewSession();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                                       () => Dialogs.Enqueue(preview));
        }
    }

    /// <summary>
    /// Eine erfundene Sitzung für <c>--show-save-dialog</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Sie ist als erfunden erkennbar, und das an jeder Stelle, an der sie im Fenster
    /// auftaucht</b> — Gegenstelle, Anwendung und Begründung sagen es. Hausregel 2: Ein
    /// Vorschaufenster, das wie eine echte Sitzung aussieht, ist eine Vortäuschung.</para>
    ///
    /// <para><b>Sie ist wirklich buchbar</b>, und dafür braucht es seit dem Umbau nichts mehr:
    /// Sie geht denselben Weg wie jede echte Sitzung — der Dialog hält sie, keine
    /// Warteschlangenzeile entsteht, und „In TANSS buchen“ sendet unmittelbar. Der Schalter
    /// soll den ganzen Weg prüfen und nicht nur die Darstellung: Ein Fenster, in dem die
    /// Aufklappfelder gesperrt sind und der Knopf nichts tut, zeigt gerade das nicht, worauf es
    /// ankommt. <b>Wer hier bucht, legt eine echte Fernwartung an</b> — der Kommentar sagt in
    /// der ersten Zeile, dass sie aus der Vorschau stammt, damit sie in TANSS als das erkennbar
    /// ist, was sie ist.</para>
    ///
    /// <para><b>Auch das Schliessen ohne Antwort verhält sich wie bei einer echten Sitzung:</b>
    /// Die erfundene Sitzung wandert dann in die Warteschlange und wird beim nächsten Start
    /// erneut vorgelegt. Das ist kein Versehen, sondern derselbe Weg — dort ist sie zu
    /// verwerfen. Die Begründung im Fenster sagt es.</para>
    ///
    /// <para>Ohne Konfiguration oder ohne eine Anbindung ab Typ 1000 gibt es keinen
    /// Fernwartungstyp, auf den zu buchen wäre. Dann geht das Fenster trotzdem auf, sperrt aber
    /// von selbst — und sagt, warum.</para>
    /// </remarks>
    private SessionClosed PreviewSession()
    {
        DateTimeOffset now = Host.Clock.GetLocalNow();

        SessionSnapshot session = new()
        {
            SessionId = Guid.NewGuid().ToString(),
            ProfileKey = "vorschau",
            ProfileName = "Vorschau (keine echte Sitzung)",
            Destination = "BEISPIEL-PC (erfunden)",
            StartedAt = now.AddMinutes(-42),
            EndedAt = now,
            Comment = "VORSCHAU aus --show-save-dialog — diese Fernwartung stammt aus einer "
                + "erfundenen Sitzung.",
        };

        // Ein Fernwartungstyp, den dieser Arbeitsplatz wirklich fuehrt - kein geratener.
        int type = Host.Config?.Monitoring
            .Select(entry => entry.RemoteSupportTypeId)
            .FirstOrDefault(value => value >= 1000) ?? 0;

        if (Host.Composition is null || type <= 0)
        {
            return new SessionClosed(session, SessionDisposition.DryRun,
                "Vorschau: Ohne eingerichtete Verbindung und ohne eine Anbindung ab Typ 1000 "
                + "gibt es keinen Fernwartungstyp, auf den gebucht werden könnte. Das Fenster "
                + "ist zu sehen, aber nicht zu beantworten.");
        }

        // Der Typ gehoert in die Abschrift und nicht daneben: Aus ihr baut der Dialog die
        // Nutzlast (SessionWatcherService.PayloadFor), und ohne Zuordnung waere sie nicht zu
        // bilden - das Fenster sperrte von selbst.
        return new SessionClosed(session with { RemoteSupportTypeId = type },
            SessionDisposition.Held,
            "Vorschau: Die Sitzung ist erfunden, das Fenster aber vollständig zu bedienen — und "
            + "es geht denselben Weg wie eine echte Sitzung. „In TANSS buchen“ legt deshalb "
            + "eine ECHTE Fernwartung an; ihr Kommentar weist sie als Vorschau aus. Wer das "
            + "Fenster ohne Antwort schliesst, findet die erfundene Sitzung danach in der "
            + "Warteschlange und kann sie dort verwerfen.");
    }

    /// <summary>
    /// Eine Sitzung ist zu Ende — den Abschlussdialog anbieten.
    /// </summary>
    /// <remarks>
    /// <para><b>Nur für Sitzungen, die sich beantworten lassen.</b> Die Bedingung stand früher
    /// auf „liegt eine Warteschlangenzeile vor“ — seit beim Sitzungsende keine mehr entsteht,
    /// wäre das der Weg gewesen, den Dialog ersatzlos zu verlieren. Sie heisst jetzt
    /// <see cref="SaveSessionViewModel.CanAnswer"/> und steht dort, wo auch das Fenster selbst
    /// entscheidet, ob es etwas anzubieten hat: Zwei getrennte Bedingungen liefen früher oder
    /// später auseinander, und dann ginge ein Fenster auf, das nichts kann — oder keines, wo
    /// eines hingehört.</para>
    ///
    /// <para>Ohne Zuordnung auf einen Fernwartungstyp und im Trockenlauf gibt es nichts, worauf
    /// gebucht werden könnte; dafür geht kein Fenster auf. Wer wissen will, warum nichts
    /// gebucht wurde, findet es unter „Überwachung“.</para>
    ///
    /// <para><b>Einer nach dem anderen.</b> Enden zwei Sitzungen im selben Takt — beim
    /// Abmelden durchaus der Normalfall —, stapelten sich sonst zwei Fenster übereinander, von
    /// denen der Techniker nur das oberste sieht und das darunter blind wegklickt.</para>
    /// </remarks>
    private void OnSessionEnded(object? sender, SessionClosed closed)
    {
        ArgumentNullException.ThrowIfNull(closed);

        if (!SaveSessionViewModel.CanAnswer(closed.Disposition))
        {
            return;
        }

        Dialogs.Enqueue(closed);
    }

    /// <summary>
    /// Die Schlange der Abschlussdialoge.
    /// </summary>
    /// <remarks>
    /// Spät gebaut und nicht im Konstruktor: <see cref="ShowDialog"/> greift auf
    /// <see cref="Host"/> zu, und den gibt es erst ab <see cref="OnStartup"/>.
    /// </remarks>
    private DialogGate Dialogs =>
        _dialogs ??= new DialogGate(ShowDialog, LogDialogFailure, LogDialogShown);

    /// <summary>Baut das Fenster und zeigt es.</summary>
    /// <remarks>
    /// <para>Der Riegel gegen zwei gleichzeitige Dialoge steht in <see cref="DialogGate"/> und
    /// ausdrücklich nicht hier: Dort lässt er sich ohne Fenster prüfen, und genau an ihm hing
    /// der Verdacht, das Werkzeug zeige nach einem einzigen misslungenen Bau nie wieder
    /// einen Abschlussdialog.</para>
    /// <para><b>Ohne <c>Activate()</c>, und das ist der Punkt.</b> Das Fenster geht auf, während
    /// jemand arbeitet — eine Fernwartung endet mitten im Tippen. Holte es den Vordergrund, traf
    /// der nächste Tastendruck einen seiner Knöpfe; so sind fünf erfundene Fernwartungen in
    /// TANSS entstanden, die von Hand wieder zu löschen waren. <c>Topmost</c> und
    /// <c>ShowActivated="False"</c> im XAML lassen es über allem stehen, ohne die Tastatur
    /// mitzunehmen: gesehen wird es, getroffen nicht.</para>
    /// </remarks>
    private void ShowDialog(SessionClosed closed, Action onClosed)
    {
        SaveSessionWindow dialog = new(Host, closed,
                                       Systems.NameFor(closed.Session.RemoteSupportTypeId),
                                       Systems.ColorFor(closed.Session.RemoteSupportTypeId),
                                       Systems.OwnTechnicianName);

        dialog.Closed += (_, _) => onClosed();

        dialog.Show();
    }

    /// <summary>Hält fest, dass der Abschlussdialog gezeigt wurde.</summary>
    /// <remarks>Hausregel 5: Ein misslungener Vermerk darf das Fenster nicht kosten.</remarks>
    private void LogDialogShown(SessionClosed closed)
    {
        if (_host?.Composition is not { } composition)
        {
            return;
        }

        // Zu einer Sitzung, die gar nicht zu beantworten ist, gibt es nichts zu vermerken -
        // und ein Eintrag, der etwas ueber ihren Verbleib behauptete, stuende falsch im echten
        // Aenderungsprotokoll des Technikers, zwischen lauter wahren Zeilen.
        if (!SaveSessionViewModel.CanAnswer(closed.Disposition))
        {
            return;
        }

        // Der Satz muss sagen, wo die Sitzung wirklich liegt: Beim Regelfall Held haelt der
        // Dialog sie und sie steht NIRGENDS; die beiden anderen Ausgaenge kommen aus einem
        // frueheren Lauf und haben ihre Zeile.
        string whereabouts = closed.Disposition == SessionDisposition.Held
            ? "Die Sitzung steht in keiner Warteschlange — dieses Fenster hält sie. Gebucht "
              + "wird aus ihm heraus; eingereiht wird nur, wenn das misslingt oder niemand "
              + "antwortet."
            : "Die Sitzung wartet in der Warteschlange, bis er beantwortet ist; von selbst "
              + "geht sie nicht hinaus.";

        try
        {
            _ = composition.Log.Append(new SessionLogEntry
            {
                Operation = "dialog.shown",
                Outcome = SessionOutcome.Ok,
                Reason = "Der Abschlussdialog wurde gezeigt. " + whereabouts,
                Trigger = SessionTrigger.Manual,
                RemoteMaintenanceId = closed.Session.SessionId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("dialog.shown nicht vermerkt: " + ex.Message);
        }
    }

    /// <summary>
    /// Hält fest, dass der Abschlussdialog <b>nicht</b> aufging.
    /// </summary>
    /// <remarks>
    /// <para><b>Der teuerste stille Fehler dieses Werkzeugs wäre genau dieser</b>: Ein Dialog,
    /// der nicht aufgeht, kostet unbemerkt Arbeitszeit — der Techniker merkt es nicht, weil
    /// nichts zu sehen ist, und das Werkzeug merkte es bisher auch nicht, weil
    /// <see cref="RuntimeNotifier"/> jede Ausnahme des Empfängers ohne Protokollzeile
    /// schluckt.</para>
    /// <para><b>Seit dem Umbau reiht diese Stelle die Sitzung selbst ein, und das ist keine
    /// Bequemlichkeit.</b> Bis hierher wartete sie ohnehin schon in der Warteschlange, und der
    /// misslungene Dialog kostete nur das Fenster. Jetzt hält der Dialog die Sitzung — geht er
    /// nicht auf, hält sie niemand. Eingereiht wird wartend: Der nächste Start legt sie erneut
    /// vor, und ungefragt gebucht wird nichts.</para>
    ///
    /// <para><b>Das deckt zugleich den zweiten Fall ab, für den es sonst keinen Ort gäbe:</b>
    /// Enden zwei Sitzungen zugleich und wird das Werkzeug beendet, während die zweite noch in
    /// der Schlange der Dialoge steht, scheitert ihr Fenster am zugehenden Programm — und
    /// landet genau hier.</para>
    /// </remarks>
    private void LogDialogFailure(SessionClosed closed, Exception error)
    {
        ArgumentNullException.ThrowIfNull(closed);
        ArgumentNullException.ThrowIfNull(error);

        if (_host?.Composition is not { } composition)
        {
            System.Diagnostics.Debug.WriteLine(
                "Abschlussdialog nicht geöffnet und keine Laufzeit mehr: " + error.Message);
            return;
        }

        string rescue = Rescue(composition, closed)
            ? "Die Sitzung ist nicht verloren — sie ist eingereiht worden, wartet dort auf eine "
              + "Entscheidung und wird beim nächsten Start erneut vorgelegt. Bis dahin geht sie "
              + "NICHT nach TANSS."
            : "Die Sitzung liess sich auch nicht einreihen. Sie ist damit nur noch als laufende "
              + "Sitzung vermerkt und muss gegebenenfalls von Hand in TANSS nachgetragen "
              + "werden.";

        string reason = string.Create(CultureInfo.CurrentCulture,
            $"Der Abschlussdialog liess sich nicht öffnen: {Redaction.Scrub(error.Message)} "
            + $"{rescue}");

        try
        {
            _ = composition.Log.Append(new SessionLogEntry
            {
                Operation = "dialog.failed",
                Outcome = SessionOutcome.Error,
                Reason = reason,
                Trigger = SessionTrigger.Manual,
                RemoteMaintenanceId = closed.Session.SessionId,
                Detail = error.GetType().FullName,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("dialog.failed nicht vermerkt: " + ex.Message);
        }
    }

    /// <summary>
    /// Reiht eine Sitzung ein, für die kein Fenster aufgehen konnte.
    /// </summary>
    /// <remarks>
    /// <para>Wartend (<c>awaitDecision</c>): Der Techniker ist nicht gefragt worden, und was
    /// niemand beantwortet hat, darf nicht mit der automatischen Beschreibung hinausgehen. Der
    /// nächste Start legt den Dialog erneut vor.</para>
    /// <para>Die Nutzlast entsteht über <see cref="SessionWatcherService.PayloadFor"/> und
    /// damit über dieselbe Stelle wie überall sonst — eine hier nachgebaute wäre eine zweite
    /// Rechnung, und die Existenzprüfung gegen Dubletten vergleicht genau diese Felder.</para>
    /// <para>Hausregel 5: Misslingt es, ist das kein Grund, den Protokolleintrag ausfallen zu
    /// lassen — der sagt dann eben, dass auch das Einreihen nicht ging.</para>
    /// </remarks>
    /// <param name="composition">Die Bausteine.</param>
    /// <param name="closed">Die Sitzung, deren Fenster ausblieb.</param>
    /// <returns><see langword="true"/>, wenn sie jetzt in der Warteschlange steht.</returns>
    private static bool Rescue(RuntimeComposition composition, SessionClosed closed)
    {
        try
        {
            if (SessionWatcherService.PayloadFor(composition, closed.Session) is not { } payload)
            {
                return false;
            }

            // Auch ein "stand schon da" ist hier ein Ja: Die Sitzung ist dann versorgt, und
            // genau danach wird gefragt.
            _ = composition.Queue.Enqueue(payload, awaitDecision: true);
            _ = composition.Queue.RemoveOpenSession(closed.Session.SessionId);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Sitzung nicht gesichert: " + ex.Message);
            return false;
        }
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
    /// „nicht eingerichtet“ und bietet unter „Einstellungen“ dasselbe noch einmal an.</para>
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
        // gerade noch lief - und das kostet bares Geld.
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
