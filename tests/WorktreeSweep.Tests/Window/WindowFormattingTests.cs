using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WorktreeSweep.Report;
using WorktreeSweep.ViewModels;
using WorktreeSweep.Window;

namespace WorktreeSweep.Tests.Window;

/// <summary>The window's converters: the risk tints and which screen's panel shows.</summary>
public sealed class WindowFormattingTests
{
    /// <summary>Each risk but none tints its cell with its own colour.</summary>
    /// <param name="risk">The cell's risk.</param>
    /// <param name="colour">The name of the colour it gets.</param>
    [Theory]
    [InlineData(CellRisk.Danger, "Firebrick")]
    [InlineData(CellRisk.Caution, "DarkGoldenrod")]
    [InlineData(CellRisk.Good, "ForestGreen")]
    public void RiskTintsItsCell(CellRisk risk, string colour)
    {
        object brush = new RiskBrushConverter().Convert(risk, typeof(Brush), null!, CultureInfo.InvariantCulture);

        Assert.Equal(ColorConverter.ConvertFromString(colour), Assert.IsType<SolidColorBrush>(brush).Color);
    }

    /// <summary>A cell with no risk keeps the default foreground.</summary>
    [Fact]
    public void NoRiskKeepsTheDefaultForeground()
    {
        object brush = new RiskBrushConverter().Convert(CellRisk.None, typeof(Brush), null!, CultureInfo.InvariantCulture);

        Assert.Same(DependencyProperty.UnsetValue, brush);
    }

    /// <summary>The screen shown makes its own panel visible and collapses every other one.</summary>
    /// <param name="shown">The screen shown.</param>
    [Theory]
    [InlineData(Screen.Scanning)]
    [InlineData(Screen.Empty)]
    [InlineData(Screen.Failed)]
    [InlineData(Screen.List)]
    [InlineData(Screen.Review)]
    [InlineData(Screen.Removing)]
    [InlineData(Screen.Results)]
    public void OnlyTheShownScreensPanelIsVisible(Screen shown)
    {
        var converter = new EnumVisibilityConverter();

        foreach (Screen panel in Enum.GetValues<Screen>())
        {
            object visibility = converter.Convert(shown, typeof(Visibility), panel.ToString(), CultureInfo.InvariantCulture);

            Assert.Equal(panel == shown ? Visibility.Visible : Visibility.Collapsed, visibility);
        }
    }
}
