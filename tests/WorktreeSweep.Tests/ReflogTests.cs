using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>Reading a branch's reflog subjects for proof that no commit was made on it.</summary>
public sealed class ReflogTests
{
    /// <summary>
    /// A complete reflog (oldest entry the branch's creation) of resets, renames, finished rebases and fast-forwards proves no
    /// commit; any other entry, or a reflog without its creation, does not.
    /// </summary>
    /// <param name="reflog">The subjects, newest first.</param>
    /// <param name="expected">Whether they prove no commit was made.</param>
    [Theory]
    [InlineData("rebase (finish): refs/heads/m2d-budget onto d97e8fa722f106740394948ecd72ef6a6fe490c1\nbranch: Created from main", true)]
    [InlineData("branch: Created from HEAD", true)]
    [InlineData("merge main: Fast-forward\nbranch: Created from main", true)]
    [InlineData("reset: moving to main\nbranch: Created from main", true)]
    [InlineData("pull -q origin main: Fast-forward\nbranch: Created from main", true)]
    [InlineData("", false)]
    [InlineData("commit: b\nbranch: Created from main", false)]
    [InlineData("merge side: Merge made by the 'ort' strategy.\nbranch: Created from main", false)]
    [InlineData("am: x\nbranch: Created from main", false)]
    [InlineData("pull: Merge made by the 'ort' strategy.\nbranch: Created from main", false)]
    [InlineData("cherry-pick: x\nbranch: Created from main", false)]
    [InlineData("reset: moving to main", false)]
    [InlineData("merge main: Fast-forward", false)]
    public void HasNoOwnCommitsReadsReflogSubjects(string reflog, bool expected) => Assert.Equal(expected, SignalReader.HasNoOwnCommits(reflog));
}
