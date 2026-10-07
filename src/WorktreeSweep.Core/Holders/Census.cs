using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.System.Threading;
using WorktreeSweep.Processes;

namespace WorktreeSweep.Holders;

/// <summary>What the holder finder learnt about each process, before the report is drawn up.</summary>
internal sealed class Census
{
    /// <summary>Same-user processes, each with what it was found holding so far.</summary>
    private readonly Dictionary<int, Candidate> candidates = [];

    /// <summary>Processes that could not be opened, or not fully inspected.</summary>
    private readonly HashSet<int> unopened = [];

    /// <summary>Gets the PIDs of the processes running as the current user.</summary>
    internal IReadOnlyCollection<int> CandidatePids => candidates.Keys;

    /// <summary>
    /// Looks at one process: a process running as another user is skipped; one that cannot be opened, or whose current folder cannot
    /// be read, is marked unopened; a same-user process becomes a candidate, holding its current folder when that lies in the folder.
    /// </summary>
    /// <param name="pid">The process.</param>
    /// <param name="exe">Its image name.</param>
    /// <param name="ownUser">This process's user, from <see cref="ProcessQuery.TokenUser"/>.</param>
    /// <param name="roots">Every spelling of the folder.</param>
    internal void Inspect(int pid, string exe, byte[] ownUser, IReadOnlyList<string> roots)
    {
        SafeFileHandle? readable = ProcessQuery.Open(
            pid,
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ
        );
        using SafeFileHandle? process = readable ?? ProcessQuery.Open(pid, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        byte[]? user = process is null ? null : ProcessQuery.TokenUser(process);
        if (process is null || user is null)
        {
            _ = unopened.Add(pid);
            return;
        }
        if (!ProcessQuery.SameUser(user, ownUser))
        {
            return;
        }
        var holds = new List<Hold>();
        Peb.Strings? peb = readable is null ? null : Peb.ReadStrings(process);
        if (peb is null)
        {
            Trace.WriteLine($"cannot read the current folder of {exe} (pid {pid})");
            _ = unopened.Add(pid);
        }
        else
        {
            string cwd = ProcessQuery.LongPath(peb.CurrentFolder);
            if (LockedPaths.Matches(cwd, roots))
            {
                holds.Add(new Hold.CurrentFolder(cwd));
            }
        }
        candidates[pid] = new Candidate(exe, ProcessQuery.ImagePath(process), ProcessQuery.CreationTime(process) ?? 0, peb?.CommandLine, holds);
    }

    /// <summary>Marks a process as not fully inspected.</summary>
    /// <param name="pid">The process.</param>
    internal void MarkUnopened(int pid) => unopened.Add(pid);

    /// <summary>Adds a hold to a candidate, unless it already has it; a PID that is not a candidate is ignored.</summary>
    /// <param name="pid">The process.</param>
    /// <param name="hold">What it holds.</param>
    internal void AddHold(int pid, Hold hold)
    {
        if (candidates.TryGetValue(pid, out Candidate? candidate) && !candidate.Holds.Contains(hold))
        {
            candidate.Holds.Add(hold);
        }
    }

    /// <summary>
    /// The report: every candidate holding something; then every process with an unnamed handle that holds nothing found; then every
    /// process using the folder that could not be inspected, is not excluded and is not listed yet.
    /// </summary>
    /// <param name="table">The process table, for image names.</param>
    /// <param name="unnamed">Processes with a handle that could not be named.</param>
    /// <param name="usingPids">Processes the file system reports as using the folder.</param>
    /// <param name="exclude">PIDs never listed.</param>
    /// <returns>The holders and the processes that may hold the folder, each sorted by PID.</returns>
    internal HolderReport IntoReport(
        IReadOnlyDictionary<int, ProcessEntry> table,
        IReadOnlyCollection<int> unnamed,
        IReadOnlyList<int> usingPids,
        IReadOnlyCollection<int> exclude
    )
    {
        List<Holder> holders =
        [
            .. candidates.Where(pair => pair.Value.Holds.Count > 0).Select(pair => pair.Value.ToHolder(pair.Key)).OrderBy(holder => holder.Pid),
        ];
        var listed = holders.Select(holder => holder.Pid).ToHashSet();
        List<MayHold> mayHold =
        [
            .. unnamed.Where(pid => !listed.Contains(pid)).Select(pid => new MayHold(pid, ExeOf(table, pid), MayHoldWhy.UnnamedHandle)),
        ];
        listed.UnionWith(unnamed);
        foreach (int pid in usingPids)
        {
            if (unopened.Contains(pid) && !exclude.Contains(pid) && listed.Add(pid))
            {
                mayHold.Add(new MayHold(pid, ExeOf(table, pid), MayHoldWhy.CannotOpen));
            }
        }
        return new HolderReport(holders, [.. mayHold.OrderBy(may => may.Pid)]);
    }

    private static string ExeOf(IReadOnlyDictionary<int, ProcessEntry> table, int pid) =>
        table.TryGetValue(pid, out ProcessEntry? entry) ? entry.Exe : "";

    /// <summary>A same-user process, and what it was found holding so far.</summary>
    /// <param name="Exe">The image name.</param>
    /// <param name="Image">The full image path, or <see langword="null"/> when it could not be read.</param>
    /// <param name="Started">The creation time as a FILETIME count, or 0 when it could not be read.</param>
    /// <param name="CommandLine">The command line, or <see langword="null"/> when it could not be read.</param>
    /// <param name="Holds">What it holds.</param>
    private sealed record Candidate(string Exe, string? Image, ulong Started, string? CommandLine, List<Hold> Holds)
    {
        /// <summary>The holder this candidate is, with the holds found so far.</summary>
        /// <param name="pid">The candidate's PID.</param>
        /// <returns>The holder.</returns>
        public Holder ToHolder(int pid) => new(pid, Exe, Image, Started, CommandLine, [.. Holds]);
    }
}
