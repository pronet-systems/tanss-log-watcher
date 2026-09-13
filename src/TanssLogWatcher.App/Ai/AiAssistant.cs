using System.Text.RegularExpressions;

using TanssLogWatcher.Storage.Config;

namespace TanssLogWatcher.App.Ai;

/// <summary>Ein Modell, wie der Anbieter es führt.</summary>
/// <param name="Id">Die Kennung, die beim Aufruf mitgeht.</param>
/// <param name="DisplayName">Der sprechende Name für die Auswahl.</param>
public sealed record AiModel(string Id, string DisplayName)
{
    /// <inheritdoc />
    public override string ToString() => DisplayName;
}

/// <summary>Was das Modell mit dem Text tun soll.</summary>
public enum AiTask
{
    /// <summary>
    /// Nur Rechtschreibung, Grammatik und Zeichensetzung.
    /// </summary>
    /// <remarks>
    /// Der zurückhaltende Fall und deshalb der voreingestellte: Er ändert nichts an dem, was
    /// dokumentiert wurde. Ein Bericht, den ein Modell inhaltlich „verbessert“ hat, ist als
    /// Nachweis gegenüber dem Kunden weniger wert, nicht mehr.
    /// </remarks>
    Proofread,

    /// <summary>Aus den Stichworten einen zusammenhängenden Bericht formulieren.</summary>
    Improve,
}

/// <summary>
/// Ein Sprachmodell-Anbieter.
/// </summary>
/// <remarks>
/// Die Schnittstelle kennt genau zwei Vorgänge: Modelle auflisten und Text überarbeiten. Mehr
/// braucht dieses Werkzeug nicht, und was es nicht braucht, soll es auch nicht können — jede
/// weitere Fähigkeit wäre ein weiterer Weg, auf dem Daten das Haus verlassen.
/// </remarks>
public interface IAiAssistant : IDisposable
{
    /// <summary>Der Name des Anbieters, für die Anzeige.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Holt die Modelle, die dieser Zugang verwenden darf.
    /// </summary>
    /// <remarks>
    /// Ausdrücklich vom Anbieter und nicht aus einer Liste im Programm: Modelle kommen und
    /// gehen, und ein fest eingebauter Name ist an dem Tag falsch, an dem er abgekündigt wird.
    /// </remarks>
    /// <param name="ct">Abbruchmarke.</param>
    Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>Überarbeitet einen Text.</summary>
    /// <param name="text">Der Bericht, so wie er im Feld steht.</param>
    /// <param name="task">Was zu tun ist.</param>
    /// <param name="model">Die Kennung des zu verwendenden Modells.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>Der überarbeitete Text.</returns>
    Task<string> ReviseAsync(string text, AiTask task, string model, CancellationToken ct = default);
}

/// <summary>
/// Die Anweisungen an das Modell.
/// </summary>
/// <remarks>
/// An einer Stelle und für beide Anbieter dieselben. Zwei Versionen desselben Auftrags lieferten
/// zwei verschiedene Ergebnisse, und welcher Anbieter gerade eingestellt ist, dürfte am Bericht
/// nicht ablesbar sein.
/// </remarks>
public static class AiPrompts
{
    /// <summary>
    /// Die Regeln, die nicht verhandelbar sind und deshalb an jede Rolle angehängt werden.
    /// </summary>
    /// <remarks>
    /// <para><b>Warum ausgerechnet diese vier nicht bearbeitbar sind.</b> Die ersten beiden sind
    /// die Zusage, auf die hin der Techniker eingewilligt hat und die in der README steht: Der
    /// Bericht ist ein Nachweis gegenüber dem Kunden, und ein Nachweis, in dem etwas steht, was
    /// nicht geschehen ist, ist schlimmer als gar keiner. Wer sie durch ein Textfeld entfernen
    /// könnte, könnte die Zusage des Werkzeugs durch ein Textfeld entfernen.</para>
    /// <para>Die letzte ist keine Haltung, sondern Technik: Ohne sie antwortet das Modell mit
    /// einer Vorrede, und die landete mitten im Bericht.</para>
    /// <para>Alles andere — die Rolle und die beiden Aufträge — lässt sich frei bearbeiten.</para>
    /// </remarks>
    public const string Fixed =
        "Unverhandelbare Regeln:\n"
        + "- Erfinde NICHTS. Keine Tätigkeiten, keine Ursachen, keine Ergebnisse, die nicht "
        + "im Text stehen. Ein Bericht ist ein Nachweis, keine Erzählung.\n"
        + "- Ändere keine Zahlen, Zeiten, Namen, Kennungen oder Platzhalter in eckigen Klammern.\n"
        + "- Behalte die deutsche Sprache und die Anrede bei.\n"
        + "- Antworte ausschliesslich mit dem überarbeiteten Text, ohne Vorrede, ohne "
        + "Rückfragen, ohne Anführungszeichen um das Ganze.";

    /// <summary>Die Rolle, die für beide Aufgaben gilt — bearbeitbar.</summary>
    public const string DefaultRole =
        "Du überarbeitest Fernwartungsberichte eines IT-Dienstleisters. Diese Berichte gehen "
        + "als Dokumentation an den Kunden.";

    /// <summary>Der Auftrag für die Rechtschreibprüfung — bearbeitbar.</summary>
    public const string DefaultProofread =
        "Korrigiere ausschliesslich Rechtschreibung, Grammatik und Zeichensetzung. "
        + "Lasse Wortwahl, Reihenfolge, Zeilenumbrüche und Formatierung unverändert. "
        + "Ist nichts zu korrigieren, gib den Text unverändert zurück.";

    /// <summary>Der Auftrag für das Ausformulieren — bearbeitbar.</summary>
    public const string DefaultImprove =
        "Formuliere die stichwortartigen Angaben zu vollständigen, sachlichen Sätzen aus, "
        + "die ein Kunde versteht. Behalte den Kopfteil mit Zeitraum, Gegenstelle, "
        + "Anwendung und Arbeitsplatz unverändert bei; ausformuliert wird nur der Abschnitt "
        + "der durchgeführten Arbeiten. Ergänze keine Tätigkeiten, die dort nicht stehen.";
}

/// <summary>
/// Die Anweisungen, mit denen tatsächlich gearbeitet wird.
/// </summary>
/// <remarks>
/// <para><b>Aus der Konfiguration, mit den eingebauten Texten als Rückfall.</b> Ein leeres oder
/// fehlendes Feld heisst „der eingebaute Text“ und nicht „keine Anweisung“ — sonst schaltete
/// ein versehentlich geleertes Feld die Regeln ab, und das Ergebnis sähe aus wie ein Fehler des
/// Modells.</para>
/// <para><b>An einer Stelle und für beide Anbieter dieselben.</b> Zwei Versionen desselben
/// Auftrags lieferten zwei verschiedene Ergebnisse, und welcher Anbieter gerade eingestellt
/// ist, dürfte am Bericht nicht ablesbar sein.</para>
/// </remarks>
/// <param name="Role">Die Rolle; die festen Regeln kommen beim Senden dazu.</param>
/// <param name="Proofread">Der Auftrag für die Rechtschreibprüfung.</param>
/// <param name="Improve">Der Auftrag für das Ausformulieren.</param>
public sealed record AiPromptSet(string Role, string Proofread, string Improve)
{
    /// <summary>Die eingebauten Anweisungen.</summary>
    public static AiPromptSet Default { get; } = new(
        AiPrompts.DefaultRole, AiPrompts.DefaultProofread, AiPrompts.DefaultImprove);

    /// <summary>
    /// Die Rolle, so wie sie an das Modell geht: der bearbeitbare Teil plus die festen Regeln.
    /// </summary>
    public string System => Role.TrimEnd() + "\n\n" + AiPrompts.Fixed;

    /// <summary>Der Auftrag zu einer Aufgabe.</summary>
    /// <param name="task">Die Aufgabe.</param>
    /// <exception cref="ArgumentOutOfRangeException">Unbekannte Aufgabe.</exception>
    public string For(AiTask task) => task switch
    {
        AiTask.Proofread => Proofread,
        AiTask.Improve => Improve,
        _ => throw new ArgumentOutOfRangeException(nameof(task)),
    };

    /// <summary>
    /// Nimmt die Anweisungen aus der Konfiguration; was dort leer ist, bleibt eingebaut.
    /// </summary>
    /// <param name="ai">Der Konfigurationsabschnitt, oder <c>null</c>.</param>
    public static AiPromptSet From(AiSection? ai) => ai is null
        ? Default
        : new AiPromptSet(
            Or(ai.RolePrompt, AiPrompts.DefaultRole),
            Or(ai.ProofreadPrompt, AiPrompts.DefaultProofread),
            Or(ai.ImprovePrompt, AiPrompts.DefaultImprove));

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

/// <summary>
/// Macht Namen vor dem Senden unkenntlich und stellt sie danach wieder her.
/// </summary>
/// <remarks>
/// <para><b>Warum das mehr ist als Beiwerk.</b> Für eine Rechtschreibprüfung muss kein Anbieter
/// in den Vereinigten Staaten wissen, dass dieser Kunde einen Server namens <c>sap</c> betreibt
/// und welcher Techniker an welchem Arbeitsplatz sitzt. Ersetzt werden die bekannten Namen durch
/// Platzhalter; was zurückkommt, wird zurückübersetzt und liest sich unverändert.</para>
///
/// <para><b>Was es nicht leistet.</b> Geschwärzt wird, was das Werkzeug kennt — Gegenstelle,
/// Rechnername, Anmeldename. Schreibt der Techniker selbst einen Kundennamen in die Zeile
/// „Durchgeführte Arbeiten“, geht der mit. Eine Schwärzung, die das verspräche, wäre eine
/// Zusage, die sie nicht halten kann; die Einwilligungsmaske sagt es deshalb ausdrücklich.</para>
/// </remarks>
public sealed class AiRedaction
{
    private readonly List<(string Placeholder, string Original)> _pairs = [];

    /// <summary>Nimmt einen Namen auf, der ersetzt werden soll.</summary>
    /// <remarks>
    /// Zu kurze Namen werden übergangen: Ein Rechnername aus zwei Zeichen käme als Silbe in
    /// gewöhnlichen Wörtern vor, und die Rückübersetzung zerlegte den Text.
    /// </remarks>
    /// <param name="original">Der Name, wie er im Text steht.</param>
    /// <param name="label">Die Art des Namens, etwa <c>ZIEL</c>.</param>
    public void Add(string? original, string label)
    {
        if (string.IsNullOrWhiteSpace(original) || original.Trim().Length < 3)
        {
            return;
        }

        string value = original.Trim();

        if (_pairs.Any(p => string.Equals(p.Original, value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _pairs.Add(($"[{label}{_pairs.Count + 1}]", value));
    }

    /// <summary>Ersetzt die aufgenommenen Namen durch Platzhalter.</summary>
    /// <param name="text">Der Text.</param>
    public string Apply(string text)
    {
        string result = text;

        foreach ((string placeholder, string original) in _pairs)
        {
            result = Regex.Replace(result, Regex.Escape(original), placeholder,
                                   RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        }

        return result;
    }

    /// <summary>Stellt die Namen wieder her.</summary>
    /// <param name="text">Der zurückgekommene Text.</param>
    public string Restore(string text)
    {
        string result = text;

        foreach ((string placeholder, string original) in _pairs)
        {
            result = result.Replace(placeholder, original, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>Wie viele Namen unkenntlich gemacht werden.</summary>
    public int Count => _pairs.Count;
}
