using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using WorktreeSweep.Holders;
using WorktreeSweep.Processes;

namespace WorktreeSweep.Unlock;

/// <summary>This unelevated worktree-sweep, as the unlock offer names it to the elevated side.</summary>
/// <param name="ExePath">The path of the running program, or <see langword="null"/> when it is not known.</param>
/// <param name="CallerPid">The process that started this one, usually the user's shell, or <see langword="null"/> when it is not
/// known.</param>
/// <param name="CallerStarted">The caller's creation time, or <see langword="null"/> when it is not known.</param>
/// <param name="Pid">This process.</param>
public sealed record SweepProcess(string? ExePath, int? CallerPid, ulong? CallerStarted, int Pid)
{
    /// <summary>The running process: its program path, its caller, that caller's creation time and its PID.</summary>
    /// <returns>This process; the caller is left out only when a newer process has provably taken the parent's PID, and each
    /// creation time that cannot be read is traced as a warning and the caller kept.</returns>
    public static SweepProcess Current()
    {
        (int? callerPid, ulong? callerStarted) = OwnParentPid();
        return new(Environment.ProcessPath, callerPid, callerStarted, Environment.ProcessId);
    }

    /// <summary>The parent's PID to keep as the caller. Only proof of reuse drops it.</summary>
    /// <param name="parentPid">The parent's PID, from the process table.</param>
    /// <param name="parentCreated">The parent's creation time, or <see langword="null"/> when it cannot be read.</param>
    /// <param name="ownCreated">This process's creation time, or <see langword="null"/> when it cannot be read.</param>
    /// <returns><see langword="null"/> only when both times are known and the parent's is later than this process's own, so its PID
    /// has been reused; <paramref name="parentPid"/> otherwise, an unknown time being no proof of reuse.</returns>
    internal static int? KeepParent(int parentPid, ulong? parentCreated, ulong? ownCreated)
    {
        bool reused = parentCreated is ulong parent && ownCreated is ulong own && parent > own;
        return reused ? null : parentPid;
    }

    /// <summary>The PID of the process that started this one and its creation time, unless a newer process has provably taken
    /// its PID.</summary>
    /// <returns>The parent's PID and creation time, or <see langword="null"/> for both when the table cannot be read or does not
    /// list this process, or when the parent provably started after this one; the creation time is also
    /// <see langword="null"/> when it cannot be read.</returns>
    private static (int? Pid, ulong? Started) OwnParentPid()
    {
        try
        {
            if (!ProcessTable.Snapshot().TryGetValue(Environment.ProcessId, out ProcessEntry? entry))
            {
                return (null, null);
            }
            int parentPid = entry.Parent;
            ulong? parentCreated = HolderFinder.StartedAt(parentPid);
            using SafeFileHandle own = PInvoke.GetCurrentProcess_SafeHandle();
            ulong? ownCreated = ProcessQuery.CreationTime(own);
            int? kept = KeepParent(parentPid, parentCreated, ownCreated);
            if (kept is null)
            {
                Trace.TraceWarning($"pid {parentPid} is not the process that started worktree-sweep: it was created after this one");
                return (null, null);
            }
            if (parentCreated is null)
            {
                Trace.TraceWarning($"cannot read when the process that started worktree-sweep began: pid {parentPid}");
            }
            else if (ownCreated is null)
            {
                Trace.TraceWarning("cannot read when this worktree-sweep began");
            }
            return (kept, parentCreated);
        }
        catch (Win32Exception error)
        {
            Trace.TraceWarning($"cannot find the process that started worktree-sweep: {error.Message}");
            return (null, null);
        }
    }
}
