using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.System.Threading;
using WorktreeSweep.Holders;
using WorktreeSweep.Processes;

namespace WorktreeSweep.Unlock;

/// <summary>This unelevated worktree-sweep, as the unlock offer names it to the elevated side.</summary>
/// <param name="ExePath">The path of the running program, or <see langword="null"/> when it is not known.</param>
/// <param name="CallerPid">The process that started this one, usually the user's shell, or <see langword="null"/> when it is not
/// known.</param>
/// <param name="Pid">This process.</param>
public sealed record SweepProcess(string? ExePath, int? CallerPid, int Pid)
{
    /// <summary>The running process: its program path, its parent and its PID.</summary>
    /// <returns>This process; a caller that cannot be read, or whose PID a newer process has taken over, is traced as a warning
    /// and left out.</returns>
    public static SweepProcess Current() => new(Environment.ProcessPath, OwnParentPid(), Environment.ProcessId);

    /// <summary>The parent's PID to keep as the caller, given the creation times of both processes.</summary>
    /// <param name="parentPid">The parent's PID, from the process table.</param>
    /// <param name="parentCreated">The parent's creation time, or <see langword="null"/> when it cannot be read.</param>
    /// <param name="ownCreated">This process's creation time, or <see langword="null"/> when it cannot be read.</param>
    /// <returns><paramref name="parentPid"/> when the parent started no later than this process; <see langword="null"/> when a
    /// creation time is unknown or the parent started later, so its PID has been reused.</returns>
    internal static int? KeepParent(int parentPid, ulong? parentCreated, ulong? ownCreated)
    {
        if (parentCreated is not ulong parent || ownCreated is not ulong own)
        {
            return null;
        }
        return parent <= own ? parentPid : null;
    }

    /// <summary>The PID of the process that started this one, when it is still that process.</summary>
    /// <returns>The parent's PID, or <see langword="null"/> when the table cannot be read or does not list this process, the
    /// parent can no longer be opened, or its PID now names a process started after this one.</returns>
    private static int? OwnParentPid()
    {
        try
        {
            if (!ProcessTable.Snapshot().TryGetValue(Environment.ProcessId, out ProcessEntry? entry))
            {
                return null;
            }
            int parentPid = entry.Parent;
            using SafeFileHandle? parent = ProcessQuery.Open(parentPid, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
            if (parent is null)
            {
                Trace.TraceWarning($"the process that started worktree-sweep has exited or cannot be read: pid {parentPid}");
                return null;
            }
            ulong? parentCreated = ProcessQuery.CreationTime(parent);
            ulong? ownCreated = OwnCreationTime();
            int? kept = KeepParent(parentPid, parentCreated, ownCreated);
            if (kept is null && parentCreated is ulong created && ownCreated is ulong ownTime && created > ownTime)
            {
                Trace.TraceWarning($"pid {parentPid} is not the process that started worktree-sweep: it was created after this one");
            }
            return kept;
        }
        catch (Win32Exception error)
        {
            Trace.TraceWarning($"cannot find the process that started worktree-sweep: {error.Message}");
            return null;
        }
    }

    /// <summary>This process's creation time, or <see langword="null"/> when it cannot be read.</summary>
    private static ulong? OwnCreationTime()
    {
        using SafeFileHandle? own = ProcessQuery.Open(Environment.ProcessId, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        return own is null ? null : ProcessQuery.CreationTime(own);
    }
}
