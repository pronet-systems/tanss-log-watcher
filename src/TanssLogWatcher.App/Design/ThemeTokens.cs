using System.Collections.ObjectModel;
using System.Windows;
using Wpf.Ui.Appearance;

namespace TanssLogWatcher.App.Design;

/// <summary>
/// Hält die eigenen Bedeutungsfarben an der Darstellung von WPF-UI.
/// </summary>
/// <remarks>
/// <para>WPF-UI tauscht beim Wechsel zwischen heller und dunkler Darstellung die Quelle seines
/// eigenen <c>ui:ThemesDictionary</c> aus. Fremde Wörterbücher rührt es nicht an — die eigenen
/// Bedeutungsfarben blieben also auf dem Stand des Programmstarts stehen. Hier wird dasselbe
/// für <c>Tokens.Dark.xaml</c> und <c>Tokens.Light.xaml</c> nachgezogen.</para>
/// <para>Der Weg über zwei Dateien und einen Tausch ist der einfachste, der trägt: die
/// Ansichten sprechen die Farben unter unveränderten Schlüsseln über <c>DynamicResource</c> an
/// und merken vom Tausch nichts.</para>
/// </remarks>
internal static class ThemeTokens
{
    private const string DarkSource = "pack://application:,,,/Design/Tokens.Dark.xaml";
    private const string LightSource = "pack://application:,,,/Design/Tokens.Light.xaml";

    /// <summary>
    /// Gleicht die Bedeutungsfarben einmal an und folgt danach jedem Wechsel der Darstellung.
    /// </summary>
    public static void Track()
    {
        Apply(ApplicationThemeManager.GetAppTheme());
        ApplicationThemeManager.Changed += (theme, _) => Apply(theme);
    }

    private static void Apply(ApplicationTheme theme)
    {
        // Unknown tritt auf, wenn WPF-UI die Darstellung nicht ermitteln konnte. Dunkel ist
        // dann die richtige Annahme: App.xaml startet dunkel, ein Tausch waere ein Flackern
        // ohne Gewinn.
        string wanted = theme == ApplicationTheme.Light ? LightSource : DarkSource;

        Collection<ResourceDictionary> merged = Application.Current.Resources.MergedDictionaries;
        for (int i = 0; i < merged.Count; i++)
        {
            string? source = merged[i].Source?.OriginalString;
            if (source is null || !IsTokenSource(source))
            {
                continue;
            }

            if (!string.Equals(source, wanted, StringComparison.OrdinalIgnoreCase))
            {
                // Ersetzen statt Source setzen: ein Austausch des Eintrags stoesst die
                // Neuaufloesung aller DynamicResource-Verweise zuverlaessig an.
                merged[i] = new ResourceDictionary { Source = new Uri(wanted, UriKind.Absolute) };
            }

            return;
        }
    }

    private static bool IsTokenSource(string source) =>
        source.EndsWith("Tokens.Dark.xaml", StringComparison.OrdinalIgnoreCase)
        || source.EndsWith("Tokens.Light.xaml", StringComparison.OrdinalIgnoreCase);
}
