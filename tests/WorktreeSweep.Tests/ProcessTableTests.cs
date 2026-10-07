using System.Diagnostics;
using WorktreeSweep.Processes;

namespace WorktreeSweep.Tests;

/// <summary>The process table and the PIDs the unlock flow must never stop.</summary>
public sealed class ProcessTableTests
{
    /// <summary>The elevated side excludes itself and its parents up to the nearest <c>sudo.exe</c>; unelevated, only itself.</summary>
    [Fact]
    public void OwnParentChainIsExcluded()
    {
        var table = new Dictionary<int, ProcessEntry>
        {
            [1] = new(0, "explorer.exe"),
            [2] = new(1, "pwsh.exe"),
            [3] = new(2, "worktree-sweep.exe"),
            [4] = new(3, "sudo.exe"),
            [5] = new(700, "sudo.exe"),
            [6] = new(5, "conhost.exe"),
            [7] = new(6, "worktree-sweep.exe"),
            [8] = new(2, "code.exe"),
        };

        AssertSet([5, 6, 7], ProcessTable.ParentChain(7, table, _ => null));
        AssertSet([5, 6, 7], ProcessTable.ExcludedPids(7, null, table, _ => null));
        AssertSet([3], ProcessTable.ParentChain(3, table, _ => null));
        AssertSet([3], ProcessTable.ExcludedPids(3, null, table, _ => null));
    }

    /// <summary>A parent cycle with no <c>sudo.exe</c> in it ends the walk and leaves only the own PID.</summary>
    [Fact]
    public void ParentCycleTerminates()
    {
        var table = new Dictionary<int, ProcessEntry> { [10] = new(11, "a.exe"), [11] = new(10, "b.exe") };

        AssertSet([10], ProcessTable.ParentChain(10, table, _ => null));
        AssertSet([10], ProcessTable.ExcludedPids(10, null, table, _ => null));
    }

    /// <summary>An empty table excludes the own PID and the sweep PID, and holds no process.</summary>
    [Fact]
    public void EmptyTableExcludesOnlyTheGivenPids()
    {
        var table = new Dictionary<int, ProcessEntry>();

        AssertSet([99], ProcessTable.ParentChain(99, table, _ => null));
        AssertSet([99], ProcessTable.ExcludedPids(99, null, table, _ => null));
        AssertSet([42, 99], ProcessTable.ExcludedPids(99, 42, table, _ => null));
        Assert.False(ProcessTable.IsSameProcess(99, "pwsh.exe", table));
    }

    /// <summary>The unelevated worktree-sweep and its children, such as the <c>sudo.exe</c> it started, are excluded.</summary>
    [Fact]
    public void SweepProcessAndItsChildrenAreExcluded()
    {
        var table = new Dictionary<int, ProcessEntry>
        {
            [2] = new(1, "pwsh.exe"),
            [3] = new(2, "worktree-sweep.exe"),
            [4] = new(3, "sudo.exe"),
            [5] = new(700, "sudo.exe"),
            [7] = new(5, "worktree-sweep.exe"),
            [8] = new(2, "code.exe"),
            [9] = new(4, "conhost.exe"),
        };

        AssertSet([3, 4, 5, 7], ProcessTable.ExcludedPids(7, 3, table, _ => null));
        AssertSet([5, 7, 42], ProcessTable.ExcludedPids(7, 42, table, _ => null));
    }

    /// <summary>A parent whose PID has been reused — its known creation time is later than its child's — ends the chain.</summary>
    [Fact]
    public void ParentChainStopsAtAParentStartedAfterItsChild()
    {
        var table = new Dictionary<int, ProcessEntry>
        {
            [4] = new(3, "sudo.exe"),
            [5] = new(4, "code.exe"),
            [6] = new(5, "conhost.exe"),
            [7] = new(6, "worktree-sweep.exe"),
        };

        // pid 5 now belongs to a newer process (200) than its child 6 (60), so the walk stops and only the own PID is left.
        AssertSet([7], ProcessTable.ParentChain(7, table, Times((7, 100UL), (6, 60UL), (5, 200UL), (4, 50UL))));
    }

    /// <summary>A creation time that cannot be read is no proof of reuse, so the walk keeps following to the sudo.exe.</summary>
    [Fact]
    public void ParentChainKeepsFollowingAnUnknownTime()
    {
        var table = new Dictionary<int, ProcessEntry>
        {
            [4] = new(3, "sudo.exe"),
            [5] = new(4, "code.exe"),
            [6] = new(5, "conhost.exe"),
            [7] = new(6, "worktree-sweep.exe"),
        };

        AssertSet([4, 5, 6, 7], ProcessTable.ParentChain(7, table, _ => null));
    }

    /// <summary>A holder that reused an ancestor's PID is left out of the exclusions, so it is still offered.</summary>
    [Fact]
    public void ReusedAncestorPidIsNotExcluded()
    {
        var table = new Dictionary<int, ProcessEntry>
        {
            [4] = new(3, "sudo.exe"),
            [5] = new(4, "code.exe"),
            [6] = new(5, "conhost.exe"),
            [7] = new(6, "worktree-sweep.exe"),
        };

        AssertSet([7], ProcessTable.ExcludedPids(7, null, table, Times((7, 100UL), (6, 60UL), (5, 200UL), (4, 50UL))));
    }

    /// <summary>A PID now held by another image is not the process the scan saw; names compare case-insensitively.</summary>
    [Fact]
    public void ReusedPidIsNotTheSameProcess()
    {
        var table = new Dictionary<int, ProcessEntry> { [10] = new(1, "pwsh.exe"), [11] = new(1, "notepad.exe") };

        Assert.True(ProcessTable.IsSameProcess(10, "pwsh.exe", table));
        Assert.True(ProcessTable.IsSameProcess(10, "PWSH.EXE", table));
        Assert.False(ProcessTable.IsSameProcess(11, "pwsh.exe", table));
        Assert.False(ProcessTable.IsSameProcess(12, "pwsh.exe", table));
    }

    /// <summary>A live snapshot holds this process and a child it started, with this process as the child's parent.</summary>
    [Fact]
    public void SnapshotContainsThisProcessAndAChildsParent()
    {
        using Process child = SleepingChild.Start();
        try
        {
            IReadOnlyDictionary<int, ProcessEntry> table = ProcessTable.Snapshot();

            Assert.True(table.ContainsKey(Environment.ProcessId));
            Assert.True(table.TryGetValue(child.Id, out ProcessEntry? entry));
            Assert.Equal(Environment.ProcessId, entry.Parent);
            Assert.Equal("pwsh.exe", entry.Exe, ignoreCase: true);
        }
        finally
        {
            SleepingChild.KillIfAlive(child);
        }
    }

    /// <summary>A start-time reader answering with the given times; a PID it does not name reads as unknown.</summary>
    private static Func<int, ulong?> Times(params (int Pid, ulong Started)[] entries)
    {
        Dictionary<int, ulong?> times = entries.ToDictionary(entry => entry.Pid, entry => (ulong?)entry.Started);
        return pid => times.TryGetValue(pid, out ulong? started) ? started : null;
    }

    private static void AssertSet(int[] expected, IReadOnlySet<int> actual) => Assert.Equal(expected.Order(), actual.Order());
}
