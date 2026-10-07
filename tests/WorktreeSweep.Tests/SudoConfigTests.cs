using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The mode <c>sudo config</c> reports.</summary>
public sealed class SudoConfigTests
{
    /// <summary>Each mode sudo config names is read, whatever the case and line ending, and anything else is unknown.</summary>
    [Theory]
    [InlineData("Sudo is currently in Inline mode on this machine\r\n", SudoMode.Inline)]
    [InlineData("Sudo is currently in Force New Window mode on this machine\r\n", SudoMode.ForceNewWindow)]
    [InlineData("Sudo is currently in Disable Input mode on this machine\r\n", SudoMode.DisableInput)]
    [InlineData("Sudo is currently in Input Closed mode on this machine\r\n", SudoMode.DisableInput)]
    [InlineData("Sudo is disabled on this machine. To enable it, go to the Developer Settings page in the Settings app", SudoMode.Disabled)]
    [InlineData("", SudoMode.Unknown)]
    [InlineData("something else entirely", SudoMode.Unknown)]
    public void SudoModeIsReadFromSudoConfig(string text, SudoMode mode) => Assert.Equal(mode, SudoConfig.Parse(text));
}
