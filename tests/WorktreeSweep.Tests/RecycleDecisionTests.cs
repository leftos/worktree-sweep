using WorktreeSweep.Recycle;

namespace WorktreeSweep.Tests;

/// <summary>Whether a folder fits in its volume's Recycle Bin.</summary>
public sealed class RecycleDecisionTests
{
    private const ulong Mb = 1024 * 1024;

    /// <summary>D:'s measured bin size, in MB.</summary>
    private const uint DriveDBinMb = 14_844;

    /// <summary>A folder well under the bin's size is recycled.</summary>
    [Fact]
    public void RecycleDecisionUnder() => Assert.IsType<RecycleDecision.Recycle>(RecycleDecider.Decide(100 * Mb, Capacity(DriveDBinMb)));

    /// <summary>A folder over the bin's size asks for a permanent delete, naming the bin's size.</summary>
    [Fact]
    public void RecycleDecisionOver()
    {
        RecycleDecision decision = RecycleDecider.Decide((DriveDBinMb + 1) * Mb, Capacity(DriveDBinMb));

        RecycleDecision.AskPermanent ask = Assert.IsType<RecycleDecision.AskPermanent>(decision);
        Assert.Contains("14.5 GB", ask.Reason, StringComparison.Ordinal);
    }

    /// <summary>A folder exactly the bin's size still fits; one byte more does not.</summary>
    [Fact]
    public void RecycleDecisionEqualBoundary()
    {
        Assert.IsType<RecycleDecision.Recycle>(RecycleDecider.Decide(DriveDBinMb * Mb, Capacity(DriveDBinMb)));
        Assert.IsType<RecycleDecision.AskPermanent>(RecycleDecider.Decide((DriveDBinMb * Mb) + 1, Capacity(DriveDBinMb)));
    }

    /// <summary>A bin set to delete at once asks for a permanent delete, whatever the size.</summary>
    [Fact]
    public void RecycleDecisionNuke()
    {
        RecycleDecision decision = RecycleDecider.Decide(1, new BinCapacity(DriveDBinMb, NukeOnDelete: true));

        RecycleDecision.AskPermanent ask = Assert.IsType<RecycleDecision.AskPermanent>(decision);
        Assert.Contains("NukeOnDelete", ask.Reason, StringComparison.Ordinal);
    }

    /// <summary>An unknown bin size asks for a permanent delete.</summary>
    [Fact]
    public void RecycleDecisionUnknown()
    {
        RecycleDecision.AskPermanent ask = Assert.IsType<RecycleDecision.AskPermanent>(RecycleDecider.Decide(1, null));
        Assert.Contains("unknown", ask.Reason, StringComparison.Ordinal);
    }

    private static BinCapacity Capacity(uint maxCapacityMb) => new(maxCapacityMb, NukeOnDelete: false);
}
