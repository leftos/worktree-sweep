using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;

namespace WorktreeSweep.Processes;

/// <summary>The running processes by PID, and the PIDs the unlock flow must never stop.</summary>
public static class ProcessTable
{
    /// <summary>Every running process's parent and image name, by PID.</summary>
    /// <returns>The table; a PID seen twice keeps its last entry.</returns>
    /// <exception cref="Win32Exception">
    /// The process snapshot cannot be taken, or its walk stops before the last process: a short table would drop exclusions.
    /// </exception>
    public static IReadOnlyDictionary<int, ProcessEntry> Snapshot()
    {
        using SafeFileHandle snapshot = PInvoke.CreateToolhelp32Snapshot_SafeHandle(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "cannot take a snapshot of the running processes");
        }
        var entry = new PROCESSENTRY32W { dwSize = (uint)Unsafe.SizeOf<PROCESSENTRY32W>() };
        var table = new Dictionary<int, ProcessEntry>();
        bool more = PInvoke.Process32FirstW(snapshot, ref entry);
        while (more)
        {
            table[(int)entry.th32ProcessID] = new ProcessEntry((int)entry.th32ParentProcessID, entry.szExeFile.ToString());
            more = PInvoke.Process32NextW(snapshot, ref entry);
        }
        int error = Marshal.GetLastPInvokeError();
        if (error != (int)WIN32_ERROR.ERROR_NO_MORE_FILES)
        {
            throw new Win32Exception(error, "cannot list the running processes");
        }
        return table;
    }

    /// <summary>
    /// The PID and, when one of its ancestors is a <c>sudo.exe</c>, every ancestor up to and including the nearest one: the
    /// elevated side's own console and the <c>sudo.exe</c> that started it. Only the PID when no <c>sudo.exe</c> is among the
    /// parents, or when the walk meets a missing parent, a cycle, or a parent started after its child (a reused PID) first. This
    /// differs from <c>HolderFinder.Ancestors</c>, which keeps a link only on proof that the parent is the older one because it
    /// claims an ancestry: a walk that only excludes PIDs keeps a link on every absence of proof of reuse, an unknown time
    /// included, so it never excludes a PID on a guess.
    /// </summary>
    /// <param name="own">The PID the walk starts from.</param>
    /// <param name="table">The process table.</param>
    /// <param name="startedAt">A process's creation time by PID, or <see langword="null"/> when it cannot be read; a parent whose
    /// known time is later than its child's is a reused PID and ends the walk.</param>
    /// <returns>The PIDs of the chain.</returns>
    public static IReadOnlySet<int> ParentChain(int own, IReadOnlyDictionary<int, ProcessEntry> table, Func<int, ulong?> startedAt)
    {
        ArgumentNullException.ThrowIfNull(startedAt);
        var chain = new HashSet<int> { own };
        int current = own;
        ulong? currentStarted = startedAt(own);
        while (
            table.TryGetValue(current, out ProcessEntry? entry)
            && table.TryGetValue(entry.Parent, out ProcessEntry? parent)
            && chain.Add(entry.Parent)
        )
        {
            ulong? parentStarted = startedAt(entry.Parent);
            if (IsReused(parentStarted, currentStarted))
            {
                return new HashSet<int> { own };
            }
            if (parent.Exe.Equals("sudo.exe", StringComparison.OrdinalIgnoreCase))
            {
                return chain;
            }
            current = entry.Parent;
            currentStarted = parentStarted;
        }
        return new HashSet<int> { own };
    }

    /// <summary>Whether a parent's known creation time is later than its child's, so the parent's PID has been reused by another
    /// process and no longer names the parent.</summary>
    /// <param name="parentStarted">The parent's creation time, or <see langword="null"/> when it cannot be read.</param>
    /// <param name="childStarted">The child's creation time, or <see langword="null"/> when it cannot be read.</param>
    /// <returns><see langword="true"/> only when both times are known and the parent's is later.</returns>
    internal static bool IsReused(ulong? parentStarted, ulong? childStarted) =>
        parentStarted is ulong parent && childStarted is ulong child && parent > child;

    /// <summary>
    /// The PIDs the unlock flow never offers to stop: <see cref="ParentChain"/> of <paramref name="own"/>, plus the unelevated
    /// worktree-sweep <paramref name="sweep"/> and its children, such as the <c>sudo.exe</c> it started.
    /// </summary>
    /// <param name="own">This process's PID.</param>
    /// <param name="sweep">The unelevated worktree-sweep's PID, or <see langword="null"/> when there is none.</param>
    /// <param name="table">The process table.</param>
    /// <param name="startedAt">A process's creation time by PID, as <see cref="ParentChain"/> takes it.</param>
    /// <returns>The excluded PIDs.</returns>
    public static IReadOnlySet<int> ExcludedPids(int own, int? sweep, IReadOnlyDictionary<int, ProcessEntry> table, Func<int, ulong?> startedAt)
    {
        var excluded = new HashSet<int>(ParentChain(own, table, startedAt));
        if (sweep is int sweepPid)
        {
            _ = excluded.Add(sweepPid);
            excluded.UnionWith(table.Where(pair => pair.Value.Parent == sweepPid).Select(pair => pair.Key));
        }
        return excluded;
    }

    /// <summary>
    /// Whether <paramref name="pid"/> is still a process named <paramref name="name"/>, compared case-insensitively, so a PID
    /// reused by another program since the scan is never acted on.
    /// </summary>
    /// <param name="pid">The PID the scan saw.</param>
    /// <param name="name">The image name the scan saw, such as <c>code.exe</c>.</param>
    /// <param name="table">The process table.</param>
    /// <returns><see langword="true"/> when the table holds the PID under that name.</returns>
    public static bool IsSameProcess(int pid, string name, IReadOnlyDictionary<int, ProcessEntry> table) =>
        table.TryGetValue(pid, out ProcessEntry? entry) && entry.Exe.Equals(name, StringComparison.OrdinalIgnoreCase);
}
