using WorktreeSweep.Holders;
using WorktreeSweep.Report;

namespace WorktreeSweep.Agent;

/// <summary>Why a worktree still locked after a retry is released rather than removed.</summary>
public static class LockedReason
{
    /// <summary>
    /// The release reason once a retry is still locked: <see cref="Reason.CannotTell"/> when nothing was found but a folder's users
    /// could not be listed, <see cref="Reason.MayHold"/> when only processes that could not be inspected remain, otherwise
    /// <see cref="Reason.Locked"/>, also when nothing was found and every folder's users could be listed.
    /// </summary>
    /// <param name="report">The holders found in the folder.</param>
    /// <returns>The reason.</returns>
    public static Reason For(HolderReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Holders.Count > 0)
        {
            return Reason.Locked;
        }
        if (report.MayHold.Count > 0)
        {
            return Reason.MayHold;
        }
        return report.UnknownFolders.Count > 0 ? Reason.CannotTell : Reason.Locked;
    }
}
