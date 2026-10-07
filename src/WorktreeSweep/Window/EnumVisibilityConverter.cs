using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WorktreeSweep.Window;

/// <summary>
/// Shows a panel only when the bound enum value is the one its parameter names: a screen's panel for its <c>Screen</c>, a Review
/// part for its <c>ReviewState</c>.
/// </summary>
[ValueConversion(typeof(Enum), typeof(Visibility))]
public sealed class EnumVisibilityConverter : IValueConverter
{
    /// <summary>Whether the panel for <paramref name="parameter"/> shows.</summary>
    /// <param name="value">The enum value shown now.</param>
    /// <param name="targetType">Unused.</param>
    /// <param name="parameter">The name of the value the panel belongs to.</param>
    /// <param name="culture">Unused.</param>
    /// <returns><see cref="Visibility.Visible"/> when <paramref name="value"/>'s name is <paramref name="parameter"/>,
    /// <see cref="Visibility.Collapsed"/> otherwise.</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Enum shown && parameter is string name && shown.ToString() == name ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Not supported: a visibility is never converted back.</summary>
    /// <param name="value">Unused.</param>
    /// <param name="targetType">Unused.</param>
    /// <param name="parameter">Unused.</param>
    /// <param name="culture">Unused.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("a visibility is never converted back to a screen");
}
