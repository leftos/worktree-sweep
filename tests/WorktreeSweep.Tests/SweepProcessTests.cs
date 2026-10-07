using WorktreeSweep.Processes;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The running process as the unlock offer names it to the elevated side.</summary>
public sealed class SweepProcessTests
{
    /// <summary>This process has its program path and PID, and its caller is the process that started it.</summary>
    [Fact]
    public void CurrentNamesThisProcess()
    {
        var self = SweepProcess.Current();

        Assert.Equal(Environment.ProcessId, self.Pid);
        Assert.NotNull(self.ExePath);
        Assert.True(File.Exists(self.ExePath), $"{self.ExePath} does not exist");
        int? parent = ProcessTable.Snapshot().TryGetValue(Environment.ProcessId, out ProcessEntry? entry) ? entry.Parent : null;
        Assert.NotNull(self.CallerPid);
        Assert.True(self.CallerPid == parent, $"caller {self.CallerPid} is not the parent {parent}");
    }

    /// <summary>A parent that started before this process is kept.</summary>
    [Fact]
    public void KeepParentKeepsAnEarlierParent()
    {
        int? kept = SweepProcess.KeepParent(4242, 1_000UL, 2_000UL);

        Assert.NotNull(kept);
        Assert.Equal(4242, kept.Value);
    }

    /// <summary>A parent that started at the same instant as this process is kept.</summary>
    [Fact]
    public void KeepParentKeepsAParentStartedAtTheSameTime()
    {
        int? kept = SweepProcess.KeepParent(4242, 2_000UL, 2_000UL);

        Assert.NotNull(kept);
        Assert.Equal(4242, kept.Value);
    }

    /// <summary>A parent that started after this process is dropped: a newer process has taken its PID.</summary>
    [Fact]
    public void KeepParentDropsAParentThatStartedLater() => Assert.Null(SweepProcess.KeepParent(4242, 3_000UL, 2_000UL));

    /// <summary>A parent whose creation time cannot be read is dropped.</summary>
    [Fact]
    public void KeepParentDropsAParentWithNoCreationTime() => Assert.Null(SweepProcess.KeepParent(4242, null, 2_000UL));

    /// <summary>When this process's own creation time cannot be read, its parent is dropped.</summary>
    [Fact]
    public void KeepParentDropsWhenThisProcessStartedAtAnUnknownTime() => Assert.Null(SweepProcess.KeepParent(4242, 1_000UL, null));
}
