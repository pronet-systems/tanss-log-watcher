using System.Net;
using TanssLogWatcher.Monitoring.Model;

namespace TanssLogWatcher.Monitoring;

/// <summary>
/// Liefert die sichtbaren Fenster der obersten Ebene.
/// </summary>
/// <remarks>
/// Eine eigene Schnittstelle, damit die Zustandsmaschine ohne Windows und ohne echte Fenster
/// geprüft werden kann.
/// <para><b>Scheitern wird geworfen, nicht durch eine leere Liste gemeldet.</b> Eine leere Liste
/// hat eine eigene Bedeutung — es gibt gerade keine sichtbaren Fenster — und würde als Ende aller
/// laufenden Sitzungen gelesen. Eine Ausnahme dagegen lässt die Zustandsmaschine den Durchlauf
/// überspringen und den Bestand unberührt.</para>
/// </remarks>
public interface IWindowSource
{
    /// <summary>Alle sichtbaren Fenster der obersten Ebene mit nicht leerem Titel.</summary>
    IReadOnlyList<WindowInfo> GetVisibleWindows();
}

/// <summary>
/// Liefert eine Momentaufnahme der laufenden Prozesse.
/// </summary>
/// <remarks>
/// <b>Genau eine Momentaufnahme je Durchlauf.</b> Zweimal WMI je überwachtem Prozess kostet
/// gemessen 172 bis 220 ms je Abfrage. Die vollständige Tabelle aus
/// <c>CreateToolhelp32Snapshot</c> kostet 16 bis 24 ms und beantwortet dieselbe Frage.
/// </remarks>
public interface IProcessSource
{
    /// <summary>Alle Prozesse, die zum Aufrufzeitpunkt sichtbar sind.</summary>
    IReadOnlyList<ProcessInfo> Snapshot();
}

/// <summary>
/// Liefert die Gegenstellen der offenen TCP-Verbindungen eines Prozesses.
/// </summary>
public interface ITcpConnectionSource
{
    /// <summary>
    /// Die Gegenstellen des Prozesses und der genannten Kindprozesse.
    /// </summary>
    /// <remarks>
    /// Aussortiert werden Schleifenadressen, <c>0.0.0.0</c> und <c>255.255.255.255</c> sowie die
    /// Zustände <c>TIME_WAIT</c> und <c>CLOSE_WAIT</c>. Ohne die Zustandsprüfung hielten
    /// nachhängende Verbindungen eine Sitzung noch Minuten nach dem Trennen am Leben.
    /// </remarks>
    /// <param name="processId">Der beobachtete Prozess.</param>
    /// <param name="childIds">Dessen Kindprozesse; leer, wenn es keine gibt.</param>
    IReadOnlyList<IPAddress> GetRemoteAddresses(int processId, IEnumerable<int> childIds);
}

/// <summary>
/// Löst Adressen rückwärts in Namen auf.
/// </summary>
/// <remarks>
/// <b>Im Durchlauf wird nie gewartet.</b> Eine Rückwärtsauflösung kann sekundenlang hängen; mitten
/// in der Schleife ließe sie die gesamte Erkennung stehen. Deshalb zwei Wege:
/// <see cref="GetCachedHostName"/> antwortet sofort und stößt im Fehlfall die Auflösung im
/// Hintergrund an, <see cref="ResolveAsync"/> wartet tatsächlich.
/// </remarks>
public interface IHostNameResolver
{
    /// <summary>
    /// Der zwischengespeicherte Name, sonst <c>null</c>. Ein Fehltreffer stößt die Auflösung im
    /// Hintergrund an und kehrt sofort zurück.
    /// </summary>
    /// <param name="address">Die gesuchte Adresse.</param>
    string? GetCachedHostName(IPAddress address);

    /// <summary>
    /// Löst auf und füllt dabei den Zwischenspeicher. <c>null</c>, wenn kein Name zu ermitteln ist.
    /// </summary>
    /// <param name="address">Die gesuchte Adresse.</param>
    /// <param name="ct">Abbruchsteuerung.</param>
    Task<string?> ResolveAsync(IPAddress address, CancellationToken ct = default);
}
