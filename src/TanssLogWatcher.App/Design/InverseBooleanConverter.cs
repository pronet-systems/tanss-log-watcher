using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TanssLogWatcher.App.Design;

/// <summary>
/// Kehrt einen Wahrheitswert um — für Schaltflächen, die genau dann gesperrt sind, wenn
/// gerade etwas läuft.
/// </summary>
/// <remarks>
/// Ausdrücklich ein Konverter und kein zweites Feld im Ansichtsmodell: Ein <c>IsNotBusy</c>
/// neben <c>IsBusy</c> ist ein Wert, der doppelt geführt und irgendwann einmal nicht
/// mitgesetzt wird. Der Konverter kann nicht auseinanderlaufen.
/// </remarks>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && !flag;

    /// <inheritdoc />
    /// <remarks>
    /// Wird gebraucht, sobald jemand gegen eine Zweiwegbindung setzt — etwa einen Schalter, der
    /// „angehalten“ zeigt und „aktiv“ meint. Deshalb umgesetzt statt geworfen.
    /// </remarks>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && !flag;
}

/// <summary>
/// Blendet ein, solange etwas <b>nicht</b> zutrifft — das Gegenstück zu
/// <see cref="System.Windows.Controls.BooleanToVisibilityConverter"/>.
/// </summary>
/// <remarks>
/// Gebraucht überall dort, wo zwei Plaketten am selben Platz stehen und einander ablösen:
/// „gültig“ verschwindet, sobald „beanstandet“ erscheint. Mit zwei gegenläufigen Bindungen auf
/// denselben Wert ist ausgeschlossen, dass beide zugleich sichtbar sind — bei zwei getrennten
/// Eigenschaften im Ansichtsmodell wäre es das nicht.
/// </remarks>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility visibility && visibility != Visibility.Visible;
}
