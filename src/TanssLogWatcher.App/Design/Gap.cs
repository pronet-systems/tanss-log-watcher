using System.Windows;
using System.Windows.Controls;

namespace TanssLogWatcher.App.Design;

/// <summary>
/// Abstand zwischen den Kindern eines <see cref="StackPanel"/>.
/// </summary>
/// <remarks>
/// WinUI und WPF-UI kennen dafür <c>Spacing</c>; WPF selbst nicht. Ohne diese Ergänzung müsste
/// an jedem einzelnen Kind ein Rand gesetzt werden — und beim Umsortieren bliebe der Rand am
/// falschen Element stehen. Hier wird er beim Laden aus der Ausrichtung des Panels abgeleitet
/// und beim letzten Kind weggelassen, damit unten beziehungsweise rechts kein toter Rand bleibt.
/// </remarks>
public static class Gap
{
    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.RegisterAttached(
            "Size", typeof(double), typeof(Gap),
            new PropertyMetadata(0d, OnSizeChanged));

    public static double GetSize(DependencyObject element) =>
        (double)element.GetValue(SizeProperty);

    public static void SetSize(DependencyObject element, double value) =>
        element.SetValue(SizeProperty, value);

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not StackPanel panel)
        {
            return;
        }

        // Beim Setzen im XAML sind die Kinder noch nicht da. Layoutaktualisierung statt Loaded:
        // Loaded feuert bei einem Panel innerhalb eines DataTemplate nicht zuverlaessig erneut,
        // wenn die Vorlage wiederverwendet wird.
        panel.LayoutUpdated -= Apply;
        panel.LayoutUpdated += Apply;
        Apply(panel, EventArgs.Empty);

        void Apply(object? sender, EventArgs _)
        {
            double gap = GetSize(panel);
            bool horizontal = panel.Orientation == Orientation.Horizontal;

            for (int i = 0; i < panel.Children.Count; i++)
            {
                if (panel.Children[i] is not FrameworkElement child)
                {
                    continue;
                }

                bool last = i == panel.Children.Count - 1;
                Thickness wanted = horizontal
                    ? new Thickness(0, 0, last ? 0 : gap, 0)
                    : new Thickness(0, 0, 0, last ? 0 : gap);

                // Ein im XAML ausdruecklich gesetzter Rand hat Vorrang - sonst ueberschriebe der
                // Abstand eine bewusste Ausrichtung.
                if (child.ReadLocalValue(FrameworkElement.MarginProperty) == DependencyProperty.UnsetValue
                    || child.Margin == wanted)
                {
                    child.Margin = wanted;
                }
            }
        }
    }
}
