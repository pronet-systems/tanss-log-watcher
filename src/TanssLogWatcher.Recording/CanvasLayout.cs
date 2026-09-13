namespace TanssLogWatcher.Recording;

/// <summary>
/// Ein aufzunehmendes Fenster mit seiner Lage auf dem Bildschirm.
/// </summary>
/// <remarks>
/// Die Angaben sind Bildpunkte im Koordinatensystem aller Bildschirme — links und oben dürfen
/// deshalb negativ sein, wenn ein Bildschirm links vom oder über dem Hauptbildschirm steht.
/// </remarks>
/// <param name="Handle">Das Fensterhandle; es bleibt die Kennung der Quelle.</param>
/// <param name="Left">Der linke Rand.</param>
/// <param name="Top">Der obere Rand.</param>
/// <param name="Width">Die Breite in Bildpunkten.</param>
/// <param name="Height">Die Höhe in Bildpunkten.</param>
public readonly record struct WindowBox(nint Handle, int Left, int Top, int Width, int Height)
{
    /// <summary>Der rechte Rand, ausschliesslich.</summary>
    public int Right => Left + Width;

    /// <summary>Der untere Rand, ausschliesslich.</summary>
    public int Bottom => Top + Height;

    /// <summary>Hat das Fenster überhaupt eine Fläche?</summary>
    public bool HasArea => Width > 0 && Height > 0;
}

/// <summary>
/// Die Leinwand: wohin jedes Fenster im gemeinsamen Bild kopiert wird.
/// </summary>
/// <remarks>
/// <para><b>Ein Video, nicht eines je Fenster.</b> Eine Fernwartung besteht selten aus einem
/// einzigen Fenster — ein Dateiübertragungswerkzeug öffnet Anmelde- und Fortschrittsfenster,
/// und die gehören zur selben Arbeit. Gemessen wurde, dass die Fensteraufnahme von Windows
/// besessene Unterfenster <b>nicht</b> mitliefert: Ein grüner Dialog über einem roten
/// Hauptfenster ergab an seiner Stelle reines Rot. Wer nur das Hauptfenster aufnähme, verlöre
/// genau die Dialoge, um die es hinterher geht.</para>
///
/// <para>Deshalb wird jedes Fenster für sich aufgenommen und an seinen Platz in ein
/// gemeinsames Bild kopiert. Der Platz ergibt sich aus der Lage auf dem Bildschirm, damit das
/// Bild aussieht wie der Bildschirm — nur ohne alles, was nicht zur Sitzung gehört.</para>
///
/// <para><b>Die Grösse steht von Anfang an fest.</b> Der Encoder nimmt eine feste Auflösung
/// entgegen und keine wechselnde. Passt eine spätere Fensterlage nicht mehr hinein, wird die
/// Datei abgeschlossen und eine neue begonnen — nicht skaliert. Skalieren würde genau das
/// zerstören, wofür die Aufzeichnung da ist: lesbare Schrift.</para>
///
/// <para><b>Gerade Kantenlängen.</b> H.264 mit 4:2:0 verlangt sie, weil die Farbanteile auf
/// halber Auflösung liegen. Eine ungerade Breite lehnt der Encoder ab; die Leinwand rundet
/// deshalb auf.</para>
/// </remarks>
public sealed class CanvasLayout
{
    private CanvasLayout(int width, int height, int originLeft, int originTop,
                         IReadOnlyList<WindowPlacement> placements)
    {
        Width = width;
        Height = height;
        OriginLeft = originLeft;
        OriginTop = originTop;
        Placements = placements;
    }

    /// <summary>Die Breite der Leinwand in Bildpunkten; immer gerade.</summary>
    public int Width { get; }

    /// <summary>Die Höhe der Leinwand in Bildpunkten; immer gerade.</summary>
    public int Height { get; }

    /// <summary>Der Bildschirmpunkt, der links oben auf der Leinwand liegt.</summary>
    public int OriginLeft { get; }

    /// <summary>Der Bildschirmpunkt, der oben auf der Leinwand liegt.</summary>
    public int OriginTop { get; }

    /// <summary>Wohin jedes Fenster kopiert wird.</summary>
    public IReadOnlyList<WindowPlacement> Placements { get; }

    /// <summary>
    /// Legt die Leinwand für eine Fenstermenge an.
    /// </summary>
    /// <remarks>
    /// Die Leinwand ist die Hüllfläche aller Fenster. Fenster ohne Fläche — minimiert,
    /// geschlossen — zählen nicht mit; eine Menge, in der keines übrig bleibt, ergibt
    /// <c>null</c>, und der Aufrufer pausiert dann, statt eine leere Datei anzulegen.
    /// </remarks>
    /// <param name="windows">Die aufzunehmenden Fenster.</param>
    /// <returns>Die Leinwand, oder <c>null</c>, wenn nichts aufzunehmen ist.</returns>
    public static CanvasLayout? For(IReadOnlyList<WindowBox> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        List<WindowBox> usable = [.. windows.Where(w => w.HasArea)];

        if (usable.Count == 0)
        {
            return null;
        }

        int left = usable.Min(w => w.Left);
        int top = usable.Min(w => w.Top);
        int right = usable.Max(w => w.Right);
        int bottom = usable.Max(w => w.Bottom);

        int width = Even(right - left);
        int height = Even(bottom - top);

        List<WindowPlacement> placements = [.. usable
            .OrderBy(w => w.Top)
            .ThenBy(w => w.Left)
            .Select(w => new WindowPlacement(w.Handle, w.Left - left, w.Top - top,
                                             w.Width, w.Height))];

        return new CanvasLayout(width, height, left, top, placements);
    }

    /// <summary>
    /// Passt diese Fenstermenge noch auf die bestehende Leinwand?
    /// </summary>
    /// <remarks>
    /// Entscheidet über den Wechsel zu einer neuen Datei. Verschiebt sich ein Fenster innerhalb
    /// der Leinwand, ändert sich nur sein Platz — das kostet keine neue Datei. Wandert es
    /// darüber hinaus, ist die Leinwand zu klein, und weil ihre Grösse in der Datei festliegt,
    /// muss eine neue begonnen werden.
    /// </remarks>
    /// <param name="windows">Die neue Fensterlage.</param>
    /// <returns><c>true</c>, wenn alle Fenster hineinpassen.</returns>
    public bool Fits(IReadOnlyList<WindowBox> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        foreach (WindowBox window in windows)
        {
            if (!window.HasArea)
            {
                continue;
            }

            int x = window.Left - OriginLeft;
            int y = window.Top - OriginTop;

            if (x < 0 || y < 0 || x + window.Width > Width || y + window.Height > Height)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Berechnet die Plätze für eine neue Fensterlage auf dieser Leinwand.
    /// </summary>
    /// <remarks>
    /// Nur aufzurufen, wenn <see cref="Fits"/> zugestimmt hat. Fenster ohne Fläche fallen weg —
    /// ein minimiertes Fenster hinterlässt seinen Platz schwarz, statt das letzte Bild
    /// stehenzulassen. Ein stehengebliebenes Bild behauptete, dort sei noch etwas zu sehen.
    /// </remarks>
    /// <param name="windows">Die neue Fensterlage.</param>
    public IReadOnlyList<WindowPlacement> PlaceAll(IReadOnlyList<WindowBox> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        return [.. windows
            .Where(w => w.HasArea)
            .OrderBy(w => w.Top)
            .ThenBy(w => w.Left)
            .Select(w => new WindowPlacement(w.Handle, w.Left - OriginLeft, w.Top - OriginTop,
                                             w.Width, w.Height))];
    }

    // H.264 mit 4:2:0 legt die Farbanteile auf halbe Aufloesung; eine ungerade Kante hat dort
    // keinen ganzen Bildpunkt mehr und wird abgelehnt.
    private static int Even(int value) => value % 2 == 0 ? value : value + 1;
}

/// <summary>Wohin ein Fenster auf der Leinwand kopiert wird.</summary>
/// <param name="Handle">Das Fenster.</param>
/// <param name="X">Der linke Rand auf der Leinwand.</param>
/// <param name="Y">Der obere Rand auf der Leinwand.</param>
/// <param name="Width">Die Breite; unverändert, es wird nie skaliert.</param>
/// <param name="Height">Die Höhe; unverändert.</param>
public readonly record struct WindowPlacement(nint Handle, int X, int Y, int Width, int Height);
