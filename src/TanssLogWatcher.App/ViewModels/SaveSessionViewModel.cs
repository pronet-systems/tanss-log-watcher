using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Ai;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.Services;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Der Abschlussdialog: der Bericht zu einer gerade beendeten Sitzung.
/// </summary>
/// <remarks>
/// <para><b>Dieses Fenster hält die Sitzung — sie liegt nicht in der Warteschlange.</b> Das ist
/// die Kehrtwende gegenüber der Vorgängerversion: Dort entstand beim Sitzungsende sofort eine
/// Zeile, und dieses Fenster ergänzte sie nur. Jetzt gilt die Ansage des Hauses: <i>In die
/// Warteschlange wird nur geschrieben, wenn die Buchung fehlgeschlagen ist.</i> Solange dieses
/// Fenster offen steht, steht die Sitzung nirgendwo sonst.</para>
///
/// <para><b>Was das kostet, und was es dafür gibt.</b> Ein Absturz mitten im Tippen verliert
/// den getippten Bericht — die Zeile, die ihn vorher gerettet hätte, gibt es nicht mehr. Die
/// <i>Sitzung</i> verliert er nicht: Sie bleibt bis zur Antwort als laufende Sitzung in
/// <c>open_sessions</c> vermerkt, und die Wiederherstellung beim nächsten Start legt sie erneut
/// vor. Dafür steht in TANSS nichts, was niemand veranlasst hat, und die Warteschlange zeigt
/// wieder nur das, was wirklich hängt.</para>
///
/// <para><b>„In TANSS buchen“ sendet unmittelbar.</b> Gibt es keine Zeile — der Regelfall —,
/// baut dieses Fenster die Nutzlast über <see cref="SessionWatcherService.PayloadFor"/>, also
/// über dieselbe Stelle wie der Beobachter, und sendet sie über
/// <c>IRemoteSupportRepository.CreateWithDiagnosticsAsync</c>: derselbe Aufruf, den auch der
/// Sendedienst macht, samt Gegenprobe der Mitarbeiterzuordnung. Gibt es eine Zeile — eine
/// Sitzung aus einem früheren Lauf —, läuft es über <see cref="IImmediateBooking"/> und damit
/// über den Sendedienst mitsamt seiner Existenzprüfung. Ein erster Versand braucht diese
/// Prüfung nicht (der Sendedienst stellt sie ebenfalls erst ab dem zweiten Versuch); eine
/// Wiederholung darf ohne sie nicht geschehen, denn TANSS dedupliziert nicht.</para>
///
/// <para><b>Scheitert das Senden, wird ERST DANN eingereiht</b> — und der Fehler wird benannt:
/// Ein Netzfehler heilt durch Warten, ein „diesen Fernwartungstyp gibt es nicht“ heilt nicht.
/// Beides landet in der Warteschlange, damit nichts verlorengeht; das eine wird vom Sendedienst
/// wiederholt, das andere bleibt mit seinem Grund stehen. Die eingereihte Zeile trägt dabei
/// sofort einen Fehlversuch: Ein abgebrochener POST kann TANSS trotzdem erreicht haben, und der
/// Sendedienst fragt deshalb vor der Wiederholung nach, ob die Fernwartung nicht doch schon
/// dort steht.</para>
///
/// <para><b>Deshalb gibt es drei Ausgänge und nicht zwei.</b> „Später“ reiht die Sitzung ein
/// und gibt sie frei; sie geht mit der automatischen Beschreibung hinaus und bleibt in der
/// Warteschlange sichtbar. „Verwerfen“ lässt sie verschwinden — die seltene, folgenreiche Wahl,
/// und die einzige, die Arbeitszeit kostet; der Protokolleintrag <c>queue.discard</c> bleibt
/// als einzige Spur. Gäbe es allein Speichern und Verwerfen, verschwände die Sitzung beim
/// Verwerfen ganz ohne Spur.</para>
///
/// <para><b>Wer gar nicht antwortet, verliert nichts.</b> Beim Schliessen des Fensters ohne
/// Entscheidung wird die Sitzung eingereiht und wartet dort auf eine Antwort
/// (<see cref="QueuedUpload.AwaitingDecision"/>) — keine Uhr gibt sie frei, und beim nächsten
/// Start wird der Dialog erneut vorgelegt. Ungefragt gebucht wird nichts.</para>
///
/// <para><b>Jeder dieser Ausgänge hinterlässt eine Zeile im Änderungsprotokoll.</b> Bis heute
/// gab es keine — und deshalb liess sich die Frage „ging der Dialog überhaupt auf, und was
/// wurde geantwortet?“ nur über den Umweg des Nutzlasttextes beantworten.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class SaveSessionViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly SessionSnapshot _session;
    private readonly string? _technician;
    private readonly IImmediateBooking? _booking;
    private readonly IRemoteSupportRepository? _supports;

    private bool _disposed;

    /// <summary>
    /// Ist die Sitzung versorgt — gebucht, eingereiht, freigegeben oder verworfen?
    /// </summary>
    /// <remarks>
    /// <b>Die Frage, die beim Schliessen des Fensters zählt.</b> Ist sie mit Nein zu
    /// beantworten, steht die Sitzung nirgends, und <see cref="Dispose"/> reiht sie ein. Ein
    /// Kennzeichen und nicht eine Ableitung aus <see cref="IsBooked"/>: Auch ein
    /// <i>misslungenes</i> Buchen versorgt die Sitzung — sie liegt danach in der
    /// Warteschlange —, und ohne diese Unterscheidung reihte das Schliessen ein zweites Mal
    /// ein und vermerkte eine unbeantwortete Sitzung, die längst beantwortet war.
    /// </remarks>
    private bool _settled;

    /// <summary>Baut den Dialog zu einer beendeten Sitzung.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="closed">Die beendete Sitzung samt dem, was mit ihr geschah.</param>
    /// <param name="typeName">Der sprechende Name des Fernwartungstyps, falls bekannt.</param>
    /// <param name="color">Die Farbe der Anbindung als Hexwert ohne Raute.</param>
    /// <param name="technician">
    /// Der Name des Technikers, oder <see langword="null"/>. Ohne ihn bleibt die Zeile im
    /// Bericht weg — eine Mitarbeiterkennung sagt dem Kunden nichts.
    /// </param>
    /// <param name="booking">
    /// Der Weg, auf dem „In TANSS buchen“ sofort sendet. Bleibt er offen, wird der Sendedienst
    /// der Laufzeit genommen — der Übergabewert ist dafür da, den Dialog ohne Netz prüfbar zu
    /// halten. Er ist ausdrücklich kein zweiter Sendeweg: siehe <see cref="IImmediateBooking"/>.
    /// </param>
    /// <param name="supports">
    /// Der Weg nach TANSS für eine Sitzung, zu der es <b>keine</b> Warteschlangenzeile gibt —
    /// der Regelfall. Bleibt er offen, wird der der Laufzeit genommen; der Übergabewert ist
    /// dafür da, das Buchen prüfen zu können, ohne wirklich eine Fernwartung anzulegen.
    /// </param>
    public SaveSessionViewModel(AppHost host, SessionClosed closed, string? typeName, string? color,
                                string? technician = null,
                                IImmediateBooking? booking = null,
                                IRemoteSupportRepository? supports = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(closed);

        _host = host;
        _session = closed.Session;
        _technician = technician;
        _booking = booking ?? host.Uploads;
        _supports = supports ?? host.Composition?.RemoteSupports;

        Color = color ?? string.Empty;
        Destination = string.IsNullOrWhiteSpace(_session.Destination)
            ? "Kein Ziel ermittelt"
            : _session.Destination;
        Application = _session.ProfileName;

        TypeText = _session.IsMapped
            ? string.Create(CultureInfo.CurrentCulture,
                $"{_session.RemoteSupportTypeId} — {typeName ?? "unbekannter Typ"}")
            : "nicht überwacht";

        // Volle Minuten wie im Bericht darunter. Die tickende Schreibweise gehoert in die Liste
        // der LAUFENDEN Sitzungen; hier ist die Sitzung beendet, und zwei verschiedene
        // Schreibweisen derselben Dauer im selben Fenster lesen sich wie zwei Messwerte.
        DateTimeOffset ended = _session.EndedAt ?? host.Clock.GetLocalNow();
        TimeRange = string.Create(CultureInfo.CurrentCulture,
            $"{Texts.Clock(_session.StartedAt)} – {Texts.Clock(ended)} · "
            + $"{Texts.Minutes(ended - _session.StartedAt)}");

        // Die automatische Beschreibung: aus der Zeile, wenn es eine gibt (Sitzung aus einem
        // frueheren Lauf), sonst aus der Abschrift der Sitzung - dort steht derselbe Text, den
        // SessionWatcherService.PayloadFor gleich in die Nutzlast schreiben wuerde. Sie wird
        // gezeigt, damit klar ist, was in TANSS ankommt, auch wenn niemand etwas dazuschreibt.
        AutoComment = CanAnswer(closed.Disposition)
            ? FindPayload()?.Comment ?? _session.Comment
            : string.Empty;

        _canEdit = CanAnswer(closed.Disposition);

        if (!CanEdit)
        {
            Result = closed.Reason;
        }

        _comment = BuildReport(ended);
    }

    /// <summary>
    /// Lässt sich dieser Ausgang überhaupt beantworten?
    /// </summary>
    /// <remarks>
    /// <para><b>Die Bedingung, an der seit dem Umbau alles hängt.</b> Vorher lautete sie
    /// „liegt eine Warteschlangenzeile vor“ (<see cref="SessionDisposition.Enqueued"/> oder
    /// <see cref="SessionDisposition.AlreadyQueued"/>). Seit beim Sitzungsende keine Zeile mehr
    /// entsteht, wäre das der Weg gewesen, den Dialog zu verlieren: Er ginge zwar auf, böte
    /// aber nichts an.</para>
    /// <para><see cref="SessionDisposition.Held"/> ist seither der Regelfall; die beiden
    /// anderen bleiben für Sitzungen aus einem früheren Lauf, die bereits in der Warteschlange
    /// stehen. Was ohne Zuordnung oder im Trockenlauf endete, ist nicht zu beantworten — dort
    /// gäbe es nichts, worauf gebucht werden könnte, und ein bedienbares Fenster behauptete
    /// eine Möglichkeit, die es nicht gibt.</para>
    /// </remarks>
    /// <param name="disposition">Der Ausgang, mit dem die Sitzung gemeldet wurde.</param>
    /// <returns><see langword="true"/>, wenn der Dialog wirklich etwas ausrichten kann.</returns>
    public static bool CanAnswer(SessionDisposition disposition) =>
        disposition is SessionDisposition.Held
                    or SessionDisposition.Enqueued
                    or SessionDisposition.AlreadyQueued;

    /// <summary>
    /// Der vorausgefüllte Bericht: alles, was das Werkzeug über die Sitzung sicher weiss.
    /// </summary>
    /// <remarks>
    /// <para><b>Vorausgefüllt und nicht bloss vorangestellt.</b> Der Techniker soll den Text
    /// bearbeiten können, bevor er in TANSS steht — dort ist er die Dokumentation, die der
    /// Kunde später bekommt. Ein Block, den das Werkzeug unveränderlich anhängt, liesse sich
    /// nicht kürzen, wenn er im Einzelfall zu viel verrät.</para>
    ///
    /// <para><b>Was bewusst NICHT darin steht:</b> der Fenstertitel. Er trägt Kundennamen,
    /// Rechnernamen und E-Mail-Betreffe, und ob er überhaupt aufbewahrt werden darf, entscheidet
    /// <c>logging.redact_window_titles</c>. Ihn hier ungefragt in einen Text zu setzen, der zum
    /// Kunden geht, ginge an dieser Einstellung vorbei.</para>
    ///
    /// <para>Die letzte Zeile bleibt offen. Sie ist die Einladung, das Einzige zu ergänzen, was
    /// das Werkzeug nicht wissen kann: was tatsächlich getan wurde.</para>
    /// </remarks>
    private string BuildReport(DateTimeOffset ended)
    {
        TimeSpan duration = ended - _session.StartedAt;

        StringBuilder report = new();
        _ = report.AppendLine(CultureInfo.CurrentCulture,
            $"{Application}: {Destination}");
        _ = report.AppendLine();

        // Volle Minuten und kein Prozessname, keine PID: Der Text geht als Dokumentation an den
        // Kunden. Eine PID ist dort ohne Bedeutung und beim naechsten Start ohnehin eine andere;
        // Sekunden behaupten eine Genauigkeit, die diese Messung nicht hat - Anfang und Ende
        // ergeben sich daraus, wann ein Fenster auf- und zuging.
        _ = report.AppendLine(CultureInfo.CurrentCulture,
            $"Zeitraum:     {Texts.Day(_session.StartedAt)} {Texts.Clock(_session.StartedAt)} – "
            + $"{Texts.Clock(ended)} ({Texts.Minutes(duration)})");
        _ = report.AppendLine(CultureInfo.CurrentCulture,
            $"Gegenstelle:  {Destination}");
        _ = report.AppendLine(CultureInfo.CurrentCulture,
            $"Anwendung:    {Application}");
        _ = report.AppendLine(CultureInfo.CurrentCulture,
            $"Arbeitsplatz: {Environment.MachineName} / {Environment.UserName}");

        // Der Name, nicht die Kennung - und lieber gar keine Zeile als eine Zahl: Der Text geht
        // an den Kunden, und "Mitarbeiter 1" bedeutet ihm nichts.
        if (!string.IsNullOrWhiteSpace(_technician))
        {
            _ = report.AppendLine(CultureInfo.CurrentCulture, $"Techniker:    {_technician}");
        }

        _ = report.AppendLine();
        _ = report.AppendLine("Durchgeführte Arbeiten:");
        _ = report.Append("- ");

        return report.ToString();
    }

    /// <summary>Die Gegenstelle.</summary>
    public string Destination { get; }

    /// <summary>Der sprechende Name der Anwendung.</summary>
    public string Application { get; }

    /// <summary>Die Farbe der Anbindung als Hexwert ohne Raute.</summary>
    public string Color { get; }

    /// <summary>Beginn, Ende und Dauer in einer Zeile.</summary>
    public string TimeRange { get; }

    /// <summary>Der Fernwartungstyp im Klartext.</summary>
    public string TypeText { get; }

    /// <summary>Die automatische Beschreibung, die TANSS ohnehin bekommt.</summary>
    public string AutoComment { get; }

    /// <summary>
    /// Lässt sich der Eintrag überhaupt noch ändern?
    /// </summary>
    /// <remarks>
    /// <para>Falsch, wenn die Sitzung gar nicht eingereiht wurde — etwa ohne Zuordnung auf einen
    /// Fernwartungstyp oder im Trockenlauf. Dann zeigt der Dialog, was geschehen ist, und bietet
    /// nichts an, was folgenlos bliebe.</para>
    /// <para><b>Veränderlich, seit aus dem Fenster heraus gebucht wird.</b> Nach einer
    /// geglückten Buchung steht die Fernwartung in TANSS; was dann noch getippt würde, käme dort
    /// nicht mehr an. Die Felder zu sperren ist die ehrliche Antwort darauf — siehe
    /// <see cref="IsBooked"/>.</para>
    /// </remarks>
    [ObservableProperty]
    private bool _canEdit;

    /// <summary>Was der Techniker dazuschreibt.</summary>
    [ObservableProperty]
    private string _comment = string.Empty;

    /// <summary>Die Rückmeldung nach einer Handlung.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string _result = string.Empty;

    /// <summary>Gibt es eine Rückmeldung?</summary>
    public bool HasResult => !string.IsNullOrEmpty(Result);

    /// <summary>
    /// Die Fernwartung steht in TANSS.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Dialog schliesst sich danach ausdrücklich nicht von selbst.</b> Das Fenster
    /// ist bis an den unteren Bildschirmrand gewandert, seine Fussleiste lag gemessen im
    /// Streifen der Taskleiste, und es reisst sich den Vordergrund. Ein einziger beiläufiger
    /// Klick erledigte es damit — und zurück blieb der Eindruck, nie gefragt worden zu sein.
    /// Bleibt das Ergebnis mit der TANSS-Kennung stehen, ist wenigstens danach zu sehen, was
    /// geschehen ist.</para>
    /// <para>Es sperrt zugleich die Eingabefelder: Was jetzt noch getippt würde, käme in TANSS
    /// nicht mehr an.</para>
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private bool _isBooked;

    /// <summary>Gibt es eine Rückmeldung, die <b>kein</b> Erfolg und keine Zwischenmeldung ist?</summary>
    /// <remarks>
    /// Zwei Anzeigen statt einer mit wechselnder Farbe — dieselbe Überlegung wie bei den beiden
    /// Prüfzeilen unter dem Ticketfeld: Eine Bedingungsformatierung bräuchte einen Stil, und
    /// <c>BasedOn</c> auf einen <c>ui:</c>-Typ liess in diesem Haus schon einmal die Navigation
    /// STILL scheitern.
    /// <para>Während gesendet wird, steht in <see cref="Result"/> die Zwischenmeldung. Sie ist
    /// keine Warnung und soll nicht so aussehen.</para>
    /// </remarks>
    public bool HasProblem => HasResult && !IsBooked && !IsBooking;

    /// <summary>Läuft gerade ein Buchungsversuch?</summary>
    /// <remarks>
    /// Sperrt die drei Ausgänge, solange gesendet wird. Ohne das könnte ein zweiter Klick einen
    /// zweiten Versand auf denselben Eintrag auslösen — und TANSS dedupliziert nicht.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private bool _isBooking;

    /// <summary>Ist gerade kein Buchungsversuch unterwegs?</summary>
    public bool IsIdle => !IsBooking;


    /// <summary>Der Dialog ist fertig und kann geschlossen werden.</summary>
    public event EventHandler? Finished;

    // --- Sprachmodell-Unterstützung ----------------------------------------

    /// <summary>Steht die Unterstützung bereit?</summary>
    /// <remarks>
    /// Die Antwort kommt aus <see cref="AiGateway.TryCreate"/> und nicht aus einer eigenen
    /// Prüfung hier: Es soll genau eine Stelle geben, die über das Senden entscheidet.
    /// </remarks>
    public bool IsAiAvailable => _host.Config?.Ai.IsUsable == true;

    /// <summary>
    /// Warum die Unterstützung hier nicht angeboten wird — oder leer, wenn das keiner Erklärung
    /// bedarf.
    /// </summary>
    /// <remarks>
    /// <para><b>Nur für den halbfertigen Zustand.</b> Wer die Einwilligung erteilt hat, hat sich
    /// mit der Sache befasst und erwartet die Schaltflächen an dieser Stelle. Fehlen sie
    /// wortlos, sieht das aus wie ein Fehler — gemeldet als „der Knopf fehlt“, obwohl er
    /// lediglich zugesperrt war.</para>
    /// <para><b>Ohne Einwilligung bleibt es still.</b> Dann ist nichts eingerichtet, niemand
    /// erwartet etwas, und ein Hinweis wäre Werbung für eine Funktion, die Daten aus dem Haus
    /// gibt. Das ist nicht die Aufgabe eines Abschlussdialogs.</para>
    /// <para>Der Satz nennt, was genau fehlt. „Ist abgeschaltet“ und „kein Modell gewählt“
    /// führen zu verschiedenen Handgriffen, und beides ist an derselben Stelle zu erledigen.</para>
    /// </remarks>
    public string AiHint
    {
        get
        {
            if (_host.Config?.Ai is not { } ai || !ai.HasConsent || ai.IsUsable)
            {
                return string.Empty;
            }

            string missing = !ai.Enabled
                ? "ist abgeschaltet"
                : "hat noch kein Modell";

            return $"Rechtschreibprüfung und Ausformulieren: Die Einwilligung liegt vor, die "
                + $"Unterstützung {missing}. Einzuschalten unter „Einstellungen“ bei "
                + $"„Rechtschreibung und Ausformulieren“.";
        }
    }

    /// <summary>Gibt es zur Unterstützung etwas zu sagen?</summary>
    public bool HasAiHint => AiHint.Length > 0;

    /// <summary>Läuft gerade ein Aufruf beim Anbieter?</summary>
    [ObservableProperty]
    private bool _isAiBusy;

    /// <summary>Der Stand der letzten Überarbeitung.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAiStatus))]
    private string _aiStatus = string.Empty;

    /// <summary>Gibt es dazu etwas zu sagen?</summary>
    public bool HasAiStatus => !string.IsNullOrEmpty(AiStatus);

    /// <summary>Lässt sich die letzte Überarbeitung zurücknehmen?</summary>
    [ObservableProperty]
    private bool _canUndoAi;

    private string? _beforeAi;

    /// <summary>Prüft Rechtschreibung, Grammatik und Zeichensetzung.</summary>
    [RelayCommand]
    private Task ProofreadAsync() => ReviseAsync(AiTask.Proofread);

    /// <summary>Formuliert die Stichworte zu einem Bericht aus.</summary>
    [RelayCommand]
    private Task ImproveAsync() => ReviseAsync(AiTask.Improve);

    /// <summary>Nimmt die letzte Überarbeitung zurück.</summary>
    /// <remarks>
    /// Pflicht und kein Komfort: Was ein Modell zurückgibt, ist ein Vorschlag. Ohne einen Weg
    /// zurück wäre der eigene Text nach einem Klick verloren — und der Techniker hat ihn
    /// geschrieben, nicht das Modell.
    /// </remarks>
    [RelayCommand]
    private void UndoAi()
    {
        if (_beforeAi is null)
        {
            return;
        }

        Comment = _beforeAi;
        _beforeAi = null;
        CanUndoAi = false;
        AiStatus = "Die Überarbeitung wurde zurückgenommen.";
    }

    /// <summary>
    /// Schickt den Text an den Anbieter und übernimmt die Antwort.
    /// </summary>
    /// <remarks>
    /// <para>Die Reihenfolge ist der ganze Datenschutz dieser Funktion: erst das Tor fragen,
    /// dann schwärzen, dann senden, dann zurückübersetzen. Wer die Schwärzung nach dem Senden
    /// anwendete, hätte sie gar nicht.</para>
    /// <para>Der vorherige Stand wird aufgehoben, bevor etwas ersetzt wird.</para>
    /// </remarks>
    private async Task ReviseAsync(AiTask task)
    {
        string text = Comment.Trim();

        if (text.Length == 0)
        {
            AiStatus = "Es steht nichts im Feld, was zu prüfen wäre.";
            return;
        }

        using IAiAssistant? assistant = AiGateway.TryCreate(_host.Config, out string reason);

        if (assistant is null)
        {
            AiStatus = reason;
            return;
        }

        AiSection settings = _host.Config!.Ai;
        AiRedaction redaction = AiGateway.BuildRedaction(_session.Destination,
                                                         settings.RedactIdentifiers);

        IsAiBusy = true;
        AiStatus = redaction.Count > 0
            ? $"Wird geprüft — {Texts.Count(redaction.Count, "Name", "Namen")} vorher unkenntlich gemacht."
            : "Wird geprüft …";

        try
        {
            string sent = redaction.Apply(text);
            string answer = await assistant.ReviseAsync(sent, task, settings.Model)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(answer))
            {
                AiStatus = "Der Anbieter hat nichts zurückgegeben. Der Text bleibt, wie er ist.";
                return;
            }

            _beforeAi = Comment;
            Comment = redaction.Restore(answer);
            CanUndoAi = true;

            AiStatus = task == AiTask.Proofread
                ? "Rechtschreibung geprüft. Bitte gegenlesen — zurücknehmen ist möglich."
                : "Ausformuliert. Bitte gegenlesen — zurücknehmen ist möglich.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AiStatus = "Der Anbieter war nicht erreichbar oder hat abgewiesen: "
                + Redaction.Scrub(ex.Message);
        }
        finally
        {
            IsAiBusy = false;
        }
    }

    /// <summary>
    /// Übernimmt Kommentar und Ticket — und bucht die Sitzung sofort in TANSS.
    /// </summary>
    /// <remarks>
    /// <para><b>Zwei Wege, und welcher gilt, entscheidet die Warteschlange.</b> Steht dort eine
    /// Zeile — eine Sitzung aus einem früheren Lauf —, geht der Bericht zuerst in diese Zeile
    /// und danach wird über <see cref="IImmediateBooking"/> gesendet, also über den Sendedienst
    /// samt seiner Existenzprüfung. Bricht das Senden ab, steht der Bericht bereits auf der
    /// Platte; wer erst sendete und dann schriebe, verlöre ihn genau dann, wenn es darauf
    /// ankommt.</para>
    ///
    /// <para><b>Der Regelfall ist der andere: Es gibt keine Zeile.</b> Dann wird die Nutzlast
    /// hier gebaut — über <see cref="SessionWatcherService.PayloadFor"/>, dieselbe Stelle wie
    /// beim Beobachter — und unmittelbar gesendet. Eingereiht wird ausschliesslich, wenn das
    /// misslingt. Eine Existenzprüfung steht davor ausdrücklich nicht: Diese Sitzung war noch
    /// nie draussen, und auch der Sendedienst fragt erst ab dem zweiten Versuch. Ab dem zweiten
    /// Versuch fragt er dann aber wirklich — dafür sorgt der Fehlversuch, den die eingereihte
    /// Zeile sofort mitbekommt.</para>
    ///
    /// <para>Der Text des Feldes <b>ersetzt</b> den Kommentar. Er ist beim Öffnen bereits mit
    /// dem vollständigen Bericht gefüllt — Zeitraum, Gegenstelle, Anwendung, Arbeitsplatz —,
    /// und was davon in TANSS landen soll, entscheidet der Techniker durch Bearbeiten. Wird das
    /// Feld leer geräumt, bleibt die automatische Beschreibung stehen: Ein Eintrag ganz ohne
    /// Kommentar wäre in TANSS nicht mehr zuzuordnen.</para>
    ///
    /// <para><b>Die Ticketprüfung hält hier nichts an — auch nicht bei „gibt es nicht“.</b>
    /// Sie fragt TANSS, und eine Leitung, die gerade aussetzt, darf den eben geschriebenen
    /// Bericht nicht kosten. Die Prüfung steht sichtbar unter dem Feld
    /// entscheiden tut der Techniker. Abgewiesen wird allein, was
    /// gar keine Nummer ist — das ist eine Feststellung des Werkzeugs, keine Auskunft von
    /// TANSS, und ohne sie fiele die Eingabe still auf „kein Ticket“.</para>
    /// </remarks>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBooked || IsBooking)
        {
            return;
        }

        if (!CanEdit)
        {
            Result = "Diese Sitzung ist nicht zu buchen. Warum, steht oben in diesem Fenster.";
            return;
        }

        if (!TryReadRow(out QueuedUpload? row))
        {
            // "Nicht gefunden" und "nicht lesbar" sehen gleich aus und bedeuten das Gegenteil.
            // Bei einer unlesbaren Warteschlange jetzt zu senden hiesse, im Blindflug zu
            // buchen: Es koennte laengst eine Zeile geben, die schon draussen war - und TANSS
            // dedupliziert nicht.
            Result = "Die Warteschlange liess sich nicht lesen; ob zu dieser Sitzung dort etwas "
                + "steht, ist damit ungeklärt. Es wird ausdrücklich nicht gesendet — der "
                + "Berichtstext bleibt hier stehen. Unter „Warteschlange“ steht, was los ist.";
            return;
        }

        if (Compose(row?.Payload) is not { } payload)
        {
            Result = "Zu dieser Sitzung lässt sich keine Fernwartung bilden: Es ist kein "
                + "Fernwartungstyp zugeordnet, oder die Laufzeit ist nicht eingerichtet. "
                + "Gebucht wird nichts, und der Berichtstext bleibt hier stehen.";
            return;
        }

        if (row is not null)
        {
            // Der Bericht ZUERST auf die Platte, dann senden. Bricht das Senden ab, geht er mit
            // dem naechsten Versuch trotzdem hinaus.
            try
            {
                if (FindQueue() is not { } queue || !queue.UpdatePayload(payload))
                {
                    Result = "Der Eintrag wartet nicht mehr — der Sendedienst hat ihn inzwischen "
                        + "übernommen. Geändert wurde nichts; in TANSS steht die automatische "
                        + "Beschreibung.";
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Result = "Das Ändern ist fehlgeschlagen: " + Redaction.Scrub(ex.Message);
                return;
            }

            await BookQueuedAsync().ConfigureAwait(true);
            return;
        }

        await BookDirectAsync(payload).ConfigureAwait(true);
    }

    /// <summary>
    /// Setzt die Nutzlast zusammen, die gleich nach TANSS geht.
    /// </summary>
    /// <remarks>
    /// <para>Grundlage ist die vorhandene Zeile, wenn es eine gibt, und sonst die Abschrift der
    /// Sitzung über <see cref="SessionWatcherService.PayloadFor"/> — dieselbe Stelle, an der
    /// auch der Beobachter baut. Zwei Rechenwege ergäben zwei Fernwartungen, die sich in
    /// Gerätekennung oder Zeitraum unterscheiden, und genau diese Felder nimmt die
    /// Existenzprüfung gegen Dubletten.</para>
    /// <para>Der Text des Feldes <b>ersetzt</b> den Kommentar; ein leer geräumtes Feld lässt
    /// die automatische Beschreibung stehen. Ein Eintrag ganz ohne Kommentar wäre in TANSS
    /// nicht mehr zuzuordnen.</para>
    /// </remarks>
    /// <param name="existing">Die Nutzlast der vorhandenen Zeile, oder <see langword="null"/>.</param>
    /// <returns>Die Nutzlast, oder <see langword="null"/>, wenn keine zu bilden ist.</returns>
    private RemoteSupportWrite? Compose(RemoteSupportWrite? existing)
    {
        if ((existing ?? BasePayload()) is not { } basis)
        {
            return null;
        }

        string text = Comment.Trim();

        // NUR der Bericht. Firma, Ticket und Geraet setzt dieses Fenster ausdruecklich NICHT:
        // Was daran haengt, ist in TANSS der Weg zur Leistung und damit zur Rechnung, und in
        // einen fakturierenden Vorgang greift dieses Werkzeug nicht hinein. Die Firma setzt
        // TANSS selbst, sofern zur Geraetekennung eine Zuordnung hinterlegt ist; alles Weitere
        // entscheidet der Techniker dort, wo die Regeln dafuer stehen.
        return basis with
        {
            Comment = text.Length == 0 ? basis.Comment : text,
        };
    }

    /// <summary>
    /// Sendet eine Sitzung, zu der es <b>keine</b> Zeile gibt — der Regelfall.
    /// </summary>
    /// <remarks>
    /// <para><b>Direkt und ohne Umweg über die Warteschlange.</b> Das ist die Anweisung, um die
    /// dieser Umbau geht: Erst buchen, und nur bei einem Fehlschlag einreihen. Gerufen wird
    /// derselbe Aufruf, den auch der Sendedienst macht —
    /// <c>CreateWithDiagnosticsAsync</c> und nicht <c>CreateAsync</c>, damit die Gegenprobe der
    /// Mitarbeiterzuordnung nicht verlorengeht: Eine Fernwartung, die bei TANSS auf einem
    /// fremden Mitarbeiter landet, ist falsch gebuchte Arbeitszeit, und sie fällt niemandem
    /// auf.</para>
    ///
    /// <para><b>Hausregel 5, und hier mit Nachdruck:</b> Was schiefgeht, kostet die Buchung und
    /// nicht die Sitzung. Jeder Fehlerausgang reiht ein — auch der, den kein Warten heilt.</para>
    /// </remarks>
    /// <param name="payload">Die fertige Nutzlast.</param>
    private async Task BookDirectAsync(RemoteSupportWrite payload)
    {
        if (_supports is null)
        {
            // Ohne Laufzeit gibt es weder einen Sendeweg noch eine Warteschlange. Die Sitzung
            // ist deswegen nicht fort: Ihre Zeile in open_sessions steht noch, und der naechste
            // Start legt sie erneut vor.
            Result = "Es ist keine Verbindung zu TANSS eingerichtet; gesendet werden kann "
                + "nichts. Die Sitzung ist nicht verloren — sie wird beim nächsten Start "
                + "erneut vorgelegt.";
            LogDialog("dialog.deferred", SessionOutcome.Deferred, Result);
            return;
        }

        IsBooking = true;

        // Sperrt zugleich den Knopf, der an CanEdit haengt: Ein zweiter Klick waehrend des
        // Sendens waere ein zweiter POST auf dieselbe Sitzung - und TANSS dedupliziert nicht.
        CanEdit = false;
        Result = "Wird nach TANSS gesendet …";

        // Ob sich das Fenster von selbst schliessen darf. Gesetzt wird das erst, wenn TANSS
        // GEANTWORTET hat - nicht beim Klick. Ein Fenster, das auf den Klick hin zugeht, sagt
        // "erledigt", waehrend die Leitung noch laeuft; misslingt der Versand danach, ist die
        // Sitzung eingereiht und niemand hat es gesehen.
        bool schliessen = false;

        try
        {
            RemoteSupportCreateResult result = await _supports
                .CreateWithDiagnosticsAsync(payload).ConfigureAwait(true);

            _settled = true;
            IsBooked = true;
            CanEdit = false;

            Result = result.AttributionConfirmed
                ? string.Create(CultureInfo.CurrentCulture,
                    $"Hochgeladen; TANSS führt sie unter der Kennung {result.Support.Id}. In "
                    + $"der Warteschlange steht sie nicht — sie musste dort nie hinein.")
                : string.Create(CultureInfo.CurrentCulture,
                    $"Hochgeladen unter der Kennung {result.Support.Id} — die Zuordnung zum "
                    + $"Mitarbeiter hat TANSS aber nicht bestätigt. Nachzusehen ist, auf wen "
                    + $"die Fernwartung dort gebucht ist.");

            // Glatt gebucht heisst: Das Fenster hat nichts mehr zu sagen und geht. Der Satz
            // oben steht dann ungelesen da, und das ist richtig so - er sagt nur, was ohnehin
            // geschehen ist. Nachzulesen bleibt er im Verlauf.
            //
            // NICHT bei unbestaetigter Zuordnung: Dann ist die Fernwartung zwar in TANSS, aber
            // moeglicherweise auf einem fremden Mitarbeiter - falsch gebuchte Arbeitszeit, die
            // niemandem auffaellt. Dieser eine Satz muss gelesen werden, also bleibt das
            // Fenster stehen, bis jemand es schliesst.
            schliessen = result.AttributionConfirmed;

            LogDialog("dialog.booked", SessionOutcome.Ok, Result, result.Support.Id);
            RecordBooked(result.Support.Id);

            // Erst ganz zum Schluss: Solange diese Zeile steht, wuerde ein Absturz die Sitzung
            // erneut vorlegen - und das ist genau richtig, bis feststeht, dass sie in TANSS ist.
            ClearOpenSession();
        }
        catch (TanssException ex)
        {
            Defer(payload, Redaction.Scrub(ex.Message), RetryPolicy.IsTransient(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ein Fehler, den niemand eingeordnet hat, gilt als heilbar: Er koennte ein
            // Netzwackler gewesen sein, und die Zeile wird ohnehin erst nach der
            // Existenzpruefung wiederholt.
            Defer(payload, "Unerwarteter Fehler beim Senden: " + Redaction.Scrub(ex.Message),
                  heilbar: true);
        }
        finally
        {
            IsBooking = false;
        }

        if (schliessen)
        {
            // Erst hier, ausserhalb des finally: Das Schliessen raeumt das Ansichtsmodell ab
            // (Dispose haengt am Closed des Fensters), und danach duerfte IsBooking nicht mehr
            // gesetzt werden. Jeder Fehlerausgang laesst schliessen auf false - ein Fenster,
            // das nach einem Fehlschlag zugeht, nimmt die Begruendung mit.
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Reiht eine Sitzung ein, deren Buchung misslungen ist — und sagt, was davon zutrifft.
    /// </summary>
    /// <remarks>
    /// <para><b>Der einzige Weg, auf dem im Regelfall etwas in die Warteschlange kommt.</b>
    /// Eingereiht wird in beiden Fällen, auch bei einem Fehler, den kein Warten heilt: Eine
    /// Sitzung, die nirgends steht, ist Arbeitszeit, die niemand mehr belegen kann. Der
    /// Unterschied steht im Zustand der Zeile und im Satz an den Techniker — wiederholt wird
    /// nur, was sich von selbst erledigen kann.</para>
    ///
    /// <para><b>Die Zeile bekommt sofort einen Fehlversuch angerechnet, und das ist der
    /// wichtigste Griff hier.</b> Ein abgebrochener POST heisst nicht, dass TANSS nichts getan
    /// hat — die Anfrage kann angekommen und nur die Antwort verlorengegangen sein. Der
    /// Sendedienst stellt seine Existenzprüfung vor jede Wiederholung, aber nur, wenn er einen
    /// vorherigen Versuch sieht. Eine frisch eingereihte Zeile ohne diesen Vermerk ginge beim
    /// nächsten Takt ungeprüft hinaus — und das wäre die Dublette, die sich nur noch per
    /// direktem Datenbankzugriff entfernen lässt.</para>
    /// </remarks>
    /// <param name="payload">Die Nutzlast, die nicht durchkam.</param>
    /// <param name="message">Die geschwärzte Meldung.</param>
    /// <param name="heilbar">Kann ein späterer Versuch das heilen?</param>
    private void Defer(RemoteSupportWrite payload, string message, bool heilbar)
    {
        string reason = heilbar
            ? message + " Die Sitzung ist jetzt in der Warteschlange und wird vom Sendedienst "
              + "erneut versucht — vorher fragt er nach, ob sie nicht doch schon in TANSS "
              + "steht. Verloren ist nichts."
            : message + " Dieser Fehler heilt nicht durch Warten. Die Sitzung ist trotzdem in "
              + "die Warteschlange aufgenommen, damit sie nicht verlorengeht; von selbst wird "
              + "sie nicht mehr versucht und ist nach dem Beheben der Ursache unter "
              + "„Warteschlange“ von Hand zu klären.";

        if (FindQueue() is not { } queue)
        {
            CanEdit = true;
            Result = message + " Einreihen war nicht möglich — die Zustandsdatenbank ist gerade "
                + "nicht zu erreichen. Die Sitzung wird beim nächsten Start erneut vorgelegt.";
            LogDialog("dialog.deferred", SessionOutcome.Error, Result);
            return;
        }

        try
        {
            if (!queue.Enqueue(payload))
            {
                // Die Kennung ist bereits bekannt - erledigt, unterwegs oder wartend. Hier
                // nichts anzufassen ist Absicht: Ein MarkFailed auf eine abgehakte Zeile
                // stellte eine gebuchte Fernwartung wieder auf "zu senden".
                CanEdit = false;
                Result = message + " Die Sitzung steht bereits in der Warteschlange; was aus "
                    + "ihr geworden ist, steht unter „Warteschlange“.";
                LogDialog("dialog.deferred", SessionOutcome.Deferred, Result);
                _settled = true;
                return;
            }

            queue.MarkFailed(payload.RemoteMaintenanceId, reason,
                heilbar
                    ? Backoff.NextAttemptAfter(_host.Clock.GetLocalNow(), attempt: 1)
                    : null);

            // Die Sitzung ist jetzt in der Warteschlange gesichert; die Zeile der laufenden
            // Sitzung hat ihren Zweck erfuellt. Bliebe sie stehen, legte der naechste Start
            // dieselbe Sitzung ein zweites Mal vor.
            ClearOpenSession();

            _settled = true;
            _ = _host.Uploads.RefreshQueue();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CanEdit = true;
            Result = message + " Auch das Einreihen ist fehlgeschlagen ("
                + Redaction.Scrub(ex.Message) + "). Die Sitzung wird beim nächsten Start erneut "
                + "vorgelegt.";
            LogDialog("dialog.deferred", SessionOutcome.Error, Result);
            return;
        }

        // Erneut versuchen ist erlaubt, solange der Fehler heilen kann: Der naechste Versuch
        // laeuft dann ueber die Zeile - und die traegt jetzt einen Fehlversuch, also fragt der
        // Sendeweg vorher nach, ob die Fernwartung nicht doch schon in TANSS steht.
        CanEdit = heilbar;
        Result = reason;

        LogDialog(heilbar ? "dialog.deferred" : "dialog.failed",
                  heilbar ? SessionOutcome.Deferred : SessionOutcome.Error, reason);
    }

    /// <summary>
    /// Sendet die wartende Zeile und sagt, was daraus geworden ist.
    /// </summary>
    /// <remarks>
    /// Der Weg für eine Sitzung, die bereits in der Warteschlange steht — eine aus einem
    /// früheren Lauf. Gesendet wird über <see cref="IImmediateBooking"/> und damit über den
    /// Sendedienst: dieselbe Existenzprüfung, dieselben Quittungen, derselbe Protokolleintrag.
    /// <para>Hausregel 5: Was hier schiefgeht, kostet die Buchung und nicht die Sitzung. Die
    /// Zeile steht in jedem Fall weiter in der Warteschlange — entweder wartend, oder mit
    /// Rückstau im gewöhnlichen Wiederholungsweg.</para>
    /// </remarks>
    private async Task BookQueuedAsync()
    {
        // Ab hier ist die Sitzung in jedem Fall versorgt: Die Zeile steht, wie dieser Versuch
        // auch ausgeht. Das Schliessen des Fensters hat danach nichts mehr einzureihen.
        _settled = true;

        IsBooking = true;

        // Sperrt zugleich den Knopf, der an CanEdit haengt: Ein zweiter Klick waehrend des
        // Sendens waere ein zweiter Versand auf dieselbe Zeile. Misslingt es, wird die Sperre
        // unten wieder aufgehoben - der Techniker soll es erneut versuchen koennen.
        CanEdit = false;
        Result = "Wird nach TANSS gesendet …";

        UploadReport? report;
        try
        {
            report = _booking is null
                ? null
                : await _booking.BookNowAsync(_session.SessionId).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Der Bericht steht bereits in der Zeile, und die Zeile steht noch auf "pending"
            // oder ist ueber MarkFailed in den Wiederholungsweg gefallen. Verloren ist nichts;
            // gebucht ist aber auch nichts, und genau das steht hier.
            CanEdit = true;
            Result = "Das Buchen ist fehlgeschlagen: " + Redaction.Scrub(ex.Message)
                + " Der Bericht ist gespeichert; die Sitzung bleibt in der Warteschlange und "
                + "wird vom Sendedienst erneut versucht.";
            LogDialog("dialog.deferred", SessionOutcome.Deferred, Result);
            return;
        }
        finally
        {
            IsBooking = false;
        }

        if (report is null)
        {
            // Entweder ist die Laufzeit nicht zusammengebaut, oder die Zeile wartete nicht
            // mehr. Beides heisst: von hier aus wird jetzt nichts gesendet - und zwar
            // ausdruecklich nicht "sicherheitshalber doch", denn ein zweiter Versand waere die
            // Dublette, die TANSS nicht abfaengt.
            // CanEdit bleibt hier aus: An dieser Zeile ist nichts mehr zu aendern, und ein
            // erneuter Klick auf "buchen" faende nichts, was zu buchen waere.
            Result = "Der Eintrag wartet nicht mehr — er ist unterwegs, bereits gesendet oder "
                + "aufgegeben. Der Bericht ist gespeichert. Was aus ihm geworden ist, steht "
                + "unter „Warteschlange“.";
            LogDialog("dialog.deferred", SessionOutcome.Deferred, Result);
            return;
        }

        switch (report.Outcome)
        {
            case UploadOutcome.Uploaded:
            case UploadOutcome.AlreadyPresent:
                IsBooked = true;
                CanEdit = false;
                Result = report.Reason;
                LogDialog("dialog.booked", SessionOutcome.Ok, report.Reason,
                          report.TanssSupportId);
                break;

            case UploadOutcome.Deferred:
            case UploadOutcome.OutcomeUnknown:
                // Erneut versuchen ist erlaubt: Die Zeile steht wieder auf "wartend" - mit
                // Rueckstau -, und der naechste Versuch fragt wegen attempts > 0 vorher nach,
                // ob die Fernwartung nicht doch schon in TANSS steht.
                CanEdit = true;
                Result = report.Reason + " Der Bericht ist gespeichert; die Sitzung bleibt in "
                    + "der Warteschlange und wird vom Sendedienst erneut versucht. Verloren "
                    + "ist nichts.";
                LogDialog("dialog.deferred", SessionOutcome.Deferred, Result);
                break;

            default:
                // Aufgegeben heisst nicht weggeworfen: Die Zeile bleibt mit ihrem Grund unter
                // "Warteschlange" stehen und ist von Hand zu klaeren.
                Result = report.Reason;
                LogDialog("dialog.failed", SessionOutcome.Error, report.Reason);
                break;
        }
    }

    /// <summary>Reiht die Sitzung ein; sie geht mit der automatischen Beschreibung hinaus.</summary>
    /// <remarks>
    /// <para>Der Ausgang für „jetzt gerade nicht“ — und ausdrücklich kein Verlust: Die Sitzung
    /// steht danach in der Warteschlange, ist dort sichtbar und geht mit dem nächsten Sendelauf
    /// hinaus.</para>
    /// <para><b>Hier entsteht die Zeile, wenn es noch keine gibt</b> — der Regelfall, seit das
    /// Sitzungsende nichts mehr einreiht. Gibt es sie schon (Sitzung aus einem früheren Lauf),
    /// wird sie zurückgehalten.</para>
    ///
    /// <para><b>„Später“ parkt und sendet nicht.</b> Das war einmal anders: Die Zeile wurde
    /// freigegeben, war damit sofort fällig und ging im nächsten Takt hinaus — gemessen eine
    /// Sekunde nach dem Klick, mit der automatischen Beschreibung. In der Warteschlange war
    /// nichts zu sehen, weil nichts liegenblieb. Eine Schaltfläche namens „Später“, die sofort
    /// sendet, sagt das Gegenteil dessen, was sie tut.</para>
    ///
    /// <para><b>Was das kostet, steht dazu:</b> Eine geparkte Sitzung geht von selbst nie
    /// hinaus — auch nicht nach einer Frist. Wer nie zurückkehrt, hat unverbuchte Arbeitszeit
    /// liegen. Sichtbar ist sie in der Warteschlange, und zwei Wege führen hinaus: „Ändern“
    /// holt den Dialog zurück, „Jetzt senden“ schickt sie mit der automatischen Beschreibung.</para>
    ///
    /// <para><b>Der getippte Bericht geht dabei nicht mit.</b> Parken heisst „noch nicht
    /// entschieden“; wer den Bericht nach TANSS bringen will, bucht — oder trägt ihn später
    /// über „Ändern“ nach.</para>
    /// </remarks>
    [RelayCommand]
    private void Later()
    {
        if (IsBooked || IsBooking || !CanEdit || FindQueue() is not { } queue)
        {
            Finished?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!TryReadRow(out QueuedUpload? row))
        {
            Result = "Die Warteschlange liess sich nicht lesen; eingereiht wurde nichts. Die "
                + "Sitzung wird beim nächsten Start erneut vorgelegt.";
            return;
        }

        try
        {
            if (row is null)
            {
                if (BasePayload() is not { } payload)
                {
                    Result = "Zu dieser Sitzung lässt sich keine Fernwartung bilden; eingereiht "
                        + "wurde nichts.";
                    return;
                }

                // Die ROHE Nutzlast und nicht die aus dem Fenster zusammengesetzte: Was im
                // Feld steht, ist noch nicht entschieden - sonst waere gebucht worden.
                //
                // MIT awaitDecision, und das ist der Punkt: "Spaeter" PARKT. Vorher gab es die
                // Zeile frei, sie war damit sofort faellig und ging im naechsten Takt hinaus -
                // gemessen eine Sekunde nach dem Klick. In der Warteschlange war nichts zu
                // sehen, weil nichts liegenblieb, und eine Schaltflaeche namens "Spaeter", die
                // SOFORT sendet, sagt das Gegenteil dessen, was sie tut.
                if (!queue.Enqueue(payload, awaitDecision: true))
                {
                    Result = "Die Sitzung steht bereits in der Warteschlange; was aus ihr "
                        + "geworden ist, steht unter „Warteschlange“.";
                    _settled = true;
                    Finished?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
            else if (!queue.Hold(_session.SessionId))
            {
                Result = "Der Eintrag wartet nicht mehr — er ist unterwegs, bereits gesendet "
                    + "oder aufgegeben. Zurückzuhalten war nichts mehr; was aus ihm geworden "
                    + "ist, steht unter „Warteschlange“.";
                return;
            }

            // Die Sitzung ist jetzt in der Warteschlange; die Zeile der laufenden Sitzung
            // haette sonst beim naechsten Start ein zweites Mal dieselbe Sitzung vorgelegt.
            ClearOpenSession();
            _settled = true;

            LogDialog("dialog.later", SessionOutcome.Deferred,
                "Im Abschlussdialog auf „Später“ gesetzt: Die Sitzung steht jetzt in der "
                + "Warteschlange und wartet dort auf eine Entscheidung. Von selbst geht sie "
                + "NICHT hinaus — weder jetzt noch nach einer Frist. Über die Schaltfläche "
                + "„Ändern“ in der Warteschlange lässt sich der Bericht nachtragen und buchen; "
                + "„Jetzt senden“ schickt sie mit der automatischen Beschreibung hinaus.");

            _ = _host.Uploads.RefreshQueue();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Verloren ist nichts - die Sitzung steht weiter als laufende Sitzung auf der
            // Platte und wird beim naechsten Start vorgelegt -, hinausgegangen aber auch
            // nichts. Das zu verschweigen hiesse, eine Buchung zu behaupten, die nicht
            // stattfindet.
            Result = "Das Einreihen ist fehlgeschlagen: " + Redaction.Scrub(ex.Message)
                + " Die Sitzung wird beim nächsten Start erneut vorgelegt.";
            return;
        }

        Finished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Schliesst das Fenster, nachdem gebucht wurde.</summary>
    /// <remarks>
    /// Der einzige Ausgang nach einer geglückten Buchung. „Später“ und „Verwerfen“ hätten dann
    /// nichts mehr zu greifen — die Zeile ist abgehakt, und in TANSS steht die Fernwartung.
    /// </remarks>
    [RelayCommand]
    private void Close() => Finished?.Invoke(this, EventArgs.Empty);

    /// <summary>Lässt die Sitzung fallen.</summary>
    /// <remarks>
    /// <para><b>Im Regelfall gibt es hier gar nichts mehr zu entfernen.</b> Die Sitzung steht
    /// in keiner Warteschlange — dieses Fenster hält sie. Verworfen heisst dann: Sie wird nicht
    /// eingereiht, sie geht nicht nach TANSS, und der Vermerk in <c>open_sessions</c> fällt
    /// weg, damit der nächste Start sie nicht doch wieder vorlegt. Was bleibt, ist der
    /// Protokolleintrag <c>queue.discard</c> — die einzige Spur einer Arbeitszeit, die gewollt
    /// verschwindet.</para>
    /// <para>Steht doch eine Zeile da (Sitzung aus einem früheren Lauf), wird sie entfernt.
    /// Nach einer geglückten Buchung gibt es nichts mehr zu verwerfen: In TANSS steht die
    /// Fernwartung. Der Knopf ist dann nicht mehr zu sehen; der Riegel hier steht trotzdem,
    /// weil ein Befehl auch ohne seinen Knopf ausgelöst werden kann.</para>
    /// </remarks>
    [RelayCommand]
    private void Discard()
    {
        if (IsBooked || IsBooking)
        {
            return;
        }

        if (CanEdit && FindQueue() is { } queue)
        {
            if (!TryReadRow(out QueuedUpload? row))
            {
                Result = "Die Warteschlange liess sich nicht lesen; ob dort eine Zeile zu "
                    + "dieser Sitzung steht, ist ungeklärt. Verworfen wurde nichts.";
                return;
            }

            try
            {
                if (row is not null && !queue.Remove(_session.SessionId))
                {
                    // Entfernt wird nur, was auf "pending" steht. Schlaegt das fehl, steht die
                    // Zeile auf unterwegs, gesendet ODER aufgegeben - und bei "aufgegeben" ist
                    // in TANSS gerade nichts angelegt. Diese Faelle zusammenzuziehen, hiesse dem
                    // Techniker zu sagen, die Arbeit sei gebucht, waehrend sie liegengeblieben ist.
                    Result = "Der Eintrag liess sich nicht entfernen: Er wartet nicht mehr, "
                        + "sondern ist unterwegs, bereits gesendet oder aufgegeben. Was davon "
                        + "zutrifft — und ob er in TANSS angekommen ist — steht unter "
                        + "„Warteschlange“.";
                    return;
                }

                // Ohne diesen Griff legte die Wiederherstellung beim naechsten Start genau die
                // Sitzung erneut vor, die der Techniker gerade verworfen hat.
                ClearOpenSession();
                _settled = true;

                LogDiscard(row is not null);
                RecordDiscarded();
                _ = _host.Uploads.RefreshQueue();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Result = "Das Verwerfen ist fehlgeschlagen: " + Redaction.Scrub(ex.Message);
                return;
            }
        }

        Finished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Hält fest, wie der Dialog beantwortet wurde.
    /// </summary>
    /// <remarks>
    /// <para><b>Diese Zeilen haben bis heute gefehlt</b> — und das war teuer: Ob der
    /// Abschlussdialog überhaupt aufging und was jemand geantwortet hat, liess sich nur noch
    /// indirekt erschliessen, nämlich daran, ob in der Nutzlast der Berichtstext oder die
    /// automatische Beschreibung stand. Eine Zeile <c>dialog.shown</c> / <c>dialog.booked</c> /
    /// <c>dialog.later</c> beantwortet dieselbe Frage in einem Blick.</para>
    ///
    /// <para>Abgeriegelt wie jeder Protokolleintrag dieses Hauses (Hausregel 5): Der Vorgang
    /// ist geschehen und wird durch einen misslungenen Vermerk nicht ungeschehen.</para>
    /// </remarks>
    /// <param name="operation">Der Vorgang, etwa <c>dialog.booked</c>.</param>
    /// <param name="outcome">Wie es ausging.</param>
    /// <param name="reason">Der Klartextgrund.</param>
    /// <param name="tanssSupportId">Die von TANSS vergebene Kennung, falls eine entstand.</param>
    private void LogDialog(string operation, SessionOutcome outcome, string reason,
                           int? tanssSupportId = null)
    {
        if (_host.Composition is not { } composition)
        {
            return;
        }

        try
        {
            _ = composition.Log.Append(new SessionLogEntry
            {
                Operation = operation,
                Outcome = outcome,
                Reason = reason,
                Trigger = SessionTrigger.Manual,
                RemoteMaintenanceId = _session.SessionId,
                TanssSupportId = tanssSupportId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Ein nicht geschriebener Protokolleintrag darf weder die Buchung
            // noch das Schliessen des Fensters kosten.
        }
    }

    /// <summary>
    /// Hält fest, dass eine Sitzung verworfen wurde.
    /// </summary>
    /// <remarks>
    /// <para><b>Das Verwerfen ist der einzige Ausgang dieses Fensters, der Arbeitszeit
    /// verschwinden lässt</b> — und ohne diesen Eintrag verschwände sie spurlos: Die Zeile in
    /// der Warteschlange ist gerade entfernt worden, und in TANSS ist nie etwas angekommen.
    /// Wer am Monatsende eine Lücke sucht, findet so wenigstens, dass sie gewollt war, wann
    /// sie entstand und wie lang sie ist.</para>
    ///
    /// <para>Die Gegenstelle wandert ausdrücklich in <see cref="SessionLogEntry.WindowTitle"/>
    /// und nicht in den Begründungstext: Sie stammt aus einer Fensterbeschriftung, und nur
    /// dieses Feld unterliegt <c>logging.redact_window_titles</c>. Die Beobachtung hält es
    /// genauso — dieselbe Sitzung soll in beiden Einträgen gleich weit erkennbar sein.</para>
    /// </remarks>
    /// <param name="hadRow">
    /// Stand die Sitzung in der Warteschlange? Der Satz muss das unterscheiden: „die Zeile ist
    /// entfernt worden“ und „es gab nie eine“ sind zwei verschiedene Sachverhalte, und seit dem
    /// Umbau ist der zweite der Regelfall.
    /// </param>
    private void LogDiscard(bool hadRow)
    {
        if (_host.Composition is not { } composition)
        {
            return;
        }

        DateTimeOffset ended = _session.EndedAt ?? _host.Clock.GetLocalNow();

        string whereabouts = hadRow
            ? "der Eintrag wartete noch und ist entfernt worden."
            : "sie stand in keiner Warteschlange, es war also nichts zu entfernen.";

        try
        {
            _ = composition.Log.Append(new SessionLogEntry
            {
                Operation = "queue.discard",
                Outcome = SessionOutcome.Skipped,
                Reason = string.Create(CultureInfo.CurrentCulture,
                    $"Im Abschlussdialog verworfen: {_session.ProfileName}, "
                    + $"{Texts.Minutes(ended - _session.StartedAt)} ab "
                    + $"{Texts.Clock(_session.StartedAt)}. Die Sitzung geht nicht nach TANSS "
                    + $"und ist dort auch nicht angekommen — {whereabouts}"),
                Trigger = SessionTrigger.Manual,
                RemoteMaintenanceId = _session.SessionId,
                WindowTitle = _session.Destination,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Ein nicht geschriebener Protokolleintrag darf das Verwerfen nicht
            // ruecknehmen - die Zeile ist fort, und der Dialog soll sich schliessen lassen.
        }
    }

    /// <summary>
    /// Hält im Verlauf fest, dass die Sitzung verworfen wurde.
    /// </summary>
    /// <remarks>
    /// <para><b>Getrennt von <see cref="LogDiscard"/> und mit eigenem Riegel</b> (Hausregel 5).
    /// Es sind zwei Tabellen und zwei Gründe, warum eine davon gerade nicht zu schreiben ist;
    /// ein gemeinsamer <c>try</c> liesse den zweiten Vermerk am ersten scheitern.</para>
    ///
    /// <para>Der Verlauf führt die Sitzung schon seit ihrem Abschluss als „eingereiht“ — der
    /// Beobachter hat sie dort eingetragen, bevor dieses Fenster aufging. Hier wird also nichts
    /// angelegt, sondern eine bestehende Zeile fortgeschrieben. Bleibt sie aus, stünde im
    /// Verlauf für immer „geht mit dem nächsten Sendelauf hinaus“ über einer Sitzung, die nie
    /// hinausgegangen ist.</para>
    ///
    /// <para>Eine bereits gebuchte Zeile stuft <c>MarkDiscarded</c> nicht zurück. Das ist kein
    /// Sonderfall dieses Dialogs, sondern der Regelfall eines Wettlaufs: Wird der Eintrag
    /// gerade gesendet, scheitert <see cref="IUploadQueue.Remove"/> ohnehin und es kommt gar
    /// nicht bis hierher.</para>
    /// </remarks>
    private void RecordDiscarded()
    {
        if (_host.Composition is not { } composition || !composition.Config.History.Enabled)
        {
            return;
        }

        try
        {
            _ = composition.History.MarkDiscarded(_session.SessionId,
                "Vom Techniker im Abschlussdialog verworfen. Die Sitzung ist nicht nach TANSS "
                + "gegangen und dort auch nicht angekommen — gesendet wurde nie etwas.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Ein nicht geschriebener Verlaufsvermerk darf das Verwerfen nicht
            // ruecknehmen - die Warteschlangenzeile ist fort, und der Dialog soll sich
            // schliessen lassen.
        }
    }

    /// <summary>Rettet eine unbeantwortete Sitzung und gibt die Zuordnung frei.</summary>
    /// <remarks>
    /// <para><b>Hier steht die Antwort auf „was, wenn niemand antwortet und das Werkzeug
    /// beendet wird?“</b> Gerufen wird diese Stelle beim Schliessen des Fensters — vom Kreuz
    /// rechts oben ebenso wie vom geordneten Beenden, das jedes Fenster zumacht. Ist die
    /// Sitzung dann nicht versorgt, wird sie <b>eingereiht</b>, und zwar wartend: Kein
    /// Zeitablauf gibt sie frei, der nächste Start legt den Dialog erneut vor.</para>
    ///
    /// <para><b>Warum eingereiht und nicht gebucht.</b> Ungefragt zu buchen wäre genau der
    /// Befund, aus dem dieser Umbau entstanden ist — die Sitzung ginge mit der automatischen
    /// Beschreibung hinaus, ohne Firma, ohne Ticket, ohne Bericht. Und warum nicht einfach
    /// liegenlassen: Der Vermerk der laufenden Sitzung allein trüge zwar über einen Neustart,
    /// aber das geordnete Beenden ist der Augenblick, in dem sich sicher etwas tun lässt. Eine
    /// Zeile in der Warteschlange ist sichtbar, zählt im Rückstau mit und lässt sich von Hand
    /// senden oder verwerfen — ein Vermerk in <c>open_sessions</c> ist nichts davon.</para>
    ///
    /// <para><b>Wird gerade gesendet, bekommt die Zeile sofort einen Fehlversuch.</b> Dann ist
    /// ungeklärt, ob TANSS den Datensatz angenommen hat, und der Sendeweg muss vor dem nächsten
    /// Versuch nachfragen — sonst entsteht die Dublette, die TANSS nicht abfängt.</para>
    ///
    /// <para>Danach das Übliche: Die Firmenauswahl führt einen Entprellungstakt; ohne diese
    /// Zeile liefe er nach dem Schliessen weiter und hielte den Dialog im Speicher.</para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        RescueUnanswered();
    }

    /// <summary>Reiht ein, was beim Schliessen unversorgt geblieben ist.</summary>
    /// <remarks>
    /// Hausregel 5 in ihrer schärfsten Lesart: Diese Methode läuft, während die Anwendung
    /// zugeht. Was hier wirft, darf das Schliessen nicht aufhalten — und was hier misslingt,
    /// kostet die Sitzung trotzdem nicht: Ihr Vermerk als laufende Sitzung steht dann noch, und
    /// die Wiederherstellung findet sie beim nächsten Start.
    /// </remarks>
    private void RescueUnanswered()
    {
        if (_settled || IsBooked || !CanEdit)
        {
            return;
        }

        try
        {
            if (FindQueue() is not { } queue || BasePayload() is not { } payload)
            {
                return;
            }

            if (!queue.Enqueue(payload, awaitDecision: true))
            {
                // Die Kennung ist bereits bekannt. Dann ist die Sitzung versorgt, und hier ist
                // ausdruecklich nichts weiter zu tun.
                return;
            }

            if (IsBooking)
            {
                queue.MarkFailed(payload.RemoteMaintenanceId,
                    "Das Fenster wurde geschlossen, während gesendet wurde. Ob TANSS die "
                    + "Fernwartung angenommen hat, ist ungeklärt — vor dem nächsten Versuch ist "
                    + "nachzufragen.",
                    _host.Clock.GetLocalNow());
            }

            _ = queue.RemoveOpenSession(_session.SessionId);
            _settled = true;

            LogDialog("dialog.unanswered", SessionOutcome.Deferred,
                "Der Abschlussdialog wurde ohne Entscheidung geschlossen. Die Sitzung ist "
                + "eingereiht und wartet dort auf eine Antwort; von selbst geht sie NICHT nach "
                + "TANSS. Beim nächsten Start wird sie erneut vorgelegt.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Unbeantwortete Sitzung nicht gesichert: "
                                               + ex.Message);
        }
    }

    private IUploadQueue? FindQueue() => _host.Composition?.Queue;

    /// <summary>
    /// Die rohe Nutzlast dieser Sitzung — ohne Bericht, Ticket und Firma.
    /// </summary>
    /// <remarks>
    /// Über <see cref="SessionWatcherService.PayloadFor"/> und ausdrücklich nicht hier
    /// nachgebaut: Die Existenzprüfung gegen Dubletten vergleicht Kennung und Zeitfenster,
    /// und zwei Rechenwege ergäben zwei Fernwartungen, die sie nicht mehr zusammenbringt.
    /// </remarks>
    private RemoteSupportWrite? BasePayload() =>
        _host.Composition is { } composition
            ? SessionWatcherService.PayloadFor(composition, _session)
            : null;

    /// <summary>
    /// Liest die Warteschlangenzeile dieser Sitzung — und sagt, ob die Frage beantwortet ist.
    /// </summary>
    /// <remarks>
    /// <b>Drei Ausgänge, weil zwei zu wenig sind.</b> „Es gibt keine Zeile“ und „die
    /// Warteschlange liess sich nicht lesen“ sehen beide wie <see langword="null"/> aus und
    /// bedeuten das Gegenteil: Im ersten Fall darf gesendet werden, im zweiten auf keinen Fall
    /// — es könnte längst eine Zeile geben, die schon einmal draussen war, und TANSS
    /// dedupliziert nicht.
    /// </remarks>
    /// <param name="row">Die Zeile, oder <see langword="null"/>, wenn es keine gibt.</param>
    /// <returns><see langword="false"/>, wenn die Warteschlange gerade nicht zu lesen war.</returns>
    private bool TryReadRow(out QueuedUpload? row)
    {
        row = null;

        if (FindQueue() is not { } queue)
        {
            // Ohne Laufzeit gibt es keine Warteschlange - das ist eine Antwort und keine
            // Stoerung: Es steht dort nichts, weil es dort nichts geben kann.
            return true;
        }

        try
        {
            row = queue.Find(_session.SessionId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Warteschlange nicht lesbar: " + ex.Message);
            return false;
        }
    }

    private RemoteSupportWrite? FindPayload()
    {
        _ = TryReadRow(out QueuedUpload? row);
        return row?.Payload;
    }

    /// <summary>
    /// Entfernt den Vermerk der laufenden Sitzung.
    /// </summary>
    /// <remarks>
    /// <b>Erst wenn die Sitzung anderswo steht</b> — gebucht, eingereiht oder ausdrücklich
    /// verworfen. Bis dahin ist dieser Vermerk die einzige Sicherung gegen einen Absturz bei
    /// offenem Dialog; ihn früher zu entfernen hiesse, genau das Fenster zu öffnen, das dieser
    /// Umbau geschlossen halten soll. Hausregel 5: Misslingt es, wird die Sitzung beim nächsten
    /// Start ein zweites Mal vorgelegt — lästig, aber niemals verloren, und die Warteschlange
    /// nimmt eine bekannte Kennung ohnehin kein zweites Mal auf.
    /// </remarks>
    private void ClearOpenSession()
    {
        try
        {
            _ = FindQueue()?.RemoveOpenSession(_session.SessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Laufende Sitzung nicht entfernt: " + ex.Message);
        }
    }

    /// <summary>
    /// Trägt die von TANSS vergebene Kennung in den Verlauf ein.
    /// </summary>
    /// <remarks>
    /// <b>Seit aus diesem Fenster heraus unmittelbar gebucht wird, gehört das hierher.</b> Beim
    /// Weg über den Sendedienst tut es jener; wird direkt gesendet, kommt niemand sonst mehr
    /// vorbei. Ohne diesen Schritt zeigte der Verlauf auf Dauer „wartet“, obwohl die
    /// Fernwartung längst in TANSS steht — und der Techniker legte sie ein zweites Mal an.
    /// <para>Abgeriegelt wie jeder Vermerk (Hausregel 5): Der Upload ist geschehen und wird
    /// durch eine misslungene Verlaufszeile nicht ungeschehen.</para>
    /// </remarks>
    /// <param name="tanssSupportId">Die Kennung, unter der TANSS die Fernwartung führt.</param>
    private void RecordBooked(int tanssSupportId)
    {
        if (_host.Composition is not { } composition || !composition.Config.History.Enabled)
        {
            return;
        }

        try
        {
            _ = composition.History.MarkBooked(_session.SessionId, tanssSupportId,
                "Aus dem Abschlussdialog heraus unmittelbar gebucht; in der Warteschlange hat "
                + "sie nie gestanden.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Verlauf nicht fortgeschrieben: " + ex.Message);
        }
    }
}
