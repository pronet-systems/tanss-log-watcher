namespace TanssLogWatcher.Monitoring;

/// <summary>Feste Zeichenketten und Zeitmaße der Sitzungserkennung.</summary>
public static class SessionConstants
{
    /// <summary>
    /// Das Ziel einer Sitzung, deren Prozess erkannt wurde, aber noch kein brauchbares Fenster
    /// zeigt. Der Platzhalter ist kein Schönheitsfehler, sondern tragendes Element: eine Sitzung
    /// mit Platzhalter nimmt später das erste echte Ziel desselben Prozesses an und behält dabei
    /// ihren Beginn. Ohne ihn ginge die Zeit zwischen Programmstart und erstem Fenstertitel
    /// verloren.
    /// <para>Wortlaut aus der Vorlage; er wird auch in bereits hochgeladenen Kommentaren
    /// wiedererkannt und darf deshalb nicht geändert werden.</para>
    /// </summary>
    public const string NoDestinationTitle = "<<< Kein Titel ermittelt >>>";

    /// <summary>
    /// Voreingestellter Abstand zwischen zwei Durchläufen. Zugleich das Maß dafür, ob ein Prozess
    /// als „gerade gestartet“ gilt und seine echte Startzeit die Sitzung beginnt.
    /// </summary>
    public static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Karenz für Profile, die das Ziel aus Netzverbindungen lesen. In den ersten Minuten baut
    /// eine solche Anwendung Verbindungen zu Lizenz- und Aktualisierungsdiensten auf, die nichts
    /// mit der Fernwartung zu tun haben und sonst als Ziel erschienen.
    /// </summary>
    public static readonly TimeSpan DefaultIpGracePeriod = TimeSpan.FromMinutes(3);
}
