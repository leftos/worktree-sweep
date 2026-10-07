using WorktreeSweep.Report;

namespace WorktreeSweep.Recycle;

/// <summary>Decides whether a folder fits in its volume's Recycle Bin.</summary>
public static class RecycleDecider
{
    private const ulong BytesPerMb = 1024 * 1024;

    /// <summary>
    /// Whether a folder of <paramref name="bytes"/> can go to a Recycle Bin with <paramref name="capacity"/>. A folder exactly the
    /// bin's size still fits.
    /// </summary>
    /// <param name="bytes">The folder's size on disk.</param>
    /// <param name="capacity">The bin's settings; <see langword="null"/> when unknown.</param>
    /// <returns><see cref="RecycleDecision.Recycle"/>, or <see cref="RecycleDecision.AskPermanent"/> with the reason.</returns>
    public static RecycleDecision Decide(ulong bytes, BinCapacity? capacity)
    {
        if (capacity is null)
        {
            return new RecycleDecision.AskPermanent("the Recycle Bin size of its volume is unknown");
        }
        if (capacity.NukeOnDelete)
        {
            return new RecycleDecision.AskPermanent("the Recycle Bin of its volume is set to delete files immediately (NukeOnDelete)");
        }
        ulong maxBytes = capacity.MaxCapacityMb * BytesPerMb;
        if (bytes <= maxBytes)
        {
            return new RecycleDecision.Recycle();
        }
        return new RecycleDecision.AskPermanent(
            $"its {HumanBytes(bytes)} is more than the {HumanBytes(maxBytes)} the Recycle Bin of its volume holds"
        );
    }

    /// <summary>The size as the report shows it; a size past <see cref="long.MaxValue"/> shows as that.</summary>
    private static string HumanBytes(ulong bytes) => ReportTable.HumanBytes((long)Math.Min(bytes, long.MaxValue));
}
