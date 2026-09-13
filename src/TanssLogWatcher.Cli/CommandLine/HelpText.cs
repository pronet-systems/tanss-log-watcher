using System.Globalization;
using System.Reflection;

namespace TanssLogWatcher.Cli.CommandLine;

/// <summary>
/// Die Hilfe und die Meldung, die ein Befehl ohne Konfiguration ausgibt.
/// </summary>
/// <remarks>
/// Beides steht an <b>einer</b> Stelle, weil beides dieselbe Frage beantwortet: „Was soll ich
/// jetzt tun?“ Eine Hilfe, die je Befehl neu formuliert wird, driftet auseinander — und
/// ausgerechnet der Hinweis auf die fehlende Einrichtung darf nicht davon abhängen, welchen
/// Befehl jemand zuerst probiert hat.
/// </remarks>
public static class HelpText
{
    /// <summary>Die Version des Werkzeugs, so wie sie in der Baugruppe steht.</summary>
    public static string Version =>
        typeof(HelpText).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? typeof(HelpText).Assembly.GetName().Version?.ToString()
        ?? "unbekannt";

    /// <summary>Schreibt die vollständige Hilfe.</summary>
    public static void Write(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"tanss-logwatch {Version} — TANSS Log-Watcher"));
        writer.WriteLine();
        writer.WriteLine("Erkennt Fernwartungssitzungen auf diesem Rechner und schreibt sie in die");
        writer.WriteLine("TANSS-Instanz des Kunden.");
        writer.WriteLine();
        writer.WriteLine("Aufruf:");
        writer.WriteLine("  tanss-logwatch <Befehl> [Optionen]");
        writer.WriteLine();
        writer.WriteLine("Befehle:");
        writer.WriteLine("  doctor                  Prüft Konfiguration, Erreichbarkeit, Token,");
        writer.WriteLine("                          Erneuerungsfähigkeit, Modul Fernwartung,");
        writer.WriteLine("                          Mitarbeiter-ID und Warteschlange.");
        writer.WriteLine("  watch [--dry-run]       Beobachtet laufend und reiht fertige Sitzungen ein.");
        writer.WriteLine("                          --dry-run zeigt nur, was geschähe. Ohne Zuordnung");
        writer.WriteLine("                          in der Konfiguration ist der Trockenlauf ohnehin");
        writer.WriteLine("                          der Standard. Strg-C beendet geordnet: laufende");
        writer.WriteLine("                          Sitzungen werden gesichert, nicht verworfen.");
        writer.WriteLine("  windows [--profile <Schlüssel>]");
        writer.WriteLine("                          Listet alle sichtbaren Fenster mit Prozess, PID,");
        writer.WriteLine("                          Handle, Titel, Hauptfenster-Kennzeichen und");
        writer.WriteLine("                          Netzverbindungen. Mit --profile zusätzlich, welches");
        writer.WriteLine("                          Ziel dieses Profil daraus ableiten würde.");
        writer.WriteLine("  queue [--flush]         Zeigt die Warteschlange. --flush sendet die fälligen");
        writer.WriteLine("                          Einträge; vor jeder Wiederholung fragt es erst, ob");
        writer.WriteLine("                          die Fernwartung schon in TANSS steht.");
        writer.WriteLine("  token status            Restlaufzeit, Erneuerungsfähigkeit, nächster Termin.");
        writer.WriteLine("  token rotate            Prägt ein neues Token, prüft es mit einem echten");
        writer.WriteLine("                          Aufruf und übernimmt es erst dann.");
        writer.WriteLine();
        writer.WriteLine("Allgemein:");
        writer.WriteLine("  --help, -h              Diese Hilfe.");
        writer.WriteLine("  --version               Version ausgeben.");
        writer.WriteLine();
        writer.WriteLine("Rückgabewerte (der Überwachungsvertrag von doctor):");
        writer.WriteLine("  0   gesund");
        writer.WriteLine("  1   Warnung — läuft, verlangt aber Aufmerksamkeit");
        writer.WriteLine("  2   gestört — in diesem Zustand geht Arbeit verloren oder kommt nicht an");
        writer.WriteLine("  64  Aufruffehler — unbekannter Befehl oder fehlendes Argument");
    }

    /// <summary>
    /// Die Meldung für einen Befehl, der ohne brauchbare Konfiguration aufgerufen wurde.
    /// </summary>
    /// <remarks>
    /// <para>Sie ist bewusst keine Ausnahme: Der erste Aufruf auf einem frischen Rechner ist
    /// der Normalfall, nicht die Störung. Ein Stapelaufruf, der hier abstürzt, sieht aus wie
    /// ein kaputtes Werkzeug.</para>
    /// <para>Genannt werden drei Dinge — was fehlt, wo es hingehört und womit man es anlegt.
    /// Der Einrichtungsassistent (<c>setup</c>) ist noch nicht gebaut; solange verweist die
    /// Meldung auf die Beispieldatei, die neben dem Programm liegt.</para>
    /// </remarks>
    /// <param name="writer">Wohin die Meldung geht, üblicherweise die Standardfehlerausgabe.</param>
    /// <param name="configPath">Der Ort, an dem die Konfiguration erwartet wird.</param>
    /// <param name="problem">Was genau nicht ging.</param>
    public static void WriteMissingConfiguration(TextWriter writer, string configPath, string problem)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine("Die Einrichtung fehlt oder ist unvollständig.");
        writer.WriteLine();
        writer.WriteLine(problem);
        writer.WriteLine();
        writer.WriteLine("Erwartet wird die Konfiguration unter:");
        writer.WriteLine("  " + configPath);
        writer.WriteLine();
        writer.WriteLine("Anzulegen ist sie mit dem Einrichtungsassistenten:");
        writer.WriteLine("  tanss-logwatch setup");
        writer.WriteLine();
        writer.WriteLine("Diesen Befehl gibt es in dieser Version noch nicht. Bis dahin ist die");
        writer.WriteLine("Beispieldatei zu kopieren und anzupassen — sie ist vollständig kommentiert:");
        writer.WriteLine("  " + ExamplePath());
        writer.WriteLine();
        writer.WriteLine("Danach prüft „tanss-logwatch doctor“, ob die Einrichtung trägt.");
    }

    /// <summary>
    /// Der Pfad der Beispielkonfiguration.
    /// </summary>
    /// <remarks>
    /// Sie wird neben das Programm gelegt. Fehlt sie dort — etwa weil jemand nur die
    /// ausführbare Datei kopiert hat —, wird der Name trotzdem genannt: Er ist auch ohne Pfad
    /// die Antwort auf „wonach suche ich?“.
    /// </remarks>
    public static string ExamplePath()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "config.example.json");
        return File.Exists(beside) ? beside : "config.example.json (im Quellbaum des Werkzeugs)";
    }
}
