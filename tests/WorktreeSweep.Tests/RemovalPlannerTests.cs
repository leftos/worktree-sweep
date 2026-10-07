using WorktreeSweep.Discovery;
using WorktreeSweep.Recycle;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>How a pick is removed: a link, a prunable registration, the Recycle Bin or a permanent delete.</summary>
public sealed class RemovalPlannerTests
{
    /// <summary>A mebibyte (1024 × 1024 bytes).</summary>
    private const long Mb = 1024 * 1024;

    /// <summary>A bin holding one MB.</summary>
    private static readonly BinCapacity OneMbBin = new(1, NukeOnDelete: false);

    /// <summary>A link loses only the link, and no Recycle Bin capacity is read for it.</summary>
    [Fact]
    public void PlanActionLinkNeedsNoCapacity()
    {
        OrphanCandidate link = Orphan(OrphanKind.Link, bytes: 0);

        Assert.False(RemovalPlanner.NeedsCapacity(link));
        PlanNeed.Run run = Assert.IsType<PlanNeed.Run>(RemovalPlanner.Plan(link, capacity: null));
        Assert.IsType<RemoveAction.RemoveLink>(run.Action);
    }

    /// <summary>A folder bigger than the bin asks for a permanent delete, naming the bin's size.</summary>
    [Fact]
    public void PlanActionOverCapacityAsksPermanent()
    {
        OrphanCandidate big = Orphan(OrphanKind.Folder, bytes: 2 * Mb);

        Assert.True(RemovalPlanner.NeedsCapacity(big));
        PlanNeed.AskPermanent ask = Assert.IsType<PlanNeed.AskPermanent>(RemovalPlanner.Plan(big, OneMbBin));
        Assert.Contains("more than", ask.Reason, StringComparison.Ordinal);
    }

    /// <summary>An unknown bin size asks for a permanent delete.</summary>
    [Fact]
    public void PlanActionUnknownCapacityAsksPermanent()
    {
        OrphanCandidate small = Orphan(OrphanKind.Folder, bytes: 10);

        PlanNeed.AskPermanent ask = Assert.IsType<PlanNeed.AskPermanent>(RemovalPlanner.Plan(small, capacity: null));
        Assert.Contains("unknown", ask.Reason, StringComparison.Ordinal);
    }

    /// <summary>A pick whose size is negative counts as unknown, which never fits: it asks for a permanent delete.</summary>
    [Fact]
    public void PlanActionNegativeSizeAsksPermanent()
    {
        OrphanCandidate negative = Orphan(OrphanKind.Folder, bytes: -1);

        PlanNeed.AskPermanent ask = Assert.IsType<PlanNeed.AskPermanent>(RemovalPlanner.Plan(negative, OneMbBin));
        Assert.Contains("more than", ask.Reason, StringComparison.Ordinal);
    }

    /// <summary>A folder exactly the bin's size still recycles.</summary>
    [Fact]
    public void PlanActionFitsRecycles()
    {
        OrphanCandidate exact = Orphan(OrphanKind.Folder, bytes: Mb);

        PlanNeed.Run run = Assert.IsType<PlanNeed.Run>(RemovalPlanner.Plan(exact, OneMbBin));
        RemoveAction.Delete delete = Assert.IsType<RemoveAction.Delete>(run.Action);
        Assert.Equal(DeleteMethod.Recycle, delete.Method);
    }

    /// <summary>A registration whose folder is already gone is pruned, and no Recycle Bin capacity is read for it.</summary>
    [Fact]
    public void PlanActionPrunableRegistrationPrunes()
    {
        RegisteredCandidate registered = ReportSamples.Registered("yaat.wt/gone", "feat", new WorktreeSignals());
        RegisteredCandidate prunable = registered with { Record = registered.Record with { Prunable = "its git dir is gone" } };

        Assert.False(RemovalPlanner.NeedsCapacity(prunable));
        PlanNeed.Run run = Assert.IsType<PlanNeed.Run>(RemovalPlanner.Plan(prunable, capacity: null));
        Assert.IsType<RemoveAction.PruneRegistration>(run.Action);
    }

    /// <summary>An orphan of <paramref name="kind"/> holding <paramref name="bytes"/>.</summary>
    /// <param name="kind">Folder or link.</param>
    /// <param name="bytes">The bytes it holds.</param>
    /// <returns>The candidate.</returns>
    private static OrphanCandidate Orphan(OrphanKind kind, long bytes) =>
        ReportSamples.Orphan("stray", kind, ReportSamples.Size(bytes, ReportSamples.Now));
}
