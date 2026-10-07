using WorktreeSweep.Holders;
using WorktreeSweep.Report;

namespace WorktreeSweep.Agent;

/// <summary>Why a worktree still locked after a retry is released rather than removed.</summary>
public static class LockedReason
{
    /// <summary>
    /// The release reason once a retry is still locked: <see cref="Reason.MayHold"/> when only processes that could not be inspected
    /// remain, otherwise <see cref="Reason.Locked"/>, also when nothing was found, since then a holder cannot be seen.
    /// </summary>
    /// <param name="report">The holders found in the folder.</param>
    /// <returns>The reason.</returns>
    public static Reason For(HolderReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Holders.Count == 0 && report.MayHold.Count > 0 ? Reason.MayHold : Reason.Locked;
    }
}
