using System.Globalization;
using System.Text;

namespace TanssLogWatcher.Cli.Output;

/// <summary>Der Befund einer einzelnen Prüfstufe.</summary>
/// <remarks>
/// Drei Stufen und nicht zwei: Der Unterschied zwischen „läuft, verlangt aber Aufmerksamkeit“
/// und „in diesem Zustand geht Arbeit verloren“ ist genau der, den eine Überwachung treffen
/// können muss. Er wird in <see cref="ExitCode"/> abgebildet.
/// </remarks>
public enum CheckLevel
{
    /// <summary>In Ordnung.</summary>
    Ok,

    /// <summary>Läuft, verlangt aber Aufmerksamkeit.</summary>
    Warn,

    /// <summary>Gestört.</summary>
    Fail,
}

/// <summary>Eine benannte Prüfstufe mit Befund und Begründung.</summary>
/// <param name="Name">Der Name der Stufe, wie er in der Ausgabe steht.</param>
/// <param name="Level">Der Befund.</param>
/// <param name="Message">
/// Was festgestellt wurde — und bei Warnung oder Fehler zusätzlich, warum das üblicherweise
/// passiert und was zu tun ist.
/// </param>
public sealed record CheckResult(string Name, CheckLevel Level, string Message);

/// <summary>
/// Die Ausgabe der Kommandozeile: Prüfstufen, Zeiten, Zahlwörter.
/// </summary>
/// <remarks>
/// An einer Stelle, damit jede Ausgabe gleich aussieht — und damit ein Umbruch nicht an vier
/// Stellen unterschiedlich gerät. Alle Texte sind deutsch; die Zahlen laufen über
/// <see cref="CultureInfo.InvariantCulture"/>, weil eine Uhrzeit im Protokoll nicht davon
/// abhängen darf, welche Regionseinstellung der Rechner gerade trägt.
/// </remarks>
public static class Report
{
    /// <summary>Breite, ab der eine Begründung umbrochen wird.</summary>
    private const int Width = 96;

    /// <summary>Die Einrückung der Fortsetzungszeilen — so breit wie die Marke.</summary>
    private const string Indent = "           ";

    /// <summary>Schreibt eine Prüfstufe mit Marke, Namen und umbrochener Begründung.</summary>
    public static void WriteCheck(TextWriter writer, CheckResult check)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(check);

        writer.WriteLine($"{Marker(check.Level)} {check.Name}");
        foreach (string line in Wrap(check.Message, Width - Indent.Length))
        {
            writer.WriteLine(Indent + line);
        }
    }

    /// <summary>Die Marke einer Stufe. Feste Breite, damit die Namen untereinander stehen.</summary>
    public static string Marker(CheckLevel level) => level switch
    {
        CheckLevel.Ok => "[in Ordnung]",
        CheckLevel.Warn => "[WARNUNG]   ",
        CheckLevel.Fail => "[FEHLER]    ",
        _ => "[?]         ",
    };

    /// <summary>Der Rückgabewert zu einem Befund.</summary>
    public static int ToExitCode(CheckLevel level) => level switch
    {
        CheckLevel.Ok => ExitCode.Healthy,
        CheckLevel.Warn => ExitCode.Warning,
        _ => ExitCode.Broken,
    };

    /// <summary>Ein Zeitpunkt in Ortszeit, auf die Sekunde genau.</summary>
    public static string Moment(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Nur die Uhrzeit — für die fortlaufende Anzeige von <c>watch</c>.</summary>
    public static string Clock(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Eine Dauer in deutscher Prosa, grob gerundet.
    /// </summary>
    /// <remarks>
    /// Grob mit Absicht: „noch 214 Tage“ ist eine Betriebsauskunft, keine Abrechnung. Eine
    /// Angabe auf die Sekunde genau täuschte eine Genauigkeit vor, die der Serveruhrabgleich
    /// gar nicht hergibt.
    /// </remarks>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            return "seit " + Duration(span.Negate()) + " abgelaufen";
        }

        if (span.TotalDays >= 2)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{span.TotalDays:0} Tage");
        }

        if (span.TotalHours >= 2)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{span.TotalHours:0} Stunden");
        }

        if (span.TotalMinutes >= 2)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:0} Minuten");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0} Sekunden");
    }

    /// <summary>Eine Dauer als <c>hh:mm:ss</c> — für Sitzungslängen.</summary>
    public static string Elapsed(TimeSpan span) =>
        span < TimeSpan.Zero
            ? "00:00:00"
            : string.Create(CultureInfo.InvariantCulture,
                $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}");

    /// <summary>
    /// Kürzt einen Text für eine Tabellenspalte und setzt ein Auslassungszeichen.
    /// </summary>
    /// <param name="text">Der Text; <c>null</c> ergibt eine leere Zelle.</param>
    /// <param name="max">Die Höchstbreite einschließlich Auslassungszeichen.</param>
    public static string Ellipsis(string? text, int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 2);

        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string single = text.ReplaceLineEndings(" ");
        return single.Length <= max ? single : single[..(max - 1)] + "…";
    }

    /// <summary>Bricht einen Satz an Wortgrenzen um.</summary>
    /// <remarks>
    /// Von Hand, weil die Begründungen lange, ganze Sätze sind: Ein harter Umbruch mitten im
    /// Wort machte sie schwerer lesbar als gar keinen.
    /// </remarks>
    public static IReadOnlyList<string> Wrap(string? text, int width)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 16);

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<string> lines = [];
        foreach (string paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            StringBuilder line = new();
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    lines.Add(line.ToString());
                    _ = line.Clear();
                }

                if (line.Length > 0)
                {
                    _ = line.Append(' ');
                }

                _ = line.Append(word);
            }

            if (line.Length > 0)
            {
                lines.Add(line.ToString());
            }
        }

        return lines;
    }
}
