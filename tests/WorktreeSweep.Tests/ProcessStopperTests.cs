using System.ComponentModel;
using System.Diagnostics;
using WorktreeSweep.Holders;
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
            ProcessStopper.Stop(child.Id, "pwsh.exe", null, ProcessStopper.DefaultWait);

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

        ProcessStopper.Stop(child.Id, "pwsh.exe", null, ProcessStopper.DefaultWait);
    }

    /// <summary>A PID no process can have cannot be opened.</summary>
    [Fact]
    public void StopOfANonexistentPidThrows()
    {
        const int NoSuchPid = int.MaxValue & ~3;

        Win32Exception error = Assert.Throws<Win32Exception>(() => ProcessStopper.Stop(NoSuchPid, "pwsh.exe", null, ProcessStopper.DefaultWait));
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
                ProcessStopper.Stop(child.Id, "not-this.exe", null, ProcessStopper.DefaultWait)
            );

            Assert.Contains("not-this.exe; not stopped", error.Message, StringComparison.Ordinal);
            Assert.False(child.HasExited);
        }
        finally
        {
            SleepingChild.KillIfAlive(child);
        }
    }

    /// <summary>A PID whose process started at a different time than the caller saw is not stopped, so a PID reused since the scan
    /// is never terminated even when the new process has the same image name.</summary>
    [Fact]
    public void StopWithADifferentStartTimeThrowsAndLeavesItRunning()
    {
        using Process child = SleepingChild.Start();
        try
        {
            ulong? started = HolderFinder.StartedAt(child.Id);
            Assert.NotNull(started);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                ProcessStopper.Stop(child.Id, "pwsh.exe", started + 1, ProcessStopper.DefaultWait)
            );

            Assert.Contains("is not the process that was seen", error.Message, StringComparison.Ordinal);
            Assert.False(child.HasExited);
        }
        finally
        {
            SleepingChild.KillIfAlive(child);
        }
    }

    /// <summary>A PID whose creation time still matches the caller's is stopped.</summary>
    [Fact]
    public void StopWithTheMatchingStartTimeStops()
    {
        using Process child = SleepingChild.Start();
        try
        {
            ulong? started = HolderFinder.StartedAt(child.Id);
            Assert.NotNull(started);

            ProcessStopper.Stop(child.Id, "pwsh.exe", started, ProcessStopper.DefaultWait);

            child.WaitForExit();
            Assert.Equal(1, child.ExitCode);
        }
        finally
        {
            SleepingChild.KillIfAlive(child);
        }
    }
}
