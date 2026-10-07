using WorktreeSweep.Discovery;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>What a candidate reports of its size: nothing when the scan read none of it, a lower bound when it read only part.</summary>
public sealed class KnownSizeTests
{
    /// <summary>A size the scan could not read at all is unknown, and so is a negative one.</summary>
    [Fact]
    public void AnUnknownOrNegativeSizeIsNull()
    {
        Assert.Null(Unsized().KnownSize);
        Assert.Null(Orphan(bytes: -1, unreadable: 0).KnownSize);
    }

    /// <summary>A size the scan read is known, and a lower bound once entries had to be skipped.</summary>
    [Fact]
    public void AReadSizeIsPartialWhenEntriesWereSkipped()
    {
        Assert.Equal(new KnownSize(10, Partial: false), Orphan(bytes: 10, unreadable: 0).KnownSize);
        Assert.Equal(new KnownSize(10, Partial: true), Orphan(bytes: 10, unreadable: 2).KnownSize);
    }

    /// <summary>A registered worktree reads its size, and its partial flag, from its signals.</summary>
    [Fact]
    public void ARegisteredWorktreeReadsItsSignals()
    {
        RegisteredCandidate sized = ReportSamples.Registered(
            @"repo.wt\feat",
            "feat",
            new WorktreeSignals
            {
                Size = new SizeInfo { Bytes = 10, Unreadable = 3 },
            }
        );

        Assert.Equal(new KnownSize(10, Partial: true), sized.KnownSize);
    }

    /// <summary>An orphan link, sized zero and never walked, is a known and complete size.</summary>
    [Fact]
    public void ALinkIsSizedZeroAndComplete() => Assert.Equal(new KnownSize(0, Partial: false), Orphan(OrphanKind.Link, 0, 0).KnownSize);

    /// <summary>An orphan with the given measured size.</summary>
    /// <param name="bytes">The bytes read.</param>
    /// <param name="unreadable">How many entries could not be read.</param>
    /// <returns>The candidate.</returns>
    private static OrphanCandidate Orphan(long bytes, int unreadable) => Orphan(OrphanKind.Folder, bytes, unreadable);

    /// <summary>An orphan of the given kind with the given measured size.</summary>
    /// <param name="kind">Folder or link.</param>
    /// <param name="bytes">The bytes read.</param>
    /// <param name="unreadable">How many entries could not be read.</param>
    /// <returns>The candidate.</returns>
    private static OrphanCandidate Orphan(OrphanKind kind, long bytes, int unreadable) =>
        ReportSamples.Orphan(
            @"repo.wt\stray",
            kind,
            new SizeInfo
            {
                Bytes = bytes,
                Files = 1,
                Unreadable = unreadable,
            }
        );

    /// <summary>A registered worktree whose signals carry no size.</summary>
    /// <returns>The candidate.</returns>
    private static RegisteredCandidate Unsized() => ReportSamples.Registered(@"repo.wt\feat", "feat", new WorktreeSignals());
}
