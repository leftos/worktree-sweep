using WorktreeSweep.Discovery;
using WorktreeSweep.Git;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>Reading every signal of one registered worktree.</summary>
public sealed class WorktreeSignalsTests
{
    /// <summary>
    /// A worktree whose signals cannot be read, here because its path holds a NUL, gets null values and one error each, never a throw
    /// that would hide the other worktrees. Git fails on the merge state and dirty reads; .NET rejects the path with an
    /// <see cref="ArgumentException"/> on the last-activity read; the upstream reads take git's failure as "no upstream", as they
    /// would for any branch without one.
    /// </summary>
    [Fact]
    public void UnreadableSignalsAreRecordedNotThrown()
    {
        using var fx = new Fixture();
        var record = new WorktreeRecord { Path = fx.PathTo("bad") + "\0name", Branch = "feat" };

        WorktreeSignals signals = SignalReader.ReadWorktreeSignals(new DefaultBranches { Local = "main" }, record, new VolumeStalls());

        Assert.Null(signals.MergeState);
        Assert.Null(signals.Dirty);
        Assert.Null(signals.LastActivityUnix);
        Assert.Equal(3, signals.Errors.Count);
        Assert.Equal(1, signals.Size?.Unreadable);
    }
}
