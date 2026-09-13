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
/// Ein Bildschirm mit seiner Lage im Koordinatensystem aller Bildschirme.
/// </summary>
/// <remarks>
/// Die ganze Fläche einschliesslich Taskleiste, nicht der Arbeitsbereich. Gemessen: Die
/// Bildschirmaufnahme von Windows liefert genau diese Fläche — wer den Arbeitsbereich nähme,
/// bekäme eine Leinwand, die um die Höhe der Taskleiste zu klein ist.
/// </remarks>
/// <param name="Left">Der linke Rand.</param>
/// <param name="Top">Der obere Rand.</param>
/// <param name="Width">Die Breite in Bildpunkten.</param>
/// <param name="Height">Die Höhe in Bildpunkten.</param>
public readonly record struct ScreenBox(int Left, int Top, int Width, int Height)
{
    /// <summary>Der rechte Rand, ausschliesslich.</summary>
    public int Right => Left + Width;

    /// <summary>Der untere Rand, ausschliesslich.</summary>
    public int Bottom => Top + Height;

    /// <summary>Hat der Bildschirm eine Fläche?</summary>
    public bool HasArea => Width > 0 && Height > 0;

    /// <summary>Wie viele Bildpunkte dieses Fensters auf diesem Bildschirm liegen.</summary>
    /// <param name="window">Das Fenster.</param>
    public long Overlap(WindowBox window)
    {
        long width = Math.Min(Right, window.Right) - Math.Max(Left, window.Left);
        long height = Math.Min(Bottom, window.Bottom) - Math.Max(Top, window.Top);

        return width > 0 && height > 0 ? width * height : 0;
    }
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
/// <para><b>Die Leinwand ist der Bildschirm, nicht die Hüllfläche der Fenster.</b> Das war
/// einmal anders, und es war der Grund für den Ärger: Die Hüllfläche steht im Augenblick des
/// Beginns fest, die Bildgrösse liegt im Encoder fest — also musste jedes Verschieben und jedes
/// Vergrössern eine neue Datei beginnen. Im Betrieb gemessen: Eine Fernwartung von neunzehn
/// Sekunden ergab drei Dateien, weil ein Remotedesktop-Fenster beim Verbindungsaufbau zweimal
/// seine Grösse ändert. Mit der Bildschirmfläche als Leinwand passt jede Lage, die auf den
/// Bildschirm passt — und das ist jede.</para>
///
/// <para><b>Der Ursprung wandert, die Grösse nie.</b> Zieht der Techniker die Sitzung auf einen
/// anderen Bildschirm, wird nur der Ursprung verschoben (<see cref="MovedTo"/>); die Bildgrösse
/// bleibt, und deshalb kostet der Wechsel keine neue Datei. Ein grösserer Bildschirm wird dabei
/// beschnitten — die Grösse einer laufenden Datei lässt sich nicht ändern, und eine zweite Datei
/// ist genau das, was hier vermieden werden soll.</para>
///
/// <para><b>Gerade Kantenlängen.</b> H.264 mit 4:2:0 verlangt sie, weil die Farbanteile auf
/// halber Auflösung liegen. Eine ungerade Breite lehnt der Encoder ab; die Leinwand rundet
/// deshalb auf.</para>
/// </remarks>
public sealed class CanvasLayout
{
    /// <summary>
    /// Die grösste Kantenlänge, die der Encoder auf dem Prüfrechner angenommen hat.
    /// </summary>
    /// <remarks>
    /// Gemessen, nicht aus einer Tabelle abgeschrieben: 8192×4352 wurde angenommen,
    /// 8192×4354 und 8194×2160 mit <c>0xC00D36B4</c> abgelehnt. Auf einem anderen Rechner kann
    /// die Schranke niedriger liegen — deshalb ist dies nur die erste Prüfung; die zweite ist
    /// der Rückfall, wenn der Encoder eine Grösse tatsächlich ablehnt.
    /// </remarks>
    public const int MaximumEdge = 8192;

    /// <summary>Die grösste Fläche in Bildpunkten, die der Encoder angenommen hat.</summary>
    public const long MaximumArea = 8192L * 4352L;

    private CanvasLayout(int width, int height, int originLeft, int originTop)
    {
        Width = width;
        Height = height;
        OriginLeft = originLeft;
        OriginTop = originTop;
    }

    /// <summary>Die Breite der Leinwand in Bildpunkten; immer gerade.</summary>
    public int Width { get; }

    /// <summary>Die Höhe der Leinwand in Bildpunkten; immer gerade.</summary>
    public int Height { get; }

    /// <summary>Der Bildschirmpunkt, der links oben auf der Leinwand liegt.</summary>
    public int OriginLeft { get; }

    /// <summary>Der Bildschirmpunkt, der oben auf der Leinwand liegt.</summary>
    public int OriginTop { get; }

    /// <summary>Der Bereich des Bildschirms, den die Leinwand gerade abbildet.</summary>
    public ScreenBox Area => new(OriginLeft, OriginTop, Width, Height);

    /// <summary>
    /// Legt die Leinwand als Hüllfläche der Fenster an.
    /// </summary>
    /// <remarks>
    /// <b>Nur noch der Rückfall</b>, wenn sich kein Bildschirm ermitteln lässt — auf einer
    /// Dienstsitzung ohne Bildschirm etwa. Im Normalfall gilt <see cref="ForScreens"/>: Eine
    /// Leinwand, die genau um die Fenster herumliegt, muss bei jeder Bewegung wechseln, und
    /// jeder Wechsel kostet eine Datei.
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

        return new CanvasLayout(Even(right - left), Even(bottom - top), left, top);
    }

    /// <summary>
    /// Legt die Leinwand auf die Bildschirmfläche, auf der die Sitzung liegt.
    /// </summary>
    /// <remarks>
    /// <para>Genommen werden die Bildschirme, auf denen mindestens ein Fenster der Sitzung
    /// tatsächlich Fläche hat. Ein Fenster, das über den Bildschirmrand hinausragt — ein
    /// maximiertes Fenster meldet gemessen (−13,−13) bei 2906×1730 auf einem Bildschirm von
    /// 2880×1800 —, vergrössert die Leinwand <b>nicht</b>: Was ausserhalb des Bildschirms liegt,
    /// ist auch auf dem Bildschirm nicht zu sehen.</para>
    ///
    /// <para>Überschreitet die Hüllfläche mehrerer Bildschirme, was der Encoder annimmt, wird
    /// auf den Bildschirm mit der grössten Überdeckung zurückgefallen, und danach auf die
    /// Hüllfläche der Fenster. Eine Aufzeichnung, die klein beginnt, ist besser als keine.</para>
    /// </remarks>
    /// <param name="windows">Die aufzunehmenden Fenster.</param>
    /// <param name="screens">Die angeschlossenen Bildschirme.</param>
    /// <returns>Die Leinwand, oder <c>null</c>, wenn nichts aufzunehmen ist.</returns>
    public static CanvasLayout? ForScreens(IReadOnlyList<WindowBox> windows,
                                           IReadOnlyList<ScreenBox> screens)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(screens);

        List<WindowBox> usable = [.. windows.Where(w => w.HasArea)];

        if (usable.Count == 0)
        {
            return null;
        }

        List<ScreenBox> touched = [.. screens
            .Where(s => s.HasArea && usable.Any(w => s.Overlap(w) > 0))];

        if (touched.Count == 0)
        {
            return For(usable);
        }

        int left = touched.Min(s => s.Left);
        int top = touched.Min(s => s.Top);
        int right = touched.Max(s => s.Right);
        int bottom = touched.Max(s => s.Bottom);

        if (Accepts(right - left, bottom - top))
        {
            return new CanvasLayout(Even(right - left), Even(bottom - top), left, top);
        }

        // Zu gross fuer den Encoder: der Bildschirm, auf dem am meisten von der Sitzung liegt.
        ScreenBox best = touched
            .OrderByDescending(s => usable.Sum(w => s.Overlap(w)))
            .First();

        return Accepts(best.Width, best.Height)
            ? new CanvasLayout(Even(best.Width), Even(best.Height), best.Left, best.Top)
            : For(usable);
    }

    /// <summary>
    /// Dieselbe Leinwand an einer anderen Stelle des Bildschirmsystems.
    /// </summary>
    /// <remarks>
    /// <b>Der ganze Monitorwechsel.</b> Die Bildgrösse liegt in der laufenden Datei fest, der
    /// Ursprung nicht — deshalb kostet ein Wechsel des Bildschirms nur eine Verschiebung und
    /// keine neue Datei.
    /// </remarks>
    /// <param name="originLeft">Der neue linke Rand im Bildschirmsystem.</param>
    /// <param name="originTop">Der neue obere Rand im Bildschirmsystem.</param>
    public CanvasLayout MovedTo(int originLeft, int originTop) =>
        originLeft == OriginLeft && originTop == OriginTop
            ? this
            : new CanvasLayout(Width, Height, originLeft, originTop);

    /// <summary>
    /// Liegt diese Fenstermenge vollständig auf der Leinwand?
    /// </summary>
    /// <remarks>
    /// <b>Eine Auskunft, keine Entscheidung mehr.</b> Früher entschied diese Frage über einen
    /// Dateiwechsel; das ist vorbei — sie schlägt bei einem maximierten Fenster gemessen
    /// ohnehin fehl, weil dessen unsichtbarer Rahmen über den Bildschirm hinausragt. Was
    /// hinausragt, wird beim Zeichnen beschnitten; die Antwort hier dient nur dem Vermerk in
    /// der Begleitdatei.
    /// </remarks>
    /// <param name="windows">Die Fensterlage.</param>
    /// <returns><c>true</c>, wenn nichts abgeschnitten wird.</returns>
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
    /// Berechnet die Plätze für die aktuelle Fensterlage.
    /// </summary>
    /// <remarks>
    /// <b>Je Bild neu.</b> Früher merkte sich die Leinwand die Plätze aus dem Augenblick ihres
    /// Anlegens; das ging nur deshalb durch, weil bei jeder Bewegung eine neue Leinwand
    /// entstand. Ein Fenster, das sich innerhalb der Leinwand bewegte, klebte im Video an
    /// seiner alten Stelle, und ein mitten in der Sitzung geöffneter Dialog erschien überhaupt
    /// nicht.
    /// </remarks>
    /// <param name="windows">Die aktuelle Fensterlage.</param>
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

    /// <summary>Nimmt der Encoder diese Grösse voraussichtlich an?</summary>
    private static bool Accepts(int width, int height) =>
        width > 0 && height > 0
        && Even(width) <= MaximumEdge && Even(height) <= MaximumEdge
        && (long)Even(width) * Even(height) <= MaximumArea;

    // H.264 mit 4:2:0 legt die Farbanteile auf halbe Aufloesung; eine ungerade Kante hat dort
    // keinen ganzen Bildpunkt mehr und wird abgelehnt.
    private static int Even(int value) => value % 2 == 0 ? value : value + 1;
}

/// <summary>Wohin ein Fenster auf der Leinwand kopiert wird.</summary>
/// <remarks>
/// <see cref="X"/> und <see cref="Y"/> dürfen negativ sein: Ein maximiertes Fenster ragt
/// gemessen um dreizehn Bildpunkte über jeden Bildschirmrand hinaus — das ist sein unsichtbarer
/// Anfassrahmen. Beschnitten wird beim Zeichnen, nicht hier.
/// </remarks>
/// <param name="Handle">Das Fenster.</param>
/// <param name="X">Der linke Rand auf der Leinwand; darf negativ sein.</param>
/// <param name="Y">Der obere Rand auf der Leinwand; darf negativ sein.</param>
/// <param name="Width">Die Breite; unverändert, es wird nie skaliert.</param>
/// <param name="Height">Die Höhe; unverändert.</param>
public readonly record struct WindowPlacement(nint Handle, int X, int Y, int Width, int Height);
