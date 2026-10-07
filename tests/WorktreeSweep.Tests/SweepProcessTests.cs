using WorktreeSweep.Processes;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The running process as the unlock offer names it to the elevated side.</summary>
public sealed class SweepProcessTests
{
    /// <summary>
    /// This process has its program path and PID, and its caller is the process that started it. A runner may launch the test host
    /// from a process that has already exited, so an unknown caller is tolerated; a known one is the parent from the table.
    /// </summary>
    [Fact]
    public void CurrentNamesThisProcess()
    {
        var self = SweepProcess.Current();

        Assert.Equal(Environment.ProcessId, self.Pid);
        Assert.NotNull(self.ExePath);
        Assert.True(File.Exists(self.ExePath), $"{self.ExePath} does not exist");
        int? parent = ProcessTable.Snapshot().TryGetValue(Environment.ProcessId, out ProcessEntry? entry) ? entry.Parent : null;
        Assert.True(self.CallerPid is null || self.CallerPid == parent, $"caller {self.CallerPid} is not the parent {parent}");
    }

    /// <summary>The caller's creation time is read with its PID: whenever the caller is kept, its time travels with it.</summary>
    [Fact]
    public void CurrentNamesTheCallersCreationTime()
    {
        var self = SweepProcess.Current();

        Assert.True(self.CallerPid is null || self.CallerStarted is not null, $"caller {self.CallerPid} has no start time");
    }

    /// <summary>A parent that started before this process is kept.</summary>
    [Fact]
    public void KeepParentKeepsAnEarlierParent()
    {
        int? kept = SweepProcess.KeepParent(4242, 1_000UL, 2_000UL);

        Assert.NotNull(kept);
        Assert.Equal(4242, kept.Value);
    }

    /// <summary>A parent that started at the same instant as this process is kept: a tie is no proof of reuse.</summary>
    [Fact]
    public void KeepParentKeepsAParentStartedAtTheSameTime()
    {
        int? kept = SweepProcess.KeepParent(4242, 2_000UL, 2_000UL);

        Assert.NotNull(kept);
        Assert.Equal(4242, kept.Value);
    }

    /// <summary>A parent whose creation time cannot be read is kept: an unknown time is no proof of reuse.</summary>
    [Fact]
    public void KeepParentKeepsAParentWithNoCreationTime()
    {
        int? kept = SweepProcess.KeepParent(4242, null, 2_000UL);

        Assert.NotNull(kept);
        Assert.Equal(4242, kept.Value);
    }

    /// <summary>When this process's own creation time cannot be read, the parent is kept: no reuse can be proved.</summary>
    [Fact]
    public void KeepParentKeepsWhenThisProcessStartedAtAnUnknownTime()
    {
        int? kept = SweepProcess.KeepParent(4242, 1_000UL, null);

        Assert.NotNull(kept);
        Assert.Equal(4242, kept.Value);
    }

    /// <summary>A parent that started after this process is dropped: a newer process has taken its PID.</summary>
    [Fact]
    public void KeepParentDropsAParentThatStartedLater() => Assert.Null(SweepProcess.KeepParent(4242, 3_000UL, 2_000UL));
}
