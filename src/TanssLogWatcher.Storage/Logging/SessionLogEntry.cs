namespace TanssLogWatcher.Storage.Logging;

/// <summary>Wie ein Vorgang ausgegangen ist.</summary>
public enum SessionOutcome
{
    /// <summary>Wie beabsichtigt geschehen.</summary>
    Ok,

    /// <summary>
    /// Nichts geschrieben, weil der Trockenlauf eingeschaltet war.
    /// </summary>
    /// <remarks>
    /// Bewusst ein eigenes Ergebnis und nicht <see cref="Ok"/> oder <see cref="Skipped"/>.
    /// Ein Trockenlauf, der sich im Protokoll wie ein Erfolg liest, ist die Vorlage für die
    /// Frage „warum steht die Fernwartung nicht in TANSS, das Protokoll sagt doch ok“.
    /// </remarks>
    DryRun,

    /// <summary>Bewusst übergangen — etwa eine ausgeschlossene Gegenstelle.</summary>
    Skipped,

    /// <summary>Aufgeschoben: in die Warteschlange gelegt, noch nicht versucht.</summary>
    Deferred,

    /// <summary>Misslungen.</summary>
    Error,
}

/// <summary>Was den Vorgang ausgelöst hat.</summary>
public enum SessionTrigger
{
    /// <summary>Ein Beobachtungstakt.</summary>
    Watcher,

    /// <summary>Ein Wiederholungslauf der Warteschlange.</summary>
    Retry,

    /// <summary>Die Wiederherstellung nach einem Programmstart.</summary>
    Startup,

    /// <summary>Das geordnete Beenden.</summary>
    Shutdown,

    /// <summary>Eine Handlung des Technikers.</summary>
    Manual,
}

/// <summary>
/// Ein Eintrag des Änderungsprotokolls.
/// </summary>
/// <remarks>
/// <para>Das Protokoll hält fest, <b>warum</b> etwas geschah, nicht nur dass. Genau danach
/// wird gefragt, wenn eine Fernwartung fehlt oder doppelt dasteht: Was war der Anlass, wer
/// hat ausgelöst, was antwortete TANSS, wie lange dauerte es, und was kam heraus.</para>
///
/// <para><see cref="Reason"/>, <see cref="Detail"/> und <see cref="WindowTitle"/> laufen
/// beim Schreiben durch <see cref="TanssLogWatcher.Api.Diagnostics.Redaction"/> — die eine
/// Schwärzung des Hauses, die auch <c>apiKey</c> kennt. Weitergereichte Fehlermeldungen
/// tragen erfahrungsgemäß mehr mit sich, als der Aufrufer beabsichtigt — ein
/// <c>Bearer eyJ…</c> aus einer HTTP-Meldung gehört nicht in eine Datei, die im
/// Benutzerprofil liegt.</para>
/// </remarks>
public sealed record SessionLogEntry
{
    /// <summary>Fortlaufende Nummer. Beim Anlegen 0, danach die vergebene Kennung.</summary>
    public long Id { get; init; }

    /// <summary>Zeitpunkt. Wird beim Schreiben gesetzt, wenn er offen bleibt.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Was geschah — eine kurze, gleichbleibende Bezeichnung wie <c>session.closed</c>,
    /// <c>upload</c>, <c>exists.probe</c> oder <c>queue.requeue</c>.
    /// </summary>
    /// <remarks>
    /// Bewusst eine Zeichenkette und keine Aufzählung: Die Beobachtungsschicht bringt eigene
    /// Vorgänge mit, und ein neuer Vorgang soll keine Änderung an dieser Schicht erzwingen.
    /// Das Ergebnis dagegen ist eine geschlossene Menge — siehe <see cref="SessionOutcome"/>.
    /// </remarks>
    public required string Operation { get; init; }

    /// <summary>Wie es ausging.</summary>
    public required SessionOutcome Outcome { get; init; }

    /// <summary>Warum es geschah. Der eigentliche Zweck dieses Protokolls.</summary>
    public required string Reason { get; init; }

    /// <summary>Wer oder was den Vorgang auslöste.</summary>
    public required SessionTrigger Trigger { get; init; }

    /// <summary>Unsere Sitzungskennung, sofern der Vorgang zu einer Sitzung gehört.</summary>
    public string? RemoteMaintenanceId { get; init; }

    /// <summary>HTTP-Status der Antwort von TANSS, sofern es eine gab.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>Dauer in Millisekunden.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Die von TANSS vergebene Kennung der Fernwartung, sofern eine entstand.</summary>
    public int? TanssSupportId { get; init; }

    /// <summary>Ergänzende Angaben, etwa der Fehlertext aus <c>error.text</c>.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Die Fensterbeschriftung, aus der die Sitzung erkannt wurde.
    /// </summary>
    /// <remarks>
    /// Ein eigenes Feld und kein Satzteil in <see cref="Reason"/>: Nur was als
    /// Beschriftung <i>benannt</i> ist, lässt sich auch als Beschriftung schwärzen. Steht
    /// <c>logging.redact_window_titles</c> (Standard), schreibt <see cref="SessionLog"/>
    /// statt des Textes dessen Fingerabdruck — Kundenname, Rechnername und E-Mail-Betreff
    /// bleiben damit von der Platte fort, die Frage „dasselbe Fenster?“ bleibt beantwortbar.
    /// Wer eine Beschriftung stattdessen in den Freitext schreibt, umgeht die Zusage: Dort
    /// ist sie von gewöhnlicher Prosa nicht zu unterscheiden.
    /// </remarks>
    public string? WindowTitle { get; init; }
}
