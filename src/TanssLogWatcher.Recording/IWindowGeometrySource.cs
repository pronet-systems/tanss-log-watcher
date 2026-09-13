namespace TanssLogWatcher.Recording;

/// <summary>
/// Woher die Fenster einer Sitzung samt ihrer Lage kommen.
/// </summary>
/// <remarks>
/// <para><b>Eine eigene Quelle, nicht die der Sitzungserkennung.</b> Die vorhandene
/// <c>WindowSource</c> im Überwachungsprojekt verwirft jedes Fenster mit leerem Titel — für die
/// Erkennung ist das richtig, denn ein namenloses Fenster verrät nichts über sein Ziel. Für die
/// Aufzeichnung ist es falsch: Dialoge tragen regelmässig keinen Titel, und genau die gehen
/// sonst verloren.</para>
/// <para>Die Schnittstelle trennt das Prüfbare vom Unprüfbaren. Der Direktor entscheidet gegen
/// erfundene Fensterlagen; die echte Aufzählung braucht einen Bildschirm.</para>
/// </remarks>
public interface IWindowGeometrySource
{
    /// <summary>
    /// Sammelt die sichtbaren Fenster eines Prozesses samt Lage.
    /// </summary>
    /// <remarks>
    /// Zurück kommen nur Fenster der obersten Ebene, die tatsächlich etwas zeigen. Minimierte,
    /// unsichtbare und von der Fensterverwaltung versteckte fallen weg — sie haben keine
    /// Fläche, und eine Aufzeichnung ohne Fläche ist ein schwarzes Rechteck.
    /// </remarks>
    /// <param name="processId">Der Prozess der Sitzung.</param>
    /// <returns>Die Fenster mit ihrer Lage; leer, wenn gerade keines etwas zeigt.</returns>
    IReadOnlyList<WindowBox> WindowsOf(int processId);
}
