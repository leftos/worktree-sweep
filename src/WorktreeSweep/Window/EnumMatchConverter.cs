using System.Globalization;
using System.Windows.Data;

namespace WorktreeSweep.Window;

/// <summary>
/// Binds a radio button of a group to one enum value: checked while the bound value equals the parameter, and checking it sets the
/// bound value to the parameter.
/// </summary>
[ValueConversion(typeof(Enum), typeof(bool))]
public sealed class EnumMatchConverter : IValueConverter
{
    /// <summary>Whether the bound value is the button's.</summary>
    /// <param name="value">The bound enum value.</param>
    /// <param name="targetType">Unused.</param>
    /// <param name="parameter">The button's enum value.</param>
    /// <param name="culture">Unused.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, parameter);

    /// <summary>The button's value when it is checked; nothing when it is unchecked, since checking another button sets the value.</summary>
    /// <param name="value">Whether the button is checked.</param>
    /// <param name="targetType">Unused.</param>
    /// <param name="parameter">The button's enum value.</param>
    /// <param name="culture">Unused.</param>
    /// <returns>The parameter, or <see cref="Binding.DoNothing"/>.</returns>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? parameter : Binding.DoNothing;
}
