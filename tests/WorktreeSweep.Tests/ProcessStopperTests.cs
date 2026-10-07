using System.ComponentModel;
using System.Diagnostics;
using WorktreeSweep.Processes;

namespace WorktreeSweep.Tests;

/// <summary>Stopping a process by PID; only processes the tests started are stopped.</summary>
public sealed class ProcessStopperTests
{
    /// <summary>A stopped child exits with code 1.</summary>
    [Fact]
    public void StopTerminatesAChild()
    {
        using Process child = SleepingChild.Start();
        try
        {
            ProcessStopper.Stop(child.Id, "pwsh.exe", ProcessStopper.DefaultWait);

            child.WaitForExit();
            Assert.Equal(1, child.ExitCode);
        }
        finally
        {
            SleepingChild.KillIfAlive(child);
        }
    }

    /// <summary>
    /// Stopping a child that has already exited returns: the goal is met. The child's <see cref="Process"/> stays open until the
    /// end, so its PID cannot be reused by another process meanwhile.
    /// </summary>
    [Fact]
    public void StopOfAnExitedChildReturns()
    {
        using Process child = SleepingChild.Start();
        child.Kill();
        child.WaitForExit();

        ProcessStopper.Stop(child.Id, "pwsh.exe", ProcessStopper.DefaultWait);
    }

    /// <summary>A PID no process can have cannot be opened.</summary>
    [Fact]
    public void StopOfANonexistentPidThrows()
    {
        const int NoSuchPid = int.MaxValue & ~3;

        Win32Exception error = Assert.Throws<Win32Exception>(() => ProcessStopper.Stop(NoSuchPid, "pwsh.exe", ProcessStopper.DefaultWait));
        Assert.Contains("cannot open process", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A PID now held by another image is not stopped, so a PID reused since the scan is never terminated.</summary>
    [Fact]
    public void StopOfAReusedPidThrowsAndLeavesItRunning()
    {
        using Process child = SleepingChild.Start();
        try
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                ProcessStopper.Stop(child.Id, "not-this.exe", ProcessStopper.DefaultWait)
            );

            Assert.Contains("not-this.exe; not stopped", error.Message, StringComparison.Ordinal);
            Assert.False(child.HasExited);
        }
        finally
        {
            SleepingChild.KillIfAlive(child);
        }
    }
}
