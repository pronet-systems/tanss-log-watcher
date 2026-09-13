namespace TanssLogWatcher.Storage.History;

/// <summary>Ein Ausschnitt des Verlaufs samt der Gesamtzahl.</summary>
/// <remarks>
/// Die Gesamtzahl gehört zwingend zum Ausschnitt. Ohne sie könnte die Seite nicht sagen, dass
/// sie nicht alles zeigt — und eine Liste, die bei hundert Zeilen aufhört und so tut, als
/// wären es alle, lässt jemanden nach einer Sitzung suchen, die er nie finden wird.
/// </remarks>
public sealed record SessionHistoryPage
{
    /// <summary>Der leere Ausschnitt.</summary>
    /// <remarks>
    /// Damit ein Fehlschlag eine Antwort hat, die nichts behauptet: Nach Hausregel 5 kostet
    /// ein missglückter Vorgang den Vorgang und nicht den Dienst — die Seite zeigt dann eine
    /// leere Liste und den Fehler, nicht eine ausgedachte Zeile.
    /// </remarks>
    public static SessionHistoryPage Empty { get; } = new() { Items = [], Total = 0 };

    /// <summary>Die Einträge, jüngste zuerst.</summary>
    public required IReadOnlyList<SessionHistoryEntry> Items { get; init; }

    /// <summary>Wie viele Einträge es insgesamt gibt — unter demselben Filter.</summary>
    public required int Total { get; init; }

    /// <summary>Gibt es mehr, als dieser Ausschnitt zeigt?</summary>
    public bool HasMore => Items.Count < Total;
}

/// <summary>Der dauerhafte Verlauf abgeschlossener Sitzungen.</summary>
/// <remarks>
/// <para>Die Leseseite des Verlaufs — das, was die Seite „Verlauf“ braucht. Das Anlegen einer
/// Zeile, der einmalige Nachtrag und das Aufräumen gehören nicht hierher: Sie geschehen im
/// Dienst, nicht in der Anzeige, und eine Anzeige, die den Verlauf schreiben könnte, lädt
/// dazu ein, es zu tun.</para>
///
/// <para>Was diese Schnittstelle herausgibt, ist bereits geschwärzt oder nicht — sie schwärzt
/// nicht selbst. Ob die Gegenstelle im Klartext steht, entscheidet die Konfiguration beim
/// Schreiben und die Frist beim Aufräumen; wer hier liest, bekommt den Zustand, der gilt.</para>
/// </remarks>
public interface ISessionHistory
{
    /// <summary>
    /// Die jüngsten Sitzungen samt Gesamtzahl.
    /// </summary>
    /// <remarks>
    /// Ausdrücklich ohne Versatz: Nachgeladen wird, indem derselbe Ausschnitt grösser geholt
    /// wird. Kommt zwischen zwei Aufrufen eine Sitzung dazu, rutschte bei <c>OFFSET</c> genau
    /// eine Zeile über die Grenze und wäre nie zu sehen.
    /// </remarks>
    /// <param name="limit">Höchstens so viele; mindestens eine.</param>
    /// <param name="onlyWithRecording">
    /// Nur Sitzungen, zu denen die Buchführung eine Aufzeichnung kennt — auch eine, deren
    /// Dateien die Frist bereits geholt hat. Gefiltert wird nach dem Eintrag, nicht nach der
    /// Datei: Sonst verschwänden Zeilen aus der Liste, sobald die Aufzeichnung gelöscht wird,
    /// und mit ihnen der Nachweis, dass es sie gab.
    /// </param>
    /// <returns>Der Ausschnitt samt der Gesamtzahl unter demselben Filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="limit"/> ist kleiner als 1.
    /// </exception>
    SessionHistoryPage Recent(int limit, bool onlyWithRecording);
}
