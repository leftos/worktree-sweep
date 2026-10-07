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

    /// <summary>sudo is the one in <c>%SystemRoot%\System32</c>, never a program found by name.</summary>
    [Fact]
    public void SudoIsTheSystemOne()
    {
        string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? throw new InvalidOperationException("SystemRoot is not set");

        Assert.Equal(Path.Join(systemRoot, "System32", "sudo.exe"), SudoConfig.Program, ignoreCase: true);
    }
}
