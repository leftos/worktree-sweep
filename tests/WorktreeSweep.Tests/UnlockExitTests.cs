using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The elevated session's exit codes and the action each process is offered first.</summary>
public sealed class UnlockExitTests
{
    /// <summary>All clear whenever nothing holds the files, whatever was acted on; otherwise some left when something was acted
    /// on, and nothing done when nothing was.</summary>
    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 4)]
    [InlineData(false, true, 3)]
    public void ForMapsClearAndActedToTheCode(bool clear, bool acted, int expected) => Assert.Equal(expected, UnlockExit.For(clear, acted));

    /// <summary>The caller is skipped by default, since stopping it closes the user's shell; any other process is stopped.</summary>
    [Theory]
    [InlineData(true, LockerAction.Skip)]
    [InlineData(false, LockerAction.Stop)]
    public void DefaultActionSkipsTheCaller(bool isCaller, LockerAction expected) => Assert.Equal(expected, LockerActions.DefaultAction(isCaller));

    /// <summary>The codes are 0, 3 and 4; 1 is a failure and 2 a usage error.</summary>
    [Fact]
    public void CodesAreZeroThreeAndFour() => Assert.Equal<int[]>([0, 3, 4], [UnlockExit.AllClear, UnlockExit.SomeLeft, UnlockExit.NothingDone]);
}
