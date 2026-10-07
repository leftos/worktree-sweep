using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The name each sudo mode shows in a sentence.</summary>
public sealed class SudoModeTests
{
    /// <summary>Every mode has its name, and the unrecognised one reads as a sentence needs it.</summary>
    [Theory]
    [InlineData(SudoMode.Inline, "Inline")]
    [InlineData(SudoMode.ForceNewWindow, "Force New Window")]
    [InlineData(SudoMode.DisableInput, "Disable Input")]
    [InlineData(SudoMode.Disabled, "Disabled")]
    [InlineData(SudoMode.Unknown, "an unrecognised")]
    public void LabelNamesTheMode(SudoMode mode, string label) => Assert.Equal(label, mode.Label());
}
