using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TanssLogWatcher.App.Design;

/// <summary>
/// Macht aus der Farbangabe einer TANSS-Fernwartungsanbindung einen Pinsel.
/// </summary>
/// <remarks>
/// TANSS führt die Farbe in <c>fernwartung_ext.farbe_hg</c> als sechs Hexziffern <b>ohne</b>
/// Raute, etwa <c>00bfff</c>. Sie wird hier übernommen, damit der Techniker im Werkzeug dieselbe
/// Farbe sieht wie im TANSS-Kalender — eine eigene Farbgebung wäre eine zweite Wahrheit.
///
/// <para>Eine unlesbare Angabe ergibt einen neutralen Grauton statt einer Ausnahme: eine falsch
/// gepflegte Farbe ist kein Grund, eine Liste nicht anzuzeigen.</para>
/// </remarks>
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Fallback = CreateFrozen(Color.FromRgb(0x7A, 0x88, 0x96));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || hex.Length == 0)
        {
            return Fallback;
        }

        ReadOnlySpan<char> digits = hex.AsSpan().TrimStart('#');
        if (digits.Length != 6
            || !byte.TryParse(digits[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
            || !byte.TryParse(digits[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
            || !byte.TryParse(digits[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return Fallback;
        }

        return CreateFrozen(Color.FromRgb(r, g, b));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Die Farbe wird nur gelesen.");

    // Eingefroren, weil der Pinsel je Zeile neu entsteht und sonst unnoetig veraenderlich bliebe.
    private static SolidColorBrush CreateFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
