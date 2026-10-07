using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WorktreeSweep.Report;

namespace WorktreeSweep.Window;

/// <summary>Tints a table cell by its <see cref="CellRisk"/>; a cell with no risk keeps the default foreground.</summary>
[ValueConversion(typeof(CellRisk), typeof(Brush))]
public sealed class RiskBrushConverter : IValueConverter
{
    /// <summary>The brush for a risk.</summary>
    /// <param name="value">The <see cref="CellRisk"/>.</param>
    /// <param name="targetType">Unused.</param>
    /// <param name="parameter">Unused.</param>
    /// <param name="culture">Unused.</param>
    /// <returns>Firebrick for danger, dark goldenrod for caution, forest green for good, and
    /// <see cref="DependencyProperty.UnsetValue"/> otherwise, so the cell keeps its default foreground.</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            CellRisk.Danger => Brushes.Firebrick,
            CellRisk.Caution => Brushes.DarkGoldenrod,
            CellRisk.Good => Brushes.ForestGreen,
            _ => DependencyProperty.UnsetValue,
        };

    /// <summary>Not supported: a tint is never converted back.</summary>
    /// <param name="value">Unused.</param>
    /// <param name="targetType">Unused.</param>
    /// <param name="parameter">Unused.</param>
    /// <param name="culture">Unused.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("a risk tint is never converted back to a risk");
}
