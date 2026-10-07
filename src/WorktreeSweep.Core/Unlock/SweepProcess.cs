using System.ComponentModel;
using System.Diagnostics;
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
    /// <returns>This process; a parent that cannot be read is traced as a warning and left out.</returns>
    public static SweepProcess Current() => new(Environment.ProcessPath, OwnParentPid(), Environment.ProcessId);

    /// <summary>The PID of the process that started this one, from the process table.</summary>
    /// <returns>The parent's PID, or <see langword="null"/> when the table cannot be read or does not list this process.</returns>
    private static int? OwnParentPid()
    {
        try
        {
            return ProcessTable.Snapshot().TryGetValue(Environment.ProcessId, out ProcessEntry? entry) ? entry.Parent : null;
        }
        catch (Win32Exception error)
        {
            Trace.TraceWarning($"cannot find the process that started worktree-sweep: {error.Message}");
            return null;
        }
    }
}
