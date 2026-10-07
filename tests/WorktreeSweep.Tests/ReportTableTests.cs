using WorktreeSweep.Discovery;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;
using static WorktreeSweep.Tests.ReportSamples;

namespace WorktreeSweep.Tests;

/// <summary>The candidates' table order and the <c>--list</c> table.</summary>
public sealed class ReportTableTests
{
    /// <summary>Released worktrees come first, then the other registered ones by repo and path, then the orphans by path.</summary>
    [Fact]
    public void OrderedPutsReleasedFirstThenRegisteredThenOrphans()
    {
        RegisteredCandidate plainB = Registered(@"b.wt\two", "two", new WorktreeSignals()) with { Repo = Under("b") };
        RegisteredCandidate plainA = Registered(@"a.wt\one", "one", new WorktreeSignals()) with { Repo = Under("a") };
        ScanReport report = ReportOf(Orphan(@"a.wt\stray", OrphanKind.Folder, Size(0, Now)), plainB, Released("b", @"b.wt\held"), plainA);

        string[] paths = [.. report.Ordered().Select(candidate => candidate.Path)];

        string[] expected = [Under(@"b.wt\held"), Under(@"a.wt\one"), Under(@"b.wt\two"), Under(@"a.wt\stray")];
        Assert.Equal(expected, paths);
    }

    /// <summary>A released worktree git also locks shows both flags, released first.</summary>
    [Fact]
    public void ReleasedGitLockedRowFlagsReleasedFirst()
    {
        RegisteredCandidate held = Released("yaat", @"yaat.wt\held");
        held = held with { Record = held.Record with { Locked = "" } };

        string row = ReportTable.Render(ReportOf(held), Now).Split('\n')[1];

        Assert.EndsWith("  released, git-locked", row, StringComparison.Ordinal);
    }

    /// <summary>Every signal has its cell: merge word, dirty counts, upstream, age, binary size and flags, in table order.</summary>
    [Fact]
    public void RenderTableFormatsSignals()
    {
        var merged = new WorktreeSignals
        {
            MergeState = MergeState.Ancestor,
            MergeStateAgainst = "main",
            Dirty = new Dirty(),
            Upstream = Upstream.Gone,
            LastActivityUnix = Now - (3 * Day),
            Size = Size(31_030_000_000, Now),
        };
        var unmerged = new WorktreeSignals
        {
            MergeState = MergeState.Unmerged(4),
            MergeStateAgainst = "main",
            Dirty = new Dirty { Modified = 3, Untracked = 2 },
            Upstream = Upstream.Tracking(2),
            LastActivityUnix = Now - (35 * Day),
            Size = Size(1536, Now),
        };
        RegisteredCandidate locked = Registered(@"yaat.wt\eram-am\yaat", "eram-am", unmerged);
        locked = locked with { Record = locked.Record with { Locked = "on a USB drive" } };
        OrphanCandidate stale = Orphan(@"yaat.wt\eram-qx", OrphanKind.Folder, Size(0, Now - (400 * Day)));
        stale = stale with { Orphan = stale.Orphan with { StaleGitdir = true } };
        ScanReport report = ReportOf(
            stale,
            Orphan(@"yaat-server.wt\yaat", OrphanKind.Link, Size(0, Now - (2 * 3600))),
            locked,
            Registered(@"yaat.wt\eram-co\yaat", "eram-co", merged)
        );

        string expected =
            "#  PATH                  KIND      BRANCH   MERGE       DIRTY  UPSTREAM  ACTIVE     SIZE  FLAGS\n"
            + "1  yaat.wt\\eram-am\\yaat  worktree  eram-am  unmerged 4  3M 2?  +2            5w   1.5 KB  git-locked\n"
            + "2  yaat.wt\\eram-co\\yaat  worktree  eram-co  merged             gone          3d  28.9 GB\n"
            + "3  yaat-server.wt\\yaat   link                                                2h\n"
            + "4  yaat.wt\\eram-qx       orphan                                              1y      0 B  stale .git\n";
        Assert.Equal(expected, ReportTable.Render(report, Now));
    }

    /// <summary>A path too long for the table keeps its end behind a <c>…</c>, and no line is wider than the table.</summary>
    [Fact]
    public void RenderTableTruncatesLongPaths()
    {
        string longPath = $@"yaat.wt\{string.Concat(Enumerable.Repeat("deeply-nested-folder", 10))}\tail-segment";

        string table = ReportTable.Render(ReportOf(Orphan(longPath, OrphanKind.Folder, Size(10, Now))), Now);

        string[] lines = table.TrimEnd('\n').Split('\n');
        Assert.All(lines, line => Assert.True(line.EnumerateRunes().Count() <= ReportTable.Width, $"line too wide: {line}"));
        Assert.StartsWith("1  …", lines[1], StringComparison.Ordinal);
        Assert.Contains(@"folder\tail-segment  orphan", lines[1], StringComparison.Ordinal);
    }

    /// <summary>A report with no candidates is one line naming the root.</summary>
    [Fact]
    public void EmptyReportSaysNothingWasFound() =>
        Assert.Equal("No worktrees or orphan folders found under D:\\.\n", ReportTable.Render(ReportOf(), Now));

    /// <summary>A branch longer than 28 characters keeps its first 27 behind a <c>…</c>.</summary>
    [Fact]
    public void LongBranchIsTruncatedAtTwentyEightCharacters()
    {
        string branch = "feature/a-very-long-branch-name";

        string row = ReportTable.Render(ReportOf(Registered(@"yaat.wt\b", branch, new WorktreeSignals())), Now).Split('\n')[1];

        Assert.EndsWith("  " + branch[..27] + "…", row, StringComparison.Ordinal);
    }

    /// <summary>Sizes use binary units with one decimal, rounded half up, from bytes to petabytes.</summary>
    /// <param name="bytes">The byte count.</param>
    /// <param name="expected">The text.</param>
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1280, "1.3 KB")]
    [InlineData(1L << 50, "1.0 PB")]
    [InlineData(long.MaxValue, "8192.0 PB")]
    public void HumanBytesUsesBinaryUnitsRoundedHalfUp(long bytes, string expected) => Assert.Equal(expected, ReportTable.HumanBytes(bytes));

    /// <summary>Ages switch unit at 1 hour, 2 days, 14 days and 365 days; a time in the future is <c>0m</c>.</summary>
    /// <param name="elapsed">Seconds between the time and now.</param>
    /// <param name="expected">The age.</param>
    [Theory]
    [InlineData(-100, "0m")]
    [InlineData(3599, "59m")]
    [InlineData(3600, "1h")]
    [InlineData((2 * Day) - 1, "47h")]
    [InlineData(2 * Day, "2d")]
    [InlineData((14 * Day) - 1, "13d")]
    [InlineData(14 * Day, "2w")]
    [InlineData((365 * Day) - 1, "52w")]
    [InlineData(365 * Day, "1y")]
    public void AgeUsesTheLargestFittingUnit(long elapsed, string expected) => Assert.Equal(expected, ReportTable.Age(Now - elapsed, Now));

    /// <summary>A gap wider than a <see cref="long"/> saturates to years rather than wrapping to a future time.</summary>
    [Fact]
    public void AgeSaturatesAnOverflowingGap() => Assert.Equal("292471208677y", ReportTable.Age(long.MinValue, Now));

    /// <summary>
    /// A path under the root shows relative to it, compared with case folded and either separator; the root itself, a path elsewhere
    /// and a sibling sharing the root's name as a prefix show whole.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="root">The root.</param>
    /// <param name="expected">What the table shows.</param>
    [Theory]
    [InlineData(@"D:\", @"D:\", @"D:\")]
    [InlineData(@"D:\yaat", @"D:\yaat\", @"D:\yaat")]
    [InlineData(@"d:\YAAT.wt\Feat", @"D:\yaat.WT", "Feat")]
    [InlineData("D:/yaat.wt/feat/", @"D:\", "yaat.wt/feat")]
    [InlineData(@"E:\yaat.wt\feat", @"D:\", @"E:\yaat.wt\feat")]
    [InlineData(@"D:\yaat-server\feat", @"D:\yaat", @"D:\yaat-server\feat")]
    public void RelativePathIsRelativeOnlyUnderTheRoot(string path, string root, string expected) =>
        Assert.Equal(expected, ReportTable.RelativePath(path, root));
}
