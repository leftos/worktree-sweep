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
    /// parents, or when the walk meets a missing parent or a cycle first.
    /// </summary>
    /// <param name="own">The PID the walk starts from.</param>
    /// <param name="table">The process table.</param>
    /// <returns>The PIDs of the chain.</returns>
    public static IReadOnlySet<int> ParentChain(int own, IReadOnlyDictionary<int, ProcessEntry> table)
    {
        var chain = new HashSet<int> { own };
        int current = own;
        while (
            table.TryGetValue(current, out ProcessEntry? entry)
            && table.TryGetValue(entry.Parent, out ProcessEntry? parent)
            && chain.Add(entry.Parent)
        )
        {
            if (parent.Exe.Equals("sudo.exe", StringComparison.OrdinalIgnoreCase))
            {
                return chain;
            }
            current = entry.Parent;
        }
        return new HashSet<int> { own };
    }

    /// <summary>
    /// The PIDs the unlock flow never offers to stop: <see cref="ParentChain"/> of <paramref name="own"/>, plus the unelevated
    /// worktree-sweep <paramref name="sweep"/> and its children, such as the <c>sudo.exe</c> it started.
    /// </summary>
    /// <param name="own">This process's PID.</param>
    /// <param name="sweep">The unelevated worktree-sweep's PID, or <see langword="null"/> when there is none.</param>
    /// <param name="table">The process table.</param>
    /// <returns>The excluded PIDs.</returns>
    public static IReadOnlySet<int> ExcludedPids(int own, int? sweep, IReadOnlyDictionary<int, ProcessEntry> table)
    {
        var excluded = new HashSet<int>(ParentChain(own, table));
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
