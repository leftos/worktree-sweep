namespace WorktreeSweep.Unlock;

/// <summary>Finds the processes holding files under the locked folders, and re-checks one handle before it is closed.</summary>
public interface ILockerScan
{
    /// <summary>
    /// Lists and names the file handles of <paramref name="pids"/> and keeps those under a locked folder, and asks the file system
    /// which processes use each folder. A locked folder that no longer exists is clear: it is skipped, with a trace line.
    /// </summary>
    /// <param name="lockedPaths">The locked folders, as the user picked them.</param>
    /// <param name="pids">The processes to open; no other process is opened.</param>
    /// <returns>The lockers, and the processes the file system reports using a folder whose handles there could not be read.</returns>
    LockerFindings Find(IReadOnlyList<string> lockedPaths, IReadOnlyCollection<int> pids);

    /// <summary>
    /// Whether the process still holds this handle on a file under a locked folder: the PID is opened afresh, its start time must
    /// equal <paramref name="started"/>, and the handle is duplicated and named again. Nothing the earlier scan saw is trusted.
    /// </summary>
    /// <param name="pid">The process.</param>
    /// <param name="started">Its creation time as the scan read it, as a <c>FILETIME</c> count; <see langword="null"/> is never
    /// confirmed.</param>
    /// <param name="handle">The handle value the scan saw in that process.</param>
    /// <param name="lockedPaths">The locked folders, as the user picked them.</param>
    /// <returns><see langword="true"/> only when the same process still holds the handle and it names a file under a locked folder.</returns>
    bool StillHolds(int pid, ulong? started, nint handle, IReadOnlyList<string> lockedPaths);
}

/// <summary>What a <see cref="ILockerScan"/> found.</summary>
/// <param name="Lockers">The processes holding files under the locked folders, sorted by PID.</param>
/// <param name="Unreadable">The processes the file system reports using a locked folder for which the scan named no handle there:
/// the process could not be opened, or its handles had no name.</param>
public sealed record LockerFindings(IReadOnlyList<Locker> Lockers, IReadOnlyList<UnreadableUser> Unreadable);

/// <summary>A process the file system reports using a locked folder, whose handles there could not be read.</summary>
/// <param name="Process">The image name of the process, or <c>unknown</c> when it was not in the process table.</param>
/// <param name="Pid">The process id.</param>
/// <param name="Path">The locked folder, as the user picked it.</param>
public sealed record UnreadableUser(string Process, int Pid, string Path);
