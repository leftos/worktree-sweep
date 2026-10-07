using WorktreeSweep.Processes;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The running process as the unlock offer names it to the elevated side.</summary>
public sealed class SweepProcessTests
{
    /// <summary>The current process has its program path and PID, and its caller is its parent, or unknown.</summary>
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
}
