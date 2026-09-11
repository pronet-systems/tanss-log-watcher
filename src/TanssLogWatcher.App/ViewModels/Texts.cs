using System.Globalization;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Die wenigen Formate, die auf mehr als einer Seite vorkommen.
/// </summary>
/// <remarks>
/// <para>Zusammengezogen, weil dieselbe Angabe sonst je Seite anders aussähe: eine Dauer als
/// „01:12“ hier und „72 Minuten“ dort liest sich wie zwei verschiedene Messwerte. Zahlen, die
/// nebeneinander in Spalten stehen, müssen zudem gleich breit sein.</para>
/// <para>Durchweg <see cref="CultureInfo.CurrentCulture"/> und nicht die invariante Kultur: Das
/// sind Anzeigetexte für einen deutschen Arbeitsplatz, keine Werte für eine Schnittstelle.</para>
/// </remarks>
internal static class Texts
{
    /// <summary>Uhrzeit ohne Datum, für Angaben aus den letzten Stunden.</summary>
    public static string Clock(DateTimeOffset moment) =>
        moment.ToString("HH:mm", CultureInfo.CurrentCulture);

    /// <summary>Tag und Uhrzeit, für alles, was älter sein kann als heute.</summary>
    public static string Moment(DateTimeOffset moment) =>
        moment.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture);

    /// <summary>Tag ohne Uhrzeit, für Fristen in Tagen.</summary>
    public static string Day(DateTimeOffset moment) =>
        moment.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);

    /// <summary>
    /// Eine laufende Dauer als <c>hh:mm:ss</c>.
    /// </summary>
    /// <remarks>
    /// Mit Sekunden, weil diese Anzeige tickt: Eine Dauer, die sich eine Minute lang nicht
    /// rührt, sieht aus wie eine stehengebliebene Anzeige.
    /// </remarks>
    public static string Ticking(TimeSpan value)
    {
        TimeSpan clamped = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        return string.Create(CultureInfo.CurrentCulture,
            $"{(int)clamped.TotalHours:00}:{clamped.Minutes:00}:{clamped.Seconds:00}");
    }

    /// <summary>Eine abgeschlossene Spanne in Worten, etwa „3 Stunden 12 Minuten“.</summary>
    public static string Span(TimeSpan value)
    {
        TimeSpan absolute = value < TimeSpan.Zero ? value.Negate() : value;

        if (absolute.TotalMinutes < 1)
        {
            return Count((int)absolute.TotalSeconds, "Sekunde", "Sekunden");
        }

        if (absolute.TotalHours < 1)
        {
            return Count((int)absolute.TotalMinutes, "Minute", "Minuten");
        }

        if (absolute.TotalDays < 1)
        {
            return string.Create(CultureInfo.CurrentCulture,
                $"{(int)absolute.TotalHours} Std. {absolute.Minutes} Min.");
        }

        return Count((int)absolute.TotalDays, "Tag", "Tage");
    }

    /// <summary>Ein Zeitpunkt in der Zukunft oder Vergangenheit, gemessen an <paramref name="now"/>.</summary>
    public static string Relative(DateTimeOffset moment, DateTimeOffset now)
    {
        TimeSpan difference = moment - now;
        return difference >= TimeSpan.Zero
            ? "in " + Span(difference)
            : "vor " + Span(difference);
    }

    /// <summary>Zahl mit passendem Ein- oder Mehrzahlwort.</summary>
    public static string Count(int value, string singular, string plural) =>
        string.Create(CultureInfo.CurrentCulture, $"{value} {(value == 1 ? singular : plural)}");

    /// <summary>Eine Zahl ohne Begleitwort, in der Kultur der Anzeige.</summary>
    public static string Number(int value) => value.ToString(CultureInfo.CurrentCulture);
}
