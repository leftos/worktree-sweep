using System.Globalization;
using System.Text;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>Which processes hold files under the locked folders.</summary>
public sealed class LockerFinderTests
{
    private const string Locked = @"D:\a.wt\x";

    /// <summary>An excluded PID and a handle whose object is not under a locked path are dropped.</summary>
    [Fact]
    public void DropsExcludedPidsAndForeignPaths()
    {
        string dump = Dump(
            ("code.exe", 90, "File", 0x10, @"D:\a.wt\x\file.txt"),
            ("pwsh.exe", 12, "File", 0x11, @"D:\a.wt\x"),
            ("other.exe", 30, "File", 0x12, @"D:\elsewhere\file.txt"),
            ("pwsh.exe", 12, "File", 0x13, @"D:\a.wt\x\sub\other.txt")
        );
        HashSet<int> excluded = [90];

        IReadOnlyList<Locker> lockers = LockerFinder.Find(dump, [Locked], excluded, _ => null);

        Locker locker = Assert.Single(lockers);
        Assert.Equal(12, locker.Pid);
        Assert.Equal("pwsh.exe", locker.Process);
        Assert.Equal<ulong[]>([0x11, 0x13], [.. locker.Handles.Select(held => held.Handle)]);
    }

    /// <summary>Rows group by PID, sorted by PID, keeping each process's handles in the order they were listed.</summary>
    [Fact]
    public void GroupsByPidInPidOrder()
    {
        string dump = Dump(
            ("code.exe", 90, "File", 0x10, @"D:\a.wt\x\a.txt"),
            ("pwsh.exe", 12, "File", 0x11, @"D:\a.wt\x\b.txt"),
            ("code.exe", 90, "File", 0x12, @"D:\a.wt\x\c.txt")
        );
        HashSet<int> excluded = [];

        IReadOnlyList<Locker> lockers = LockerFinder.Find(dump, [Locked], excluded, _ => null);

        Assert.Equal<int[]>([12, 90], [.. lockers.Select(locker => locker.Pid)]);
        Assert.Equal("code.exe", lockers[1].Process);
        Assert.Equal<ulong[]>([0x10, 0x12], [.. lockers[1].Handles.Select(held => held.Handle)]);
    }

    /// <summary>Each locker carries the start time the reader gave its PID, read once per PID even with several handles.</summary>
    [Fact]
    public void FindReadsEachLockersStartTimeOnce()
    {
        string dump = Dump(
            ("code.exe", 90, "File", 0x10, @"D:\a.wt\x\a.txt"),
            ("pwsh.exe", 12, "File", 0x11, @"D:\a.wt\x\b.txt"),
            ("pwsh.exe", 12, "File", 0x12, @"D:\a.wt\x\c.txt")
        );
        HashSet<int> excluded = [];
        Dictionary<int, ulong> times = new() { [12] = 1000, [90] = 2000 };
        List<int> reads = [];
        ulong? StartedAt(int pid)
        {
            reads.Add(pid);
            return times.TryGetValue(pid, out ulong started) ? started : null;
        }

        IReadOnlyList<Locker> lockers = LockerFinder.Find(dump, [Locked], excluded, StartedAt);

        Assert.Equal<ulong?[]>([1000, 2000], [.. lockers.Select(locker => locker.Started)]);
        Assert.Equal(2, reads.Count);
        Assert.Equal<int[]>([12, 90], [.. reads.Order()]);
    }

    /// <summary>A dump of the <c>-nobanner -v</c> layout, holding the given rows.</summary>
    /// <param name="rows">The handles.</param>
    /// <returns>The dump.</returns>
    private static string Dump(params (string Process, int Pid, string Kind, ulong Handle, string Name)[] rows)
    {
        var text = new StringBuilder("Process,PID,User,Handle,Type,Share Flags,Name\n");
        foreach ((string Process, int Pid, string Kind, ulong Handle, string Name) row in rows)
        {
            text.Append(CultureInfo.InvariantCulture, $"{row.Process},{row.Pid},EXAMPLE-PC\\user,0x{row.Handle:X8},{row.Kind},,{row.Name}\n");
        }
        return text.ToString();
    }
}
