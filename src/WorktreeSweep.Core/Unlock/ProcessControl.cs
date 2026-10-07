using WorktreeSweep.Holders;
using WorktreeSweep.Processes;

namespace WorktreeSweep.Unlock;

/// <summary>The real process operations, over <see cref="ProcessTable"/>, <see cref="HolderFinder"/> and
/// <see cref="ProcessStopper"/>.</summary>
public sealed class ProcessControl : IProcessControl
{
    /// <inheritdoc/>
    public IReadOnlyDictionary<int, ProcessEntry> Snapshot() => ProcessTable.Snapshot();

    /// <inheritdoc/>
    public ulong? StartedAt(int pid) => HolderFinder.StartedAt(pid);

    /// <inheritdoc/>
    public void Stop(int pid, string exe) => ProcessStopper.Stop(pid, exe, ProcessStopper.DefaultWait);
}
