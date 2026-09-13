namespace TanssLogWatcher.Recording;

/// <summary>Ein Bildschirm samt seinem Handle.</summary>
/// <param name="Handle">Das Bildschirmhandle (<c>HMONITOR</c>) — nur die Aufnahme braucht es.</param>
/// <param name="Box">Die Lage im Koordinatensystem aller Bildschirme.</param>
/// <param name="IsPrimary">Ist das der Hauptbildschirm?</param>
public readonly record struct ScreenInfo(nint Handle, ScreenBox Box, bool IsPrimary);

/// <summary>
/// Woher die angeschlossenen Bildschirme kommen.
/// </summary>
/// <remarks>
/// <para><b>Warum die Bildschirme überhaupt eine eigene Quelle sind.</b> Die Leinwand ist seit
/// dieser Version die Bildschirmfläche und nicht mehr die Hüllfläche der Fenster — nur deshalb
/// kostet ein Verschieben, Vergrössern oder Maximieren keine neue Datei mehr. Der Direktor, der
/// darüber entscheidet, darf aber kein Windows kennen; sonst liesse sich die Entscheidung nur
/// mit einem Bildschirm prüfen, und das hiesse: gar nicht.</para>
/// <para>Die Schnittstelle trennt also wie bei den Fenstern das Prüfbare vom Unprüfbaren: Der
/// Direktor rechnet gegen erfundene Bildschirmlagen, die echte Aufzählung braucht Windows.</para>
/// </remarks>
public interface IScreenSource
{
    /// <summary>
    /// Die angeschlossenen Bildschirme.
    /// </summary>
    /// <remarks>
    /// Je Takt neu gefragt und nicht einmal gemerkt: Ein Bildschirm kommt mitten in einer
    /// Fernwartung dazu oder fällt weg, wenn jemand sein Notebook andockt. Eine gemerkte Liste
    /// führte dann auf einen Bildschirm, den es nicht mehr gibt.
    /// </remarks>
    /// <returns>Die Bildschirme; leer, wenn sich keiner ermitteln lässt.</returns>
    IReadOnlyList<ScreenInfo> Screens();
}
