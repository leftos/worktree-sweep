using System.Diagnostics;
using WorktreeSweep.Discovery;
using WorktreeSweep.Recycle;
using WorktreeSweep.Report;

namespace WorktreeSweep.Removal;

/// <summary>Turns a pick into the removal to carry out, and reads the Recycle Bin settings one needs.</summary>
public static class RemovalPlanner
{
    /// <summary>
    /// How <paramref name="candidate"/> can be removed, given the Recycle Bin <paramref name="capacity"/> of its volume
    /// (<see langword="null"/> when unknown): a link loses only the link, a prunable registration is pruned, and a folder goes to the
    /// Recycle Bin when it fits (see <see cref="RecycleDecider.Decide"/>; a size that is unknown or negative counts as unknown, which
    /// never fits), else needs the user's yes to delete permanently.
    /// </summary>
    /// <param name="candidate">The pick.</param>
    /// <param name="capacity">The Recycle Bin settings of the pick's volume; <see langword="null"/> when unknown.</param>
    /// <returns>What to run, or why a permanent delete needs the user's yes.</returns>
    public static PlanNeed Plan(Candidate candidate, BinCapacity? capacity)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate is OrphanCandidate { Orphan.Kind: OrphanKind.Link })
        {
            return new PlanNeed.Run(new RemoveAction.RemoveLink());
        }
        if (candidate is RegisteredCandidate { Record.Prunable: not null })
        {
            return new PlanNeed.Run(new RemoveAction.PruneRegistration());
        }
        long? size = candidate.SizeBytes;
        ulong bytes = size is null || size < 0 ? ulong.MaxValue : (ulong)size.Value;
        RecycleDecision decision = RecycleDecider.Decide(bytes, capacity);
        return decision switch
        {
            RecycleDecision.Recycle => new PlanNeed.Run(new RemoveAction.Delete(DeleteMethod.Recycle)),
            RecycleDecision.AskPermanent ask => new PlanNeed.AskPermanent(ask.Reason),
            _ => throw new UnreachableException($"unknown recycle decision {decision}"),
        };
    }

    /// <summary>
    /// Whether the Recycle Bin capacity is read for this pick: <see langword="false"/> for a link and a prunable registration.
    /// </summary>
    /// <param name="candidate">The pick.</param>
    /// <returns><see langword="true"/> when the pick's action depends on the capacity.</returns>
    public static bool NeedsCapacity(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate switch
        {
            RegisteredCandidate registered => registered.Record.Prunable is null,
            OrphanCandidate orphan => orphan.Orphan.Kind != OrphanKind.Link,
            _ => throw new UnreachableException($"unknown candidate {candidate}"),
        };
    }

    /// <summary>
    /// The Recycle Bin settings of the volume <paramref name="path"/> is on; <see langword="null"/>, with a warning, when they cannot
    /// be read.
    /// </summary>
    /// <param name="path">A path on the volume.</param>
    /// <returns>The settings, or <see langword="null"/>.</returns>
    public static BinCapacity? ReadCapacity(string path)
    {
        try
        {
            return BinCapacityReader.Read(path);
        }
        catch (IOException error)
        {
            Trace.TraceWarning($"cannot read the Recycle Bin size for {path}: {error.Message}");
            return null;
        }
    }
}
