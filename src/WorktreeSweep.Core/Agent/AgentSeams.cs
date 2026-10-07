using WorktreeSweep.Holders;
using WorktreeSweep.Processes;
using WorktreeSweep.Recycle;

namespace WorktreeSweep.Agent;

/// <summary>What <see cref="AgentRemover"/> reaches outside the process through, so a test can stand in for each.</summary>
internal sealed record AgentSeams
{
    /// <summary>Gets the seams a real removal uses.</summary>
    public static AgentSeams Production { get; } =
        new()
        {
            EnterMainWorktree = Directory.SetCurrentDirectory,
            Recycle = ShellRecycler.Recycle,
            RecycleTimeout = TimeSpan.FromSeconds(30),
            FindHolders = HolderFinder.Find,
            OwnChain = () => HolderFinder.Ancestors(Environment.ProcessId, HolderFinder.ProcessTimes()).ToHashSet(),
            Stop = holder => ProcessStopper.Stop(holder.Pid, holder.Exe, holder.Started == 0 ? null : holder.Started, ProcessStopper.DefaultWait),
        };

    /// <summary>Gets what makes the main worktree the process's current folder, so the process itself never holds the worktree.</summary>
    public required Action<string> EnterMainWorktree { get; init; }

    /// <summary>Gets what moves a folder to the Recycle Bin, throwing <see cref="Removal.LockedException"/> on a lock.</summary>
    public required Action<string> Recycle { get; init; }

    /// <summary>Gets how long one recycle may take before the worktree is released instead.</summary>
    public required TimeSpan RecycleTimeout { get; init; }

    /// <summary>Gets what finds the processes holding a folder, never listing the PIDs it is given.</summary>
    public required Func<string, IReadOnlyCollection<int>, HolderReport> FindHolders { get; init; }

    /// <summary>Gets what lists the PIDs of this process and its ancestors.</summary>
    public required Func<IReadOnlySet<int>> OwnChain { get; init; }

    /// <summary>Gets what stops a holder, throwing <see cref="InvalidOperationException"/> or a Win32 exception when it cannot.</summary>
    public required Action<Holder> Stop { get; init; }
}
