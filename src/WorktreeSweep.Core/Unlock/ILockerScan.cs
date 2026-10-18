namespace WorktreeSweep.Unlock;

/// <summary>Finds the processes holding files under the locked folders, and closes a locker's handles that a fresh look still finds
/// holding them.</summary>
public interface ILockerScan
{
    /// <summary>
    /// Lists and names the file handles of <paramref name="pids"/> and keeps those under a locked folder, and asks the file system
    /// which processes use each folder. A locked folder that is gone from a drive that exists is clear: it is skipped, with a trace
    /// line. A folder that cannot be opened for any other reason, a drive this process cannot see included, is unreachable, and so
    /// is one whose users the file system cannot list.
    /// </summary>
    /// <param name="lockedPaths">The locked folders, as the user picked them.</param>
    /// <param name="pids">The processes to open; no other process is opened.</param>
    /// <returns>The lockers, the processes the file system reports using a folder whose handles there could not be read, the
    /// folders that could not be opened or whose users could not be listed, and the processes with a handle whose name could not
    /// be read in time.</returns>
    LockerFindings Find(IReadOnlyList<string> lockedPaths, IReadOnlyCollection<int> pids);

    /// <summary>
    /// Closes the locker's handles that still name a file under a locked folder. The PID is opened once and its start time must
    /// equal <see cref="Locker.Started"/>, and the folders are resolved once; then each handle in turn is duplicated and named
    /// again, the duplicate is disposed, and <paramref name="close"/> is called only while the handle still names a file under a
    /// locked folder. The process stays open until the last <paramref name="close"/> has returned, so its PID cannot pass to another
    /// process meanwhile. Nothing the earlier scan saw is trusted: a re-check that fails or throws leaves that handle open. When the
    /// locker cannot be confirmed as the process the scan saw, nothing is closed and the result is <see langword="null"/>.
    /// </summary>
    /// <param name="locker">The process with the handles and the start time the scan saw; a <see langword="null"/> start time is
    /// never confirmed.</param>
    /// <param name="lockedPaths">The locked folders, as the user picked them.</param>
    /// <param name="close">Closes one handle and returns whether it closed.</param>
    /// <returns>How many calls to <paramref name="close"/> returned <see langword="true"/>, which is 0 when the process was confirmed
    /// and nothing was closed; <see langword="null"/> when the process could not be confirmed as the one the scan saw, by a
    /// <see langword="null"/> start time, a process that cannot be opened, a start time that differs, a process that has exited, or
    /// a process whose exit cannot be checked, so nothing was closed.</returns>
    int? CloseStillHeld(Locker locker, IReadOnlyList<string> lockedPaths, Func<HeldHandle, bool> close);

    /// <summary>
    /// Waits, at most <paramref name="limit"/>, for the name lookups this scan gave up on while they ran, in its scans and its
    /// re-checks alike, to return. Until one returns, this process may itself hold a file under a locked folder through the duplicate
    /// handle the lookup made.
    /// </summary>
    /// <param name="limit">How long to wait.</param>
    /// <returns>How many of those lookups have still not returned.</returns>
    int WaitForUnsettledLookups(TimeSpan limit);
}

/// <summary>What a <see cref="ILockerScan"/> found.</summary>
/// <param name="Lockers">The processes holding files under the locked folders, sorted by PID.</param>
/// <param name="Unreadable">The processes the file system reports using a locked folder for which the scan named no handle there:
/// the process could not be opened, or its handles had no name.</param>
/// <param name="Unreachable">The locked folders, as the user picked them, that could not be opened or whose users could not be
/// listed, so what holds them is not known.</param>
public sealed record LockerFindings(IReadOnlyList<Locker> Lockers, IReadOnlyList<UnreadableUser> Unreadable, IReadOnlyList<string> Unreachable)
{
    /// <summary>Gets the processes with a handle whose name could not be read in time, so it may name a file under a locked folder,
    /// sorted by PID; a process in <see cref="Unreadable"/> or <see cref="Lockers"/> is left out. Empty unless set.</summary>
    public IReadOnlyList<TimedOutProcess> TimedOut { get; init; } = [];
}

/// <summary>A process with a handle whose name lookup timed out.</summary>
/// <param name="Process">The image name of the process, or <c>unknown</c> when it was not in the process table.</param>
/// <param name="Pid">The process id.</param>
public sealed record TimedOutProcess(string Process, int Pid);

/// <summary>A process the file system reports using a locked folder, whose handles there could not be read.</summary>
/// <param name="Process">The image name of the process, or <c>unknown</c> when it was not in the process table.</param>
/// <param name="Pid">The process id.</param>
/// <param name="Path">The locked folder, as the user picked it.</param>
public sealed record UnreadableUser(string Process, int Pid, string Path);
