using WorktreeSweep.Discovery;
using WorktreeSweep.Removal;

namespace WorktreeSweep.Tests;

/// <summary>The removal summary's lines, as goldens over hand-built outcomes.</summary>
public sealed class SweepSummaryTests
{
    private const long Mib = 1024 * 1024;

    /// <summary>Every outcome gets its own wording, notes follow after <c>; </c>, and the total counts the bytes freed and recycled.</summary>
    [Fact]
    public void MixOfOutcomesMatchesTheGolden()
    {
        Swept[] swept =
        [
            Entry(@"yaat.wt\recycled", new Outcome.Recycled(3 * Mib / 2)),
            Entry(@"yaat.wt\permanent", new Outcome.Permanent(2 * Mib), "branch feat deleted", "prune failed: boom"),
            Entry(@"yaat.wt\link", new Outcome.LinkRemoved()),
            Entry(@"yaat.wt\gone", new Outcome.Pruned()),
            Entry(@"yaat.wt\skipped", new Outcome.Skipped("not confirmed")),
            Entry(@"yaat.wt\failed", new Outcome.Failed(@"locked (D:\yaat.wt\failed\held.txt)")),
        ];

        IReadOnlyList<string> lines = SweepSummary.Lines(swept, ReportSamples.Root);

        string[] golden =
        [
            @"yaat.wt\recycled: removed (recycled)",
            @"yaat.wt\permanent: removed (permanent); branch feat deleted; prune failed: boom",
            @"yaat.wt\link: removed (link only)",
            @"yaat.wt\gone: removed (registration pruned)",
            @"yaat.wt\skipped: skipped (not confirmed)",
            @"yaat.wt\failed: failed: locked (D:\yaat.wt\failed\held.txt)",
            "4 removed, 1 skipped, 1 failed; 3.5 MB freed (1.5 MB of it in the Recycle Bin)",
        ];
        Assert.Equal(golden, lines);
    }

    /// <summary>Without a recycled pick the total names no Recycle Bin share.</summary>
    [Fact]
    public void TotalWithoutRecyclingHasNoBinShare()
    {
        Swept[] swept = [Entry(@"yaat.wt\permanent", new Outcome.Permanent(512)), Entry(@"yaat.wt\skipped", new Outcome.Skipped("cancelled"))];

        IReadOnlyList<string> lines = SweepSummary.Lines(swept, ReportSamples.Root);

        Assert.Equal(
            [@"yaat.wt\permanent: removed (permanent)", @"yaat.wt\skipped: skipped (cancelled)", "1 removed, 1 skipped, 0 failed; 512 B freed"],
            lines
        );
    }

    /// <summary>A removed folder whose size is unknown makes the total read "at least" and count it, and the bin share too.</summary>
    [Fact]
    public void UnknownSizeReadsAsAtLeastAndIsCounted()
    {
        Swept[] swept = [Entry(@"yaat.wt\recycled", new Outcome.Recycled(Mib)), Entry(@"yaat.wt\unknown", new Outcome.Recycled(null))];

        IReadOnlyList<string> lines = SweepSummary.Lines(swept, ReportSamples.Root);

        Assert.Equal(
            [
                @"yaat.wt\recycled: removed (recycled)",
                @"yaat.wt\unknown: removed (recycled)",
                "2 removed, 0 skipped, 0 failed; at least 1.0 MB freed (1 of unknown size) (at least 1.0 MB of it in the Recycle Bin)",
            ],
            lines
        );
    }

    /// <summary>A permanent delete of unknown size alone reads as at least zero bytes freed, with the count.</summary>
    [Fact]
    public void PermanentDeleteOfUnknownSizeAloneReadsAsAtLeastZero()
    {
        Swept[] swept = [Entry(@"yaat.wt\unknown", new Outcome.Permanent(null))];

        IReadOnlyList<string> lines = SweepSummary.Lines(swept, ReportSamples.Root);

        Assert.Equal([@"yaat.wt\unknown: removed (permanent)", "1 removed, 0 skipped, 0 failed; at least 0 B freed (1 of unknown size)"], lines);
    }

    /// <summary>One pick under the sample root with its outcome and notes.</summary>
    /// <param name="path">The pick's path relative to the root.</param>
    /// <param name="outcome">What happened to it.</param>
    /// <param name="notes">Its follow-up notes.</param>
    /// <returns>The entry.</returns>
    private static Swept Entry(string path, Outcome outcome, params string[] notes) =>
        new(ReportSamples.Orphan(path, OrphanKind.Folder, ReportSamples.Size(0, ReportSamples.Now)), outcome, notes);
}
