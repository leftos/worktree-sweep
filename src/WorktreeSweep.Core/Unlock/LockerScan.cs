using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using WorktreeSweep.Holders;
using WorktreeSweep.Processes;

namespace WorktreeSweep.Unlock;

/// <summary>
/// The production <see cref="ILockerScan"/>: the C# handle scan (<see cref="OpenHandles.Scan"/>) over the given processes, run with
/// SeDebugPrivilege so it can open nearly every process. Handle names are read in UTF-16, so a path in any script is matched intact.
/// </summary>
public sealed class LockerScan : ILockerScan
{
    /// <summary>The process name shown for a PID the process table did not list.</summary>
    private const string UnknownProcess = "unknown";

    /// <summary>Initializes a new instance of the <see cref="LockerScan"/> class without SeDebugPrivilege, so it opens only the
    /// processes this user may; <see cref="Elevated"/> outside tests.</summary>
    internal LockerScan() { }

    /// <summary>Enables SeDebugPrivilege for this process and returns the scan; it never scans without it.</summary>
    /// <returns>The scan.</returns>
    /// <exception cref="UnlockException">SeDebugPrivilege cannot be enabled: this process is not elevated.</exception>
    public static LockerScan Elevated()
    {
        try
        {
            Process.EnterDebugMode();
        }
        catch (Win32Exception error)
        {
            throw new UnlockException($"cannot get SeDebugPrivilege ({error.Message}); the unlock session must run elevated", error);
        }
        return new LockerScan();
    }

    /// <inheritdoc/>
    /// <exception cref="IOException">A locked folder that still exists cannot be resolved, or this program's own handles cannot be
    /// read to learn which handles are files.</exception>
    /// <exception cref="Win32Exception">The process table cannot be read.</exception>
    public LockerFindings Find(IReadOnlyList<string> lockedPaths, IReadOnlyCollection<int> pids)
    {
        ArgumentNullException.ThrowIfNull(lockedPaths);
        ArgumentNullException.ThrowIfNull(pids);
        IReadOnlyList<LockedFolder> folders = ExistingFolders(lockedPaths);
        if (folders.Count == 0)
        {
            return new LockerFindings([], []);
        }
        IReadOnlyDictionary<int, ProcessEntry> table = ProcessTable.Snapshot();
        // Read before the handles are listed: a PID reused after this reading has a start time that no longer matches, so it is
        // never acted on; one read after the listing could pair the old process's handles with the new process.
        var started = pids.Distinct().ToDictionary(pid => pid, HolderFinder.StartedAt);
        HandleFindings findings = OpenHandles.Scan(pids, [.. folders.SelectMany(folder => folder.Forms)]);
        return new LockerFindings(Lockers(findings, folders, table, started), Unreadable(findings, folders, table));
    }

    /// <inheritdoc/>
    public bool StillHolds(int pid, ulong? started, nint handle, IReadOnlyList<string> lockedPaths)
    {
        ArgumentNullException.ThrowIfNull(lockedPaths);
        if (started is not ulong expected)
        {
            return false;
        }
        IReadOnlyList<string> roots = [.. ExistingFolders(lockedPaths).SelectMany(folder => folder.Forms)];
        using SafeFileHandle? source = OpenHandles.OpenSource(pid);
        if (source is null || ProcessQuery.CreationTime(source) != expected)
        {
            return false;
        }
        NamedHandles named = HandleNaming.Name(
            [(pid, new RemoteHandle(pid, source, (ulong)handle))],
            OpenHandles.Check,
            1,
            HandleNaming.DefaultTimeout,
            HandleNaming.CancelLookup
        );
        return named.Names.Any(found => found.Handle == handle && found.Name is not null && LockedPaths.Matches(found.Name, roots));
    }

    /// <summary>
    /// Each locked folder that still exists, with every spelling a handle name may use. A folder that no longer exists is clear, and
    /// is traced and left out.
    /// </summary>
    /// <param name="lockedPaths">The locked folders.</param>
    /// <returns>The folders.</returns>
    /// <exception cref="IOException">A folder that still exists cannot be resolved.</exception>
    private static List<LockedFolder> ExistingFolders(IReadOnlyList<string> lockedPaths)
    {
        var folders = new List<LockedFolder>();
        foreach (string path in lockedPaths)
        {
            try
            {
                folders.Add(new LockedFolder(path, HolderFinder.FolderForms(path)));
            }
            catch (IOException error) when (!Path.Exists(path))
            {
                Trace.WriteLine($"{path} no longer exists, so nothing holds it: {error.Message}");
            }
        }
        return folders;
    }

    /// <summary>The processes with a named handle under a locked folder, sorted by PID, each with its handles there.</summary>
    /// <param name="findings">What the scan found.</param>
    /// <param name="folders">The locked folders.</param>
    /// <param name="table">The process table, read before the scan.</param>
    /// <param name="started">Each scanned process's creation time, read before the scan.</param>
    /// <returns>The lockers.</returns>
    private static List<Locker> Lockers(
        HandleFindings findings,
        IReadOnlyList<LockedFolder> folders,
        IReadOnlyDictionary<int, ProcessEntry> table,
        IReadOnlyDictionary<int, ulong?> started
    )
    {
        IReadOnlyList<string> roots = [.. folders.SelectMany(folder => folder.Forms)];
        var held = new SortedDictionary<int, List<HeldHandle>>();
        foreach (NamedHandle handle in findings.Names)
        {
            if (handle.Name is string name && LockedPaths.Matches(name, roots))
            {
                if (!held.TryGetValue(handle.Pid, out List<HeldHandle>? handles))
                {
                    handles = [];
                    held[handle.Pid] = handles;
                }
                handles.Add(new HeldHandle(handle.Handle, HolderFinder.WithoutVerbatimPrefix(name)));
            }
        }
        return
        [
            .. held.Select(pair => new Locker(
                ProcessName(pair.Key, table),
                pair.Key,
                [.. pair.Value.OrderBy(handle => handle.Name, StringComparer.OrdinalIgnoreCase)],
                started.GetValueOrDefault(pair.Key)
            )),
        ];
    }

    /// <summary>
    /// Each process the file system reports using a locked folder for which the scan named no handle under that folder: it could not
    /// be opened, or its handles there had no name.
    /// </summary>
    /// <param name="findings">What the scan found.</param>
    /// <param name="folders">The locked folders.</param>
    /// <param name="table">The process table, read before the scan.</param>
    /// <returns>The processes, by folder and then by PID.</returns>
    private static List<UnreadableUser> Unreadable(
        HandleFindings findings,
        IReadOnlyList<LockedFolder> folders,
        IReadOnlyDictionary<int, ProcessEntry> table
    )
    {
        var unreadable = new List<UnreadableUser>();
        foreach (LockedFolder folder in folders)
        {
            IEnumerable<int> users = findings
                .Users.Where(user => folder.Forms.Contains(user.Root, StringComparer.Ordinal))
                .Select(user => user.Pid)
                .Distinct()
                .Order();
            foreach (int pid in users)
            {
                bool named = findings.Names.Any(handle => handle.Pid == pid && handle.Name is string name && LockedPaths.Matches(name, folder.Forms));
                if (!named)
                {
                    unreadable.Add(new UnreadableUser(ProcessName(pid, table), pid, folder.Path));
                }
            }
        }
        return unreadable;
    }

    /// <summary>The process's image name, or <see cref="UnknownProcess"/> when the table did not list it.</summary>
    /// <param name="pid">The process.</param>
    /// <param name="table">The process table.</param>
    /// <returns>The name.</returns>
    private static string ProcessName(int pid, IReadOnlyDictionary<int, ProcessEntry> table) =>
        table.TryGetValue(pid, out ProcessEntry? entry) ? entry.Exe : UnknownProcess;

    /// <summary>A locked folder and every spelling a handle name may use for it.</summary>
    /// <param name="Path">The folder, as the user picked it.</param>
    /// <param name="Forms">Its spellings, from <see cref="HolderFinder.FolderForms"/>, the fully resolved one first.</param>
    private sealed record LockedFolder(string Path, IReadOnlyList<string> Forms);
}
