using WorktreeSweep.Holders;

namespace WorktreeSweep.Agent;

/// <summary>Whether the process asking for a removal holds the folder itself, through its own parent chain.</summary>
public static class CallerHolds
{
    /// <summary>Whether a process in <paramref name="ownChain"/> holds something inside the folder.</summary>
    /// <param name="ownChain">The PIDs of the caller and its ancestors.</param>
    /// <param name="report">The holders found in the folder.</param>
    /// <returns><see langword="true"/> when a holder in the chain holds something; a may-hold never counts.</returns>
    public static bool Check(IReadOnlySet<int> ownChain, HolderReport report)
    {
        ArgumentNullException.ThrowIfNull(ownChain);
        ArgumentNullException.ThrowIfNull(report);
        return report.Holders.Any(holder => holder.Holds.Count > 0 && ownChain.Contains(holder.Pid));
    }
}
