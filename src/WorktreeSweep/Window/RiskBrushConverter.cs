using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WorktreeSweep.Report;

namespace WorktreeSweep.Window;

/// <summary>
/// Tints a table cell by its <see cref="CellRisk"/>: its text, or with the parameter <c>Background</c> the pill behind it. A cell
/// with no risk keeps the default foreground and no pill.
/// </summary>
[ValueConversion(typeof(CellRisk), typeof(Brush))]
public sealed class RiskBrushConverter : IValueConverter
{
    /// <summary>The parameter that asks for the pill's background rather than the text's brush.</summary>
    private const string Background = "Background";

    /// <summary>The pill behind a cell that may lose work.</summary>
    private static readonly Brush DangerPill = Frozen(0xFD, 0xE3, 0xE5);

    /// <summary>The pill behind a cell that deserves a second look.</summary>
    private static readonly Brush CautionPill = Frozen(0xFF, 0xEF, 0xC2);

    /// <summary>The pill behind a safe cell.</summary>
    private static readonly Brush GoodPill = Frozen(0xE2, 0xF3, 0xDF);

    /// <summary>The brush for a risk.</summary>
    /// <param name="value">The <see cref="CellRisk"/>.</param>
    /// <param name="targetType">Unused.</param>
    /// <param name="parameter"><c>Background</c> for the pill's brush; anything else for the text's.</param>
    /// <param name="culture">Unused.</param>
    /// <returns>The text: firebrick for danger, dark goldenrod for caution, forest green for good. The pill: light red, light
    /// amber and light green. <see cref="DependencyProperty.UnsetValue"/> otherwise, so the cell keeps its default look.</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Background.Equals(parameter) ? PillBrush(value) : TextBrush(value);

    /// <summary>The text's brush for a risk.</summary>
    /// <param name="value">The <see cref="CellRisk"/>.</param>
    /// <returns>The brush, or <see cref="DependencyProperty.UnsetValue"/>.</returns>
    private static object TextBrush(object value) =>
        value switch
        {
            CellRisk.Danger => Brushes.Firebrick,
            CellRisk.Caution => Brushes.DarkGoldenrod,
            CellRisk.Good => Brushes.ForestGreen,
            _ => DependencyProperty.UnsetValue,
        };

    /// <summary>The pill's brush for a risk.</summary>
    /// <param name="value">The <see cref="CellRisk"/>.</param>
    /// <returns>The brush, or <see cref="DependencyProperty.UnsetValue"/>.</returns>
    private static object PillBrush(object value) =>
        value switch
        {
            CellRisk.Danger => DangerPill,
            CellRisk.Caution => CautionPill,
            CellRisk.Good => GoodPill,
            _ => DependencyProperty.UnsetValue,
        };

    /// <summary>A frozen solid brush, safe to share across threads.</summary>
    /// <param name="red">The red channel.</param>
    /// <param name="green">The green channel.</param>
    /// <param name="blue">The blue channel.</param>
    /// <returns>The brush.</returns>
    private static SolidColorBrush Frozen(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

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
