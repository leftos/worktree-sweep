using WorktreeSweep.Processes;

namespace WorktreeSweep.Unlock;

/// <summary>Which processes hold files under the locked folders.</summary>
public static class LockerFinder
{
    /// <summary>
    /// Parses a <c>handle.exe</c> dump, drops every handle whose process is excluded or whose object is not one of
    /// <paramref name="paths"/> or under one, and groups the rest by process.
    /// </summary>
    /// <param name="dump">The dump as <c>handle.exe -nobanner -v</c> printed it.</param>
    /// <param name="paths">The locked paths.</param>
    /// <param name="excluded">The PIDs never offered, such as this process and the unelevated worktree-sweep.</param>
    /// <param name="startedAt">A process's creation time by PID, read once per locker when it is built.</param>
    /// <returns>One locker per process, sorted by pid.</returns>
    public static IReadOnlyList<Locker> Find(string dump, IReadOnlyList<string> paths, IReadOnlySet<int> excluded, Func<int, ulong?> startedAt)
    {
        ArgumentNullException.ThrowIfNull(dump);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(excluded);
        ArgumentNullException.ThrowIfNull(startedAt);
        return HandleCsv.GroupByProcess(
            HandleCsv.Parse(dump).Where(row => !excluded.Contains(row.Pid) && LockedPaths.Matches(row.Name, paths)),
            startedAt
        );
    }
}
