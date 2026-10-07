using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>Running a child process.</summary>
public sealed class ChildProcessTests
{
    /// <summary>Only the codes a missing program gives mean it is not on the PATH.</summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(5, false)]
    public void IsNotFoundReadsTheStartFailureCode(int code, bool expected) => Assert.Equal(expected, ChildProcess.IsNotFound(code));
}
