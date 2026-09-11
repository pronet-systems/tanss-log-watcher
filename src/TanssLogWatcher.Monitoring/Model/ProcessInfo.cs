namespace TanssLogWatcher.Monitoring.Model;

/// <summary>
/// Ein Prozess, wie ihn eine einzelne Momentaufnahme des Systems beschreibt.
/// </summary>
/// <param name="Id">Die Prozesskennung.</param>
/// <param name="ParentId">
/// Die Kennung des Erzeugers. Windows gibt sie in der Momentaufnahme mit aus; sie kann auf einen
/// bereits beendeten Prozess zeigen, dessen Kennung inzwischen neu vergeben wurde. Für die
/// Zuordnung von Netzverbindungen reicht das, für Sicherheitsentscheidungen nicht.
/// </param>
/// <param name="Name">Der Name ohne Erweiterung, also <c>mstsc</c> und nicht <c>mstsc.exe</c>.</param>
/// <param name="StartedAt">
/// Der Startzeitpunkt, sofern ermittelbar. <c>null</c> bedeutet <b>nicht ermittelbar</b> und wird
/// bewusst wie „lange genug gelaufen“ behandelt: ein fremder oder erhöht laufender Prozess darf
/// nicht deshalb aus der Überwachung fallen, weil sein Startzeitpunkt verschlossen ist.
/// </param>
public readonly record struct ProcessInfo(int Id, int ParentId, string Name, DateTimeOffset? StartedAt);
