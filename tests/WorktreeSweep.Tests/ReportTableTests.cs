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

    /// <summary>A discovery error is one trailing line after the table, a <c>\r\n</c> in its message turned into <c>; </c>.</summary>
    [Fact]
    public void RenderTableAppendsDiscoveryErrors()
    {
        ScanReport report = ReportOf(Orphan(@"yaat.wt\stray", OrphanKind.Folder, Size(0, Now))) with
        {
            DiscoveryErrors =
            [
                new DiscoveryError(Under("yaat"), Under(@"yaat\.git\worktrees\feat\gitdir"), "git worktree list failed\r\nbad config"),
            ],
        };

        Assert.EndsWith(
            "discovery error: yaat\\.git\\worktrees\\feat\\gitdir: git worktree list failed; bad config\n",
            ReportTable.Render(report, Now),
            StringComparison.Ordinal
        );
    }

    /// <summary>A discovery error follows the no-candidates message, a <c>\n</c> in its message turned into <c>; </c>.</summary>
    [Fact]
    public void RenderEmptyReportAppendsDiscoveryErrors()
    {
        ScanReport report = ReportOf() with { DiscoveryErrors = [new DiscoveryError(Under("yaat"), Under("yaat"), "line one\nline two")] };

        Assert.Equal(
            "No worktrees or orphan folders found under D:\\.\ndiscovery error: yaat: line one; line two\n",
            ReportTable.Render(report, Now)
        );
    }

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

    /// <summary>The root itself shows whole, whether or not the root is written with a trailing separator.</summary>
    [Fact]
    public void RelativePathShowsTheRootWhole()
    {
        using var fx = new Fixture();
        string root = fx.PathTo("root");
        Directory.CreateDirectory(root);

        Assert.Equal(root, ReportTable.RelativePath(root, root));
        Assert.Equal(root, ReportTable.RelativePath(root, root + "\\"));
    }

    /// <summary>
    /// A path under the root shows relative to it, the two compared resolved and case folded, either separator folded to <c>\</c>
    /// and a trailing one dropped.
    /// </summary>
    [Fact]
    public void RelativePathShowsAPathUnderTheRootRelative()
    {
        using var fx = new Fixture();
        string root = fx.PathTo("root");
        Directory.CreateDirectory(root);

        Assert.Equal(@"a.wt\feat", ReportTable.RelativePath(Path.Join(root, "a.wt", "feat"), root));
        Assert.Equal(@"a.wt\feat", ReportTable.RelativePath(root.ToUpperInvariant() + @"\a.wt\feat", root));
        Assert.Equal(@"a.wt\feat", ReportTable.RelativePath(root + "/a.wt/feat/", root));
    }

    /// <summary>A path elsewhere than the root shows whole, in the resolved-parent spelling.</summary>
    [Fact]
    public void RelativePathShowsAPathElsewhereWhole()
    {
        using var fx = new Fixture();
        string root = fx.PathTo("root");
        Directory.CreateDirectory(root);
        string elsewhere = fx.PathTo("elsewhere/feat");

        Assert.Equal(elsewhere, ReportTable.RelativePath(elsewhere, root));
    }

    /// <summary>A sibling sharing the root's name as a prefix shows whole rather than relative.</summary>
    [Fact]
    public void RelativePathKeepsASiblingWithTheRootsNameAsPrefixWhole()
    {
        using var fx = new Fixture();
        string root = fx.PathTo("yaat");
        Directory.CreateDirectory(root);
        string sibling = fx.PathTo("yaat-server/feat");

        Assert.Equal(sibling, ReportTable.RelativePath(sibling, root));
    }

    /// <summary>A path spelled as git spells it shows relative under a root given with a <c>\\?\</c> prefix.</summary>
    [Fact]
    public void RelativePathResolvesAVerbatimPrefixedRoot()
    {
        using var fx = new Fixture();
        string path = fx.PathTo("repo.wt/feat");
        Directory.CreateDirectory(path);

        Assert.Equal(@"repo.wt\feat", ReportTable.RelativePath(path, @"\\?\" + fx.Root));
    }

    /// <summary>A junction under the root shows as its own relative path, never its target's.</summary>
    [Fact]
    public void RelativePathKeepsAJunctionLeafAsItsOwnName()
    {
        using var fx = new Fixture();
        string moved = fx.PathTo("moved");
        Directory.CreateDirectory(moved);
        string link = fx.PathTo("repo.wt/feat");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Assert.SkipUnless(Fixture.MakeJunction(link, moved), "mklink /J is unavailable");

        Assert.Equal(@"repo.wt\feat", ReportTable.RelativePath(link, fx.Root));
    }

    /// <summary>
    /// <see cref="ReportTable.Rows"/> follows <see cref="ScanReport.Ordered"/>, and each row's PATH, KIND, MERGE and SIZE cells
    /// are the cells the table prints for that row.
    /// </summary>
    [Fact]
    public void RowsFollowTheOrderedCandidatesAndMatchTheTable()
    {
        var registeredSignals = new WorktreeSignals
        {
            MergeState = MergeState.Unmerged(2),
            MergeStateAgainst = "main",
            Dirty = new Dirty { Modified = 3, Untracked = 2 },
            Upstream = Upstream.Tracking(1),
            LastActivityUnix = Now - (3 * Day),
            Size = Size(1536, Now),
        };
        var releasedSignals = new WorktreeSignals
        {
            MergeState = MergeState.Ancestor,
            MergeStateAgainst = "main",
            Upstream = Upstream.Gone,
            LastActivityUnix = Now - (5 * Day),
            Size = Size(1536, Now),
        };
        RegisteredCandidate held = Released("yaat", @"yaat.wt\held") with { Signals = releasedSignals };
        ScanReport report = ReportOf(
            Orphan(@"yaat.wt\stray", OrphanKind.Folder, Size(10, Now)),
            Registered(@"yaat.wt\plain", "plain", registeredSignals),
            held
        );

        IReadOnlyList<ReportRow> rows = ReportTable.Rows(report, Now);

        Assert.Equal(report.Ordered(), rows.Select(row => row.Candidate));
        (string Path, string Kind, string Merge, string Size)[] expected =
        [
            (@"yaat.wt\held", "worktree", "merged", "1.5 KB"),
            (@"yaat.wt\plain", "worktree", "unmerged 2", "1.5 KB"),
            (@"yaat.wt\stray", "orphan", "", "10 B"),
        ];
        Assert.Equal(expected.Length, rows.Count);
        string[] lines = ReportTable.Render(report, Now).TrimEnd('\n').Split('\n');
        for (int index = 0; index < rows.Count; index++)
        {
            ReportRow row = rows[index];
            (string Path, string Kind, string Merge, string Size) cells = expected[index];
            Assert.Equal(cells.Path, row.Path);
            Assert.Equal(cells.Kind, row.Kind);
            Assert.Equal(cells.Merge, row.Merge);
            Assert.Equal(cells.Size, row.Size);
            Assert.Contains(cells.Path, lines[index + 1], StringComparison.Ordinal);
            Assert.Contains(cells.Kind, lines[index + 1], StringComparison.Ordinal);
            Assert.Contains(cells.Size, lines[index + 1], StringComparison.Ordinal);
        }
    }

    /// <summary>The MERGE cell's risk follows the merge state.</summary>
    /// <param name="kind">The merge state to build.</param>
    /// <param name="expected">The risk it carries.</param>
    [Theory]
    [InlineData("unmerged", CellRisk.Danger)]
    [InlineData("detached-not-contained", CellRisk.Danger)]
    [InlineData("no-commits", CellRisk.Caution)]
    [InlineData("ancestor", CellRisk.Good)]
    [InlineData("patches-applied", CellRisk.Good)]
    [InlineData("content-contained", CellRisk.Good)]
    [InlineData("detached-contained", CellRisk.None)]
    [InlineData("none", CellRisk.None)]
    public void MergeRiskFollowsTheMergeState(string kind, CellRisk expected) =>
        Assert.Equal(expected, Row(new WorktreeSignals { MergeState = MergeStateOf(kind) }).MergeRisk);

    /// <summary>An orphan's MERGE cell carries no risk.</summary>
    [Fact]
    public void OrphanMergeRiskIsNone()
    {
        OrphanCandidate orphan = Orphan(@"yaat.wt\stray", OrphanKind.Folder, Size(0, Now));

        Assert.Equal(CellRisk.None, Assert.Single(ReportTable.Rows(ReportOf(orphan), Now)).MergeRisk);
    }

    /// <summary>A cell is a caution only when it says something: uncommitted work, or commits not pushed.</summary>
    [Fact]
    public void DirtyAndUpstreamRiskAreCautionOnlyWhenSet()
    {
        ReportRow clean = Row(new WorktreeSignals { Dirty = new Dirty(), Upstream = Upstream.Tracking(0) });
        Assert.Equal(CellRisk.None, clean.DirtyRisk);
        Assert.Equal(CellRisk.None, clean.UpstreamRisk);

        ReportRow dirty = Row(
            new WorktreeSignals
            {
                Dirty = new Dirty { Modified = 1 },
                Upstream = Upstream.Tracking(3),
            }
        );
        Assert.Equal(CellRisk.Caution, dirty.DirtyRisk);
        Assert.Equal(CellRisk.Caution, dirty.UpstreamRisk);

        ReportRow gone = Row(new WorktreeSignals { Upstream = Upstream.Gone });
        Assert.Equal(CellRisk.None, gone.DirtyRisk);
        Assert.Equal(CellRisk.None, gone.UpstreamRisk);
    }

    /// <summary>The single row of a report holding one registered worktree with <paramref name="signals"/>.</summary>
    /// <param name="signals">Its signals.</param>
    /// <returns>The row.</returns>
    private static ReportRow Row(WorktreeSignals signals) =>
        Assert.Single(ReportTable.Rows(ReportOf(Registered(@"yaat.wt\one", "one", signals)), Now));

    /// <summary>The merge state named by <paramref name="kind"/>.</summary>
    /// <param name="kind">The case name.</param>
    /// <returns>The state; <see langword="null"/> for <c>none</c>.</returns>
    private static MergeState? MergeStateOf(string kind) =>
        kind switch
        {
            "unmerged" => MergeState.Unmerged(3),
            "detached-not-contained" => MergeState.Detached(false),
            "no-commits" => MergeState.NoCommits,
            "ancestor" => MergeState.Ancestor,
            "patches-applied" => MergeState.PatchesApplied,
            "content-contained" => MergeState.ContentContained,
            "detached-contained" => MergeState.Detached(true),
            "none" => null,
            _ => throw new ArgumentException($"unknown merge state {kind}", nameof(kind)),
        };
}
