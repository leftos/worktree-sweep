using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using WorktreeSweep.Processes;

namespace WorktreeSweep.Holders;

/// <summary>
/// Finds which processes hold a folder, without elevation: every other process running as the current user is opened, its current
/// folder, read from its PEB, is compared with the folder, and its open disk handles are named and compared too. The file system is
/// also asked which processes use the folder itself, so one that cannot be inspected is still reported as one that may hold it.
/// </summary>
public static class HolderFinder
{
    private const string Verbatim = @"\\?\";

    /// <summary>The idle and System processes, which are never inspected.</summary>
    private static readonly int[] SystemPids = [0, 4];

    /// <summary>
    /// Lists the current user's processes, other than this process and the processes in <paramref name="exclude"/>, whose current
    /// folder is <paramref name="folder"/> or lies under it, or that have a file or folder under it open. A process whose disk handle
    /// could not be named in time, or that uses the folder but could not be inspected, is listed as one that may hold it.
    /// </summary>
    /// <param name="folder">The folder, as the user gave it; a junction, subst drive or 8.3 name in it is matched as given and resolved.</param>
    /// <param name="exclude">PIDs that are never inspected or listed.</param>
    /// <returns>The holders and the processes that may hold the folder, each sorted by PID.</returns>
    /// <exception cref="PlatformNotSupportedException">This is not a 64-bit process, which the PEB offsets assume.</exception>
    /// <exception cref="IOException">
    /// The folder cannot be opened or resolved, or this program's own file or handles cannot be read to learn which handles are files.
    /// </exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The process list or this process's own user cannot be read.</exception>
    public static HolderReport Find(string folder, IReadOnlyCollection<int> exclude) => Find(folder, exclude, OpenHandles.Scan);

    /// <summary>
    /// Whether <paramref name="holder"/>'s PID still names the same running process: same creation time and same image.
    /// <see langword="false"/> when that cannot be checked.
    /// </summary>
    /// <param name="holder">A holder from <see cref="Find(string, IReadOnlyCollection{int})"/>.</param>
    /// <returns><see langword="true"/> when the process is still running and is the one found.</returns>
    public static bool StillSame(Holder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        if (holder.Started == 0)
        {
            return false;
        }
        using SafeFileHandle? process = ProcessQuery.Open(
            holder.Pid,
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE
        );
        if (process is null)
        {
            return false;
        }
        bool exited = PInvoke.WaitForSingleObject(process, 0) == WAIT_EVENT.WAIT_OBJECT_0;
        return !exited && ProcessQuery.CreationTime(process) == holder.Started && ProcessQuery.ImagePath(process) == holder.Image;
    }

    /// <summary>Every running process's parent, image name and creation time, by PID.</summary>
    /// <returns>The table.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The process snapshot cannot be taken.</exception>
    public static IReadOnlyDictionary<int, TimedProcess> ProcessTimes() =>
        ProcessTable.Snapshot().ToDictionary(pair => pair.Key, pair => new TimedProcess(pair.Value.Parent, pair.Value.Exe, StartedAt(pair.Key)));

    /// <summary>
    /// The chain of processes from <paramref name="pid"/> up through its parents, <paramref name="pid"/> first. A parent is followed
    /// only when it was created before its child, so a reused PID ends the chain, as do a missing entry and a cycle.
    /// </summary>
    /// <param name="pid">The PID the chain starts from.</param>
    /// <param name="table">The processes, from <see cref="ProcessTimes"/>.</param>
    /// <returns>The PIDs of the chain.</returns>
    public static IReadOnlyList<int> Ancestors(int pid, IReadOnlyDictionary<int, TimedProcess> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var chain = new List<int> { pid };
        int current = pid;
        while (table.TryGetValue(current, out TimedProcess? child) && table.TryGetValue(child.Parent, out TimedProcess? parent))
        {
            bool older = parent.Started is ulong parentStarted && child.Started is ulong childStarted && parentStarted < childStarted;
            if (!older || chain.Contains(child.Parent))
            {
                break;
            }
            chain.Add(child.Parent);
            current = child.Parent;
        }
        return chain;
    }

    /// <summary>
    /// <see cref="Find(string, IReadOnlyCollection{int})"/>, with the open handles of the inspected processes found by
    /// <paramref name="handles"/>; without one, only current folders are looked at.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <param name="exclude">PIDs that are never inspected or listed.</param>
    /// <param name="handles">Lists and names the inspected processes' open handles, or <see langword="null"/> to skip handles.</param>
    /// <returns>The report.</returns>
    internal static HolderReport Find(string folder, IReadOnlyCollection<int> exclude, HandleScanner? handles)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(exclude);
        Peb.EnsureSixtyFourBit();
        IReadOnlyList<string> roots = FolderForms(folder);
        IReadOnlyDictionary<int, ProcessEntry> table = ProcessTable.Snapshot();
        byte[] ownUser = ProcessQuery.CurrentUser();
        int ownPid = Environment.ProcessId;
        var census = new Census();
        foreach (KeyValuePair<int, ProcessEntry> pair in table)
        {
            if (SystemPids.Contains(pair.Key) || pair.Key == ownPid || exclude.Contains(pair.Key))
            {
                continue;
            }
            census.Inspect(pair.Key, pair.Value.Exe, ownUser, roots);
        }
        HandleFindings findings = handles?.Invoke(census.CandidatePids, roots) ?? HandleFindings.None;
        foreach (int pid in findings.Unlisted)
        {
            census.MarkUnopened(pid);
        }
        var unnamed = new SortedSet<int>();
        foreach ((int Pid, string? Name) handle in findings.Names)
        {
            if (handle.Name is null)
            {
                _ = unnamed.Add(handle.Pid);
            }
            else if (LockedPaths.Matches(handle.Name, roots))
            {
                census.AddHold(handle.Pid, new Hold.OpenHandle(WithoutVerbatimPrefix(handle.Name)));
            }
        }
        return census.IntoReport(table, unnamed, findings.Using, exclude);
    }

    /// <summary>
    /// Every spelling of <paramref name="folder"/> a holder's path may use: fully resolved (handle names are), absolute as given (a
    /// process's current folder keeps the subst drive or junction it was set through), and with 8.3 names expanded. The resolved form
    /// comes first; a spelling equal to an earlier one is left out.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The spellings.</returns>
    /// <exception cref="IOException">The folder cannot be opened or resolved.</exception>
    internal static IReadOnlyList<string> FolderForms(string folder)
    {
        string resolved = Discovery.PathResolver.FinalPath(folder);
        string absolute = Path.GetFullPath(folder);
        string[] others = [absolute, Discovery.PathResolver.LongPath(absolute)];
        var forms = new List<string> { resolved };
        foreach (string form in others)
        {
            if (!forms.Contains(form, StringComparer.Ordinal))
            {
                forms.Add(form);
            }
        }
        return forms;
    }

    /// <summary>
    /// <c>\\?\D:\x</c> as <c>D:\x</c>; any other path, a verbatim UNC path included, as given. Unlike
    /// <see cref="Discovery.Discoverer.StripVerbatim"/>, which drops any <c>\\?\</c> or <c>\??\</c> prefix not followed by <c>UNC\</c>,
    /// a volume path such as <c>\\?\Volume{guid}\x</c> keeps its prefix, since without it the path names nothing.
    /// </summary>
    /// <param name="path">A path.</param>
    /// <returns>The path without a verbatim drive prefix.</returns>
    internal static string WithoutVerbatimPrefix(string path) =>
        path.StartsWith(Verbatim, StringComparison.Ordinal) && path.Length > Verbatim.Length + 1 && path[Verbatim.Length + 1] == ':'
            ? path[Verbatim.Length..]
            : path;

    private static ulong? StartedAt(int pid)
    {
        using SafeFileHandle? process = ProcessQuery.Open(pid, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        return process is null ? null : ProcessQuery.CreationTime(process);
    }
}
