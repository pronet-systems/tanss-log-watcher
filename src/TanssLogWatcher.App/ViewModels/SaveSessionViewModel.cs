using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Ai;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Queue;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Der Abschlussdialog: Kommentar und Ticket zu einer gerade beendeten Sitzung.
/// </summary>
/// <remarks>
/// <para><b>Die Sitzung liegt schon in der Warteschlange, bevor dieses Fenster aufgeht.</b> Das
/// ist Absicht und der Kern des Entwurfs: Ein Absturz, ein Stromausfall oder ein versehentliches
/// Wegklicken während des Tippens darf die Sitzung nicht kosten. Dieses Fenster ergänzt eine
/// vorhandene Zeile, es erzeugt sie nicht.</para>
///
/// <para><b>Deshalb gibt es drei Ausgänge und nicht zwei.</b> „Später“ lässt den Eintrag
/// stehen, wie er ist — er geht mit der automatischen Beschreibung hinaus und bleibt in der
/// Warteschlange sichtbar. Nur „Verwerfen“ entfernt ihn, und das ist die seltene, folgenreiche
/// Wahl. Die Vorlage kannte allein Speichern und Verwerfen, und Verwerfen löschte die Sitzung
/// spurlos.</para>
///
/// <para><b>Das Wettrennen mit dem Sendedienst wird gemeldet, nicht verschwiegen.</b> Zwischen
/// dem Ende der Sitzung und dem Klick auf „In TANSS buchen“ kann der Sendedienst den Eintrag
/// bereits verschickt haben. Dann ändert hier nichts mehr etwas — und der Dialog sagt das,
/// statt einen Erfolg vorzutäuschen, den es nicht gab.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class SaveSessionViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly SessionSnapshot _session;

    /// <summary>Baut den Dialog zu einer beendeten Sitzung.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="closed">Die beendete Sitzung samt dem, was mit ihr geschah.</param>
    /// <param name="typeName">Der sprechende Name des Fernwartungstyps, falls bekannt.</param>
    /// <param name="color">Die Farbe der Anbindung als Hexwert ohne Raute.</param>
    public SaveSessionViewModel(AppHost host, SessionClosed closed, string? typeName, string? color)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(closed);

        _host = host;
        _session = closed.Session;

        Color = color ?? string.Empty;
        Destination = string.IsNullOrWhiteSpace(_session.Destination)
            ? "Kein Ziel ermittelt"
            : _session.Destination;
        Application = _session.ProfileName;

        TypeText = _session.IsMapped
            ? string.Create(CultureInfo.CurrentCulture,
                $"{_session.RemoteSupportTypeId} — {typeName ?? "unbekannter Typ"}")
            : "nicht überwacht";

        DateTimeOffset ended = _session.EndedAt ?? host.Clock.GetLocalNow();
        TimeRange = string.Create(CultureInfo.CurrentCulture,
            $"{Texts.Clock(_session.StartedAt)} – {Texts.Clock(ended)} · "
            + $"{Texts.Ticking(ended - _session.StartedAt)}");

        // Die automatische Beschreibung steht bereits in der Nutzlast. Sie wird hier gezeigt,
        // damit klar ist, was in TANSS ankommt, auch wenn niemand etwas dazuschreibt.
        AutoComment = closed.Disposition == SessionDisposition.Enqueued
            || closed.Disposition == SessionDisposition.AlreadyQueued
            ? FindPayload()?.Comment ?? string.Empty
            : string.Empty;

        CanEdit = closed.Disposition is SessionDisposition.Enqueued
            or SessionDisposition.AlreadyQueued;

        if (!CanEdit)
        {
            Result = closed.Reason;
        }

        _comment = BuildReport(ended);
    }

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

        _ = report.AppendLine(CultureInfo.CurrentCulture,
            $"Zeitraum:     {Texts.Day(_session.StartedAt)} {Texts.Clock(_session.StartedAt)} – "
            + $"{Texts.Clock(ended)} ({Texts.Span(duration)})");
        _ = report.AppendLine(CultureInfo.CurrentCulture,
            $"Gegenstelle:  {Destination}");
        _ = report.AppendLine(CultureInfo.CurrentCulture,
            $"Anwendung:    {Application} ({_session.ProfileKey}, PID {_session.ProcessId})");
        _ = report.AppendLine(CultureInfo.CurrentCulture,
            $"Arbeitsplatz: {Environment.MachineName} / {Environment.UserName}");

        if (_host.Config is { } config)
        {
            _ = report.AppendLine(CultureInfo.CurrentCulture,
                $"Techniker:    Mitarbeiter {config.Tanss.EmployeeId}");
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
    /// Falsch, wenn die Sitzung gar nicht eingereiht wurde — etwa ohne Zuordnung auf einen
    /// Fernwartungstyp oder im Trockenlauf. Dann zeigt der Dialog, was geschehen ist, und bietet
    /// nichts an, was folgenlos bliebe.
    /// </remarks>
    public bool CanEdit { get; }

    /// <summary>Das Ticket, auf das gebucht wird; leer für keines.</summary>
    [ObservableProperty]
    private string _ticket = string.Empty;

    /// <summary>Was der Techniker dazuschreibt.</summary>
    [ObservableProperty]
    private string _comment = string.Empty;

    /// <summary>Die Rückmeldung nach einer Handlung.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _result = string.Empty;

    /// <summary>Gibt es eine Rückmeldung?</summary>
    public bool HasResult => !string.IsNullOrEmpty(Result);

    /// <summary>Der Dialog ist fertig und kann geschlossen werden.</summary>
    public event EventHandler? Finished;

    // --- Sprachmodell-Unterstützung ----------------------------------------

    /// <summary>Steht die Unterstützung bereit?</summary>
    /// <remarks>
    /// Die Antwort kommt aus <see cref="AiGateway.TryCreate"/> und nicht aus einer eigenen
    /// Prüfung hier: Es soll genau eine Stelle geben, die über das Senden entscheidet.
    /// </remarks>
    public bool IsAiAvailable => _host.Config?.Ai.IsUsable == true;

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
    /// Übernimmt Kommentar und Ticket in den wartenden Eintrag.
    /// </summary>
    /// <remarks>
    /// <para>Der Text des Feldes <b>ersetzt</b> den Kommentar. Er ist beim Öffnen bereits mit
    /// dem vollständigen Bericht gefüllt — Zeitraum, Gegenstelle, Anwendung, Arbeitsplatz —,
    /// und was davon in TANSS landen soll, entscheidet der Techniker durch Bearbeiten.</para>
    /// <para>Wird das Feld leer geräumt, bleibt die automatische Beschreibung stehen. Ein
    /// Eintrag ganz ohne Kommentar wäre in TANSS nicht mehr zuzuordnen.</para>
    /// </remarks>
    [RelayCommand]
    private void Save()
    {
        if (!CanEdit || FindQueue() is not { } queue)
        {
            Result = "Es liegt kein wartender Eintrag vor, der zu ändern wäre.";
            return;
        }

        if (FindPayload() is not { } payload)
        {
            Result = "Der Eintrag ist nicht mehr in der Warteschlange — der Sendedienst hat ihn "
                + "bereits verschickt. Kommentar und Ticket wären in TANSS nachzutragen.";
            return;
        }

        int ticketId = 0;
        string trimmed = Ticket.Trim().TrimStart('#');

        if (trimmed.Length > 0
            && !int.TryParse(trimmed, NumberStyles.None, CultureInfo.CurrentCulture, out ticketId))
        {
            Result = $"„{Ticket}“ ist keine Ticketnummer. Erwartet wird eine Zahl, oder das Feld "
                + "bleibt leer.";
            return;
        }

        string text = Comment.Trim();
        string merged = text.Length == 0 ? payload.Comment : text;

        try
        {
            if (!queue.UpdatePayload(payload with { Comment = merged, TicketId = ticketId }))
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

        // Die Schonfrist hat ihren Zweck erfuellt - der Bericht steht. Ohne diese Freigabe laege
        // der Eintrag noch bis zu fuenf Minuten daneben, obwohl niemand mehr etwas daran tut.
        _ = queue.Release(_session.SessionId);
        _ = _host.Uploads.RefreshQueue();

        Finished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lässt den Eintrag stehen, wie er ist.</summary>
    /// <remarks>
    /// Er geht mit der automatischen Beschreibung hinaus und bleibt bis dahin in der
    /// Warteschlange sichtbar. Das ist der Ausgang für „jetzt gerade nicht“ — und ausdrücklich
    /// kein Verlust.
    /// </remarks>
    [RelayCommand]
    private void Later() => Finished?.Invoke(this, EventArgs.Empty);

    /// <summary>Entfernt den wartenden Eintrag.</summary>
    [RelayCommand]
    private void Discard()
    {
        if (CanEdit && FindQueue() is { } queue)
        {
            try
            {
                if (!queue.Remove(_session.SessionId))
                {
                    Result = "Der Eintrag liess sich nicht mehr entfernen — der Sendedienst hat "
                        + "ihn bereits übernommen. In TANSS ist er damit angelegt.";
                    return;
                }

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

    private IUploadQueue? FindQueue() => _host.Composition?.Queue;

    private RemoteSupportWrite? FindPayload()
    {
        try
        {
            return FindQueue()?.Find(_session.SessionId)?.Payload;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Warteschlange nicht lesbar: " + ex.Message);
            return null;
        }
    }
}
