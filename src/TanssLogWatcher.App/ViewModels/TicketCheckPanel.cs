using System.Globalization;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Die Zeile unter einem Ticketfeld: was aus der eingetippten Nummer geworden ist.
/// </summary>
/// <remarks>
/// <para><b>Sie zeigt, sie sperrt nie.</b> Das Ergebnis einer Prüfung ist eine Auskunft und
/// keine Erlaubnis. Der Abschlussdialog hat den Bericht des Technikers im Feld, danach geht der
/// Eintrag mit der automatischen Beschreibung hinaus — eine Prüfung, die den Knopf sperrt,
/// kostet bei einem Aussetzer der Leitung genau den Bericht, den der Techniker gerade
/// geschrieben hat (Hausregel 5). Deshalb hält dieser Anzeiger nichts an; er stellt nur hin,
/// was TANSS gesagt hat.</para>
///
/// <para><b>Gemeinsam für beide Dialoge.</b> Abschlussdialog und Leistungsdialog fragen
/// dasselbe und sollen dieselbe Antwort gleich formulieren. Zwei Abschriften desselben
/// Textbaukastens liefen nach der ersten Änderung auseinander.</para>
///
/// <para><b>Ohne Oberfläche prüfbar.</b> Der Anzeiger ist ein gewöhnliches Objekt mit
/// Benachrichtigungen; er kennt weder Fenster noch Steuerelemente. Die Fälle in
/// <c>TicketCheckTests</c> arbeiten unmittelbar auf ihm.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class TicketCheckPanel : ObservableObject
{
    private readonly ITicketVerification? _verification;

    // Jede Pruefung bekommt eine Nummer. Antwortet eine alte nach einer neuen, wird sie
    // verworfen - sonst ueberschriebe die Antwort auf "249" die auf das inzwischen getippte
    // "24911", und unter dem Feld staende ein Ticket, das dort nicht mehr steht.
    private int _generation;

    /// <summary>Baut den Anzeiger zu einer Prüfstelle.</summary>
    /// <param name="verification">
    /// Die Prüfstelle, oder <see langword="null"/>. Ohne sie bleibt die Zeile leer — behauptet
    /// wird dann nichts (Hausregel 2).
    /// </param>
    public TicketCheckPanel(ITicketVerification? verification) => _verification = verification;

    /// <summary>
    /// Baut den Anzeiger aus dem Ticketspeicher der Laufzeit, falls der prüfen kann.
    /// </summary>
    /// <remarks>
    /// <c>RuntimeComposition.Tickets</c> trägt das konkrete <c>TicketRepository</c>, und das
    /// erfüllt <see cref="ITicketVerification"/>. Die Abfrage steht hier und nicht in beiden
    /// Dialogen; erfüllt eine Attrappe die Schnittstelle nicht, bleibt die Zeile leer.
    /// </remarks>
    /// <param name="tickets">Der Ticketspeicher, oder <see langword="null"/>.</param>
    /// <returns>Ein Anzeiger; niemals <see langword="null"/>.</returns>
    public static TicketCheckPanel For(ITicketRepository? tickets) =>
        new(tickets as ITicketVerification);

    /// <summary>Was unter dem Feld steht; leer heisst „hier ist nichts zu sagen“.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    [NotifyPropertyChangedFor(nameof(ShowNote))]
    [NotifyPropertyChangedFor(nameof(ShowWarning))]
    private string _note = string.Empty;

    /// <summary>
    /// Ist die Zeile eine Warnung?
    /// </summary>
    /// <remarks>
    /// Nur zwei Fälle: TANSS hat <c>OBJECT_NOT_FOUND</c> gemeldet, oder im Feld steht etwas,
    /// das gar keine Nummer ist. <b>„Nicht ermittelt“ ist keine Warnung</b> — es ist eine
    /// offene Frage, und sie rot zu färben hiesse, aus einem Aussetzer der Leitung einen
    /// Vorwurf zu machen.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNote))]
    [NotifyPropertyChangedFor(nameof(ShowWarning))]
    private bool _isWarning;

    /// <summary>Läuft gerade eine Prüfung?</summary>
    [ObservableProperty]
    private bool _isChecking;

    /// <summary>Die zuletzt geprüfte Nummer; 0, wenn keine geprüft wurde.</summary>
    [ObservableProperty]
    private int _ticketId;

    /// <summary>
    /// Wie die letzte Prüfung ausgegangen ist, oder <see langword="null"/>, wenn keine lief.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> heisst ausdrücklich <b>nicht</b>
    /// <see cref="TicketCheckOutcome.Undetermined"/>: Bei <c>Undetermined</c> wurde gefragt und
    /// keine Antwort erhalten, bei <see langword="null"/> wurde gar nicht gefragt.
    /// </remarks>
    [ObservableProperty]
    private TicketCheckOutcome? _outcome;

    /// <summary>Gibt es überhaupt etwas anzuzeigen?</summary>
    public bool HasNote => Note.Length > 0;

    /// <summary>Ist die gewöhnliche, unauffällige Zeile zu zeigen?</summary>
    public bool ShowNote => HasNote && !IsWarning;

    /// <summary>Ist die auffällige Zeile zu zeigen?</summary>
    public bool ShowWarning => HasNote && IsWarning;

    /// <summary>Kann hier überhaupt geprüft werden?</summary>
    /// <remarks>
    /// <see langword="false"/> ohne Einrichtung oder mit einer Attrappe, die
    /// <see cref="ITicketVerification"/> nicht erfüllt. Der Dialog arbeitet dann wie zuvor,
    /// nur ohne Zeile darunter.
    /// </remarks>
    public bool CanCheck => _verification is not null;

    /// <summary>Räumt die Zeile leer — und macht eine noch laufende Prüfung ungültig.</summary>
    public void Clear()
    {
        _generation++;

        TicketId = 0;
        Outcome = null;
        IsChecking = false;
        IsWarning = false;
        Note = string.Empty;
    }

    /// <summary>
    /// Stellt einen Befund hin, den das Werkzeug allein getroffen hat.
    /// </summary>
    /// <remarks>
    /// Für das, was ohne TANSS feststeht: „im Feld steht keine Nummer“. Eine laufende Prüfung
    /// wird dabei ungültig, denn sie gehörte zur vorigen Eingabe.
    /// </remarks>
    /// <param name="note">Der fertige deutsche Satz.</param>
    public void ShowProblem(string note)
    {
        _generation++;

        TicketId = 0;
        Outcome = null;
        IsChecking = false;
        IsWarning = true;
        Note = note ?? string.Empty;
    }

    /// <summary>
    /// Fragt TANSS nach dieser Nummer und schreibt das Ergebnis in die Zeile.
    /// </summary>
    /// <remarks>
    /// <para>Wirft nicht. <see cref="ITicketVerification"/> sagt das zwar bereits zu, aber
    /// gerufen wird es hier aus einer Eigenschaftsänderung heraus — eine Ausnahme hätte keinen
    /// Fänger und risse die Anwendung mit (Hausregel 5). Auch der Abbruch wird geschluckt: Er
    /// entsteht, wenn der Dialog zugeht, und dann ist niemand mehr da, dem er etwas sagte.</para>
    /// <para>Bei <see cref="TicketCheckOutcome.Exists"/> steht <c>Summary</c> in der Zeile und
    /// nicht <c>Explanation</c>: „#24911 Serverstörung — Müller GmbH“ ist das, woran ein
    /// Zahlendreher auffällt.</para>
    /// </remarks>
    /// <param name="ticketId">Die zu prüfende Nummer.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>Die laufende Prüfung.</returns>
    public async Task CheckAsync(int ticketId, CancellationToken ct = default)
    {
        if (_verification is null)
        {
            // Ohne Prüfstelle wird nichts behauptet - auch kein "konnte nicht geprueft werden",
            // denn gefragt wurde ja niemand (Hausregel 2).
            Clear();
            return;
        }

        int mine = ++_generation;

        TicketId = ticketId;
        Outcome = null;
        IsWarning = false;
        IsChecking = true;
        Note = string.Create(CultureInfo.CurrentCulture, $"Ticket {ticketId} wird geprüft …");

        TicketCheck? check = null;
        string? failure = null;

        try
        {
            check = await _verification.CheckAsync(ticketId, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            failure = string.Create(CultureInfo.CurrentCulture,
                $"Ob es Ticket {ticketId} gibt, wurde nicht ermittelt: Die Prüfung wurde "
                + $"abgebrochen. Buchen bleibt möglich.");
        }
        catch (Exception ex)
        {
            // Der geschwaerzte Text steht INNERHALB der Interpolation: string.Create nimmt die
            // Zeichenkette als Handler, und ein mit + angehaengtes Stueck macht daraus eine
            // gewoehnliche Verkettung, auf die diese Ueberladung nicht mehr passt (CS1620).
            string reason = Redaction.Scrub(ex.Message);

            failure = string.Create(CultureInfo.CurrentCulture,
                $"Ob es Ticket {ticketId} gibt, liess sich nicht prüfen: {reason} "
                + $"Buchen bleibt möglich.");
        }

        if (mine != _generation)
        {
            // Inzwischen steht eine andere Nummer im Feld. Deren Pruefung hat das Sagen.
            return;
        }

        IsChecking = false;

        if (check is null)
        {
            Outcome = TicketCheckOutcome.Undetermined;
            IsWarning = false;
            Note = failure ?? string.Empty;
            return;
        }

        Outcome = check.Outcome;
        IsWarning = check.BlocksBooking;

        Note = check.Outcome switch
        {
            // Titel und Firma, damit ein Zahlendreher auf ein FREMDES vorhandenes Ticket
            // auffaellt - der haeufigste Fehler, den eine blosse Existenzpruefung durchlaesst.
            TicketCheckOutcome.Exists => check.Summary,

            // Ohne Nummer ist nichts zu melden; das leere Feld sagt es selbst.
            TicketCheckOutcome.NoTicketNumber => string.Empty,

            // Fertige deutsche Sätze aus der Prüfstelle, unverändert. Wer sie hier
            // nachformulierte, holte sich die Lüge "Ticket gibt es nicht" zurück.
            _ => check.Explanation,
        };
    }

    /// <summary>
    /// Liest eine Ticketnummer aus dem, was im Feld steht.
    /// </summary>
    /// <remarks>
    /// <para>Nimmt „24911“, „#24911“ und „#24911 — Serverstörung“ — die letzte Form ist die
    /// Zeile aus der Auswahlliste, und sie landet im Textteil eines bearbeitbaren
    /// Auswahlfeldes.</para>
    /// <para><b>„24911x“ wird abgelehnt.</b> Beiwerk muss durch einen Zwischenraum getrennt
    /// sein; direkt an den Ziffern klebende Zeichen sind ein Vertipper, und eine Nummer daraus
    /// zu schneiden hiesse zu raten (Hausregel 2).</para>
    /// </remarks>
    /// <param name="text">Der Inhalt des Feldes.</param>
    /// <param name="ticketId">Die gelesene Nummer, oder 0.</param>
    /// <returns><see langword="true"/>, wenn eine Nummer grösser als 0 herauskam.</returns>
    public static bool TryReadNumber(string? text, out int ticketId)
    {
        ticketId = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> rest = text.AsSpan().Trim();

        if (rest[0] == '#')
        {
            rest = rest[1..].TrimStart();
        }

        int digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        if (digits == 0 || (digits < rest.Length && !char.IsWhiteSpace(rest[digits])))
        {
            return false;
        }

        // NumberStyles.None: kein Vorzeichen, keine Tausenderpunkte. Eine Ticketnummer ist eine
        // Folge von Ziffern, und "1.234" waere je nach Kultur 1234 oder 1 - geraten wird nicht.
        return int.TryParse(rest[..digits], NumberStyles.None, CultureInfo.InvariantCulture,
                            out ticketId)
            && ticketId > 0;
    }
}
