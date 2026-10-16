using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Foundation;
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
    /// <exception cref="IOException">This program's own handles cannot be read to learn which handles are files.</exception>
    /// <exception cref="Win32Exception">The process table cannot be read.</exception>
    public LockerFindings Find(IReadOnlyList<string> lockedPaths, IReadOnlyCollection<int> pids)
    {
        ArgumentNullException.ThrowIfNull(lockedPaths);
        ArgumentNullException.ThrowIfNull(pids);
        FolderSurvey survey = Survey(lockedPaths);
        IReadOnlyList<LockedFolder> folders = survey.Folders;
        if (folders.Count == 0)
        {
            return new LockerFindings([], [], survey.Unreachable);
        }
        IReadOnlyDictionary<int, ProcessEntry> table = ProcessTable.Snapshot();
        // Read before the handles are listed: a PID reused after this reading has a start time that no longer matches, so it is
        // never acted on; one read after the listing could pair the old process's handles with the new process.
        var started = pids.Distinct().ToDictionary(pid => pid, HolderFinder.StartedAt);
        HandleFindings findings = OpenHandles.Scan(pids, [.. folders.Select(folder => folder.Forms[0])]);
        return new LockerFindings(Lockers(findings, folders, table, started), Unreadable(findings, folders, table), survey.Unreachable);
    }

    /// <inheritdoc/>
    public bool StillHolds(int pid, ulong? started, nint handle, IReadOnlyList<string> lockedPaths)
    {
        ArgumentNullException.ThrowIfNull(lockedPaths);
        if (started is not ulong expected)
        {
            return false;
        }
        IReadOnlyList<string> roots = [.. Survey(lockedPaths).Folders.SelectMany(folder => folder.Forms)];
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
    /// Each locked folder that opens, with every spelling a handle name may use, and each that does not open. A folder that is gone
    /// from a drive that exists is clear, and is traced and left out of both.
    /// </summary>
    /// <param name="lockedPaths">The locked folders.</param>
    /// <returns>The folders.</returns>
    private static FolderSurvey Survey(IReadOnlyList<string> lockedPaths)
    {
        var folders = new List<LockedFolder>();
        var unreachable = new List<string>();
        foreach (string path in lockedPaths)
        {
            try
            {
                folders.Add(new LockedFolder(path, HolderFinder.FolderForms(path)));
            }
            catch (IOException error) when (IsGone(path, error))
            {
                Trace.WriteLine($"{path} no longer exists, so nothing holds it: {error.Message}");
            }
            catch (IOException error)
            {
                Trace.TraceWarning($"cannot open {path}, so what holds it is not known: {error.Message}");
                unreachable.Add(path);
            }
        }
        return new FolderSurvey(folders, unreachable);
    }

    /// <summary>
    /// Whether a failure to open <paramref name="path"/> shows that the folder is gone: the file system found no such file or path,
    /// on a drive whose root exists. A drive this process cannot see, such as one mapped by an unelevated token, fails the same way
    /// and is not gone.
    /// </summary>
    /// <param name="path">The folder.</param>
    /// <param name="error">The failure from <see cref="HolderFinder.FolderForms"/>, which carries the Win32 error.</param>
    /// <returns><see langword="true"/> when the folder is gone.</returns>
    private static bool IsGone(string path, IOException error) =>
        error.InnerException is Win32Exception { NativeErrorCode: (int)WIN32_ERROR.ERROR_FILE_NOT_FOUND or (int)WIN32_ERROR.ERROR_PATH_NOT_FOUND }
        && Directory.Exists(Path.GetPathRoot(Path.GetFullPath(path)));

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

    /// <summary>The locked folders sorted by whether they open.</summary>
    /// <param name="Folders">The folders that open, with their spellings.</param>
    /// <param name="Unreachable">The folders, as the user picked them, that do not open and are not gone.</param>
    private sealed record FolderSurvey(IReadOnlyList<LockedFolder> Folders, IReadOnlyList<string> Unreachable);
}
