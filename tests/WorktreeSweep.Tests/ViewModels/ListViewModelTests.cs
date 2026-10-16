using System.ComponentModel;
using WorktreeSweep.Discovery;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;
using WorktreeSweep.ViewModels;
using static WorktreeSweep.Tests.ViewModels.ViewModelSamples;

namespace WorktreeSweep.Tests.ViewModels;

/// <summary>The List screen: its rows, sorting, filters, ticks, header, detail pane and the way into Review.</summary>
public sealed class ListViewModelTests
{
    /// <summary>A released worktree starts ticked; any other row starts unticked.</summary>
    [Fact]
    public void ReleasedWorktreesAreTickedByDefault()
    {
        MainViewModel main = Listed(Worktree("plain", MergeState.Ancestor, 10), ReportSamples.Released("yaat", @"yaat.wt\done"));

        IReadOnlyList<CandidateRowViewModel> rows = ListOf(main).Rows;

        Assert.Equal(2, rows.Count);
        Assert.Equal(@"yaat.wt\done", rows[0].Path);
        Assert.True(rows[0].IsTicked);
        Assert.Equal(@"repo.wt\plain", rows[1].Path);
        Assert.False(rows[1].IsTicked);
    }

    /// <summary>The header counts the rows, the shown ones and the ticked ones and adds up the ticked sizes, following each change.</summary>
    [Fact]
    public void HeaderCountsShownTickedAndTheirSize()
    {
        ListViewModel list = ListFor(Worktree("a", MergeState.Ancestor, 1536), Worktree("b", MergeState.Ancestor, 10));
        Assert.Equal("2 candidates · 2 shown · 0 ticked · 0 B selected for removal", list.Header);

        list.Rows[0].IsTicked = true;

        Assert.Equal("2 candidates · 2 shown · 1 ticked · 1.5 KB selected for removal", list.Header);
        list.TickAllCommand.Execute(null);
        Assert.Equal("2 candidates · 2 shown · 2 ticked · 1.5 KB selected for removal", list.Header);
        list.Search = "repo.wt\\b";
        Assert.Equal("2 candidates · 1 shown · 2 ticked · 1.5 KB selected for removal", list.Header);
        list.UntickAllCommand.Execute(null);
        Assert.Equal("2 candidates · 1 shown · 0 ticked · 0 B selected for removal", list.Header);
    }

    /// <summary>A ticked size the scan read only partly makes the header's size a lower bound.</summary>
    [Fact]
    public void HeaderSaysAtLeastWhenATickedSizeIsPartial()
    {
        OrphanCandidate partial = ReportSamples.Orphan(@"repo.wt\stray", OrphanKind.Folder, new SizeInfo { Bytes = 10, Unreadable = 1 });
        ListViewModel list = ListFor(partial);

        list.TickAllCommand.Execute(null);

        Assert.Equal("1 candidates · 1 shown · 1 ticked · at least 10 B selected for removal", list.Header);
    }

    /// <summary>The Review button names how many picks it reviews.</summary>
    [Fact]
    public void ReviewLabelCountsThePicks()
    {
        ListViewModel list = ListFor(Worktree("a", MergeState.Ancestor, 10), Worktree("b", MergeState.Ancestor, 10));
        Assert.Equal("Review", list.ReviewLabel);

        list.Rows[0].IsTicked = true;

        Assert.Equal("Review 1 pick…", list.ReviewLabel);
        list.Rows[1].IsTicked = true;
        Assert.Equal("Review 2 picks…", list.ReviewLabel);
    }

    /// <summary>Review with no row ticked stays on the List and says what to do.</summary>
    [Fact]
    public void ReviewWithNothingTickedSaysNothingPicked()
    {
        MainViewModel main = Listed(Worktree("a", MergeState.Ancestor, 10));
        ListViewModel list = ListOf(main);

        list.ReviewCommand.Execute(null);

        Assert.Equal(Screen.List, main.Screen);
        Assert.Equal("Nothing picked; tick a row first.", list.Message);
        Assert.Null(main.Review);
    }

    /// <summary>Changing a tick, or Untick all, clears the Nothing picked message.</summary>
    [Fact]
    public void TickingARowClearsTheNothingPickedMessage()
    {
        ListViewModel list = ListFor(Worktree("a", MergeState.Ancestor, 10));
        list.ReviewCommand.Execute(null);
        Assert.Equal(ListViewModel.NothingPicked, list.Message);

        list.Rows[0].IsTicked = true;

        Assert.Null(list.Message);
        list.Rows[0].IsTicked = false;
        list.ReviewCommand.Execute(null);
        Assert.Equal(ListViewModel.NothingPicked, list.Message);
        list.UntickAllCommand.Execute(null);
        Assert.Null(list.Message);
    }

    /// <summary>Tick all and Untick all each announce one header change, however many rows they tick.</summary>
    [Fact]
    public void TickAllRaisesHeaderOnce()
    {
        ListViewModel list = ListFor(
            Worktree("a", MergeState.Ancestor, 10),
            Worktree("b", MergeState.Ancestor, 10),
            Worktree("c", MergeState.Ancestor, 10)
        );
        int headerChanges = 0;
        list.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ListViewModel.Header))
            {
                headerChanges++;
            }
        };

        list.TickAllCommand.Execute(null);

        Assert.Equal(1, headerChanges);
        list.UntickAllCommand.Execute(null);
        Assert.Equal(2, headerChanges);
    }

    /// <summary>The selected row's detail is its labelled fields, dated with the injected offset.</summary>
    [Fact]
    public void SelectedRowShowsItsDetailFields()
    {
        RegisteredCandidate candidate = Worktree("a", MergeState.Ancestor, 10);
        ListViewModel list = ListFor(candidate);
        Assert.Empty(list.Detail);
        Assert.False(list.SelectedLosesNothing);

        list.Selected = list.Rows[0];

        Assert.Equal(DetailText.Fields(candidate, ReportSamples.Now, _ => TimeSpan.Zero), list.Detail);
        Assert.True(list.SelectedLosesNothing);
    }

    /// <summary>Sorting a column cycles ascending, descending and off, off showing the table order again.</summary>
    [Fact]
    public void SortCyclesAscendingDescendingOff()
    {
        ListViewModel list = ListFor(
            ReportSamples.Released("yaat", @"yaat.wt\zz"),
            Worktree("a", MergeState.Ancestor, 10),
            Worktree("b", MergeState.Ancestor, 10)
        );
        string[] table = [@"yaat.wt\zz", @"repo.wt\a", @"repo.wt\b"];
        Assert.Equal(table, ShownPaths(list));
        Assert.Null(list.SortKey);

        list.SortByCommand.Execute(SortKey.Path);

        Assert.Equal(SortKey.Path, list.SortKey);
        Assert.Equal(ListSortDirection.Ascending, list.SortDirection);
        string[] ascending = [@"repo.wt\a", @"repo.wt\b", @"yaat.wt\zz"];
        Assert.Equal(ascending, ShownPaths(list));
        list.SortByCommand.Execute(SortKey.Path);
        Assert.Equal(ListSortDirection.Descending, list.SortDirection);
        string[] descending = [@"yaat.wt\zz", @"repo.wt\b", @"repo.wt\a"];
        Assert.Equal(descending, ShownPaths(list));
        list.SortByCommand.Execute(SortKey.Path);
        Assert.Null(list.SortKey);
        Assert.Equal(table, ShownPaths(list));
        Assert.Equal(table, list.Rows.Select(row => row.Path));
    }

    /// <summary>Sorting a different column starts it ascending, whatever the last column's direction was.</summary>
    [Fact]
    public void SortingAnotherColumnStartsAscending()
    {
        ListViewModel list = ListFor(Worktree("a", MergeState.Ancestor, 10), Worktree("b", MergeState.Ancestor, 20));
        list.SortByCommand.Execute(SortKey.Path);
        list.SortByCommand.Execute(SortKey.Path);

        list.SortByCommand.Execute(SortKey.Size);

        Assert.Equal(SortKey.Size, list.SortKey);
        Assert.Equal(ListSortDirection.Ascending, list.SortDirection);
        string[] ascending = [@"repo.wt\a", @"repo.wt\b"];
        Assert.Equal(ascending, ShownPaths(list));
    }

    /// <summary>The Kind column sorts by its text, ignoring case.</summary>
    [Fact]
    public void SortByKindOrdersByKindText()
    {
        ListViewModel list = ListFor(Worktree("w", MergeState.Ancestor, 10), Link("l"), FolderOrphan(@"yaat.wt\o"));

        list.SortByCommand.Execute(SortKey.Kind);

        string[] ascending = [@"repo.wt\l", @"yaat.wt\o", @"repo.wt\w"];
        Assert.Equal(ascending, ShownPaths(list));
        list.SortByCommand.Execute(SortKey.Kind);
        string[] descending = [@"repo.wt\w", @"yaat.wt\o", @"repo.wt\l"];
        Assert.Equal(descending, ShownPaths(list));
    }

    /// <summary>The Branch column sorts by its text, ignoring case.</summary>
    [Fact]
    public void SortByBranchOrdersByTextIgnoringCase()
    {
        ListViewModel list = ListFor(
            OnBranch(Worktree("a", MergeState.Ancestor, 10), "zeta"),
            OnBranch(Worktree("b", MergeState.Ancestor, 10), "Alpha"),
            OnBranch(Worktree("c", MergeState.Ancestor, 10), "beta")
        );

        list.SortByCommand.Execute(SortKey.Branch);

        string[] ascending = [@"repo.wt\b", @"repo.wt\c", @"repo.wt\a"];
        Assert.Equal(ascending, ShownPaths(list));
    }

    /// <summary>The Merge column sorts the riskiest first, then by its text.</summary>
    [Fact]
    public void SortByMergeOrdersByRiskThenText()
    {
        ListViewModel list = ListFor(
            Worktree("a", MergeState.Ancestor, 10),
            Worktree("b", MergeState.Unmerged(2), 10),
            Worktree("c", MergeState.NoCommits, 10),
            Worktree("d", MergeState.PatchesApplied, 10)
        );

        list.SortByCommand.Execute(SortKey.Merge);

        string[] ascending = [@"repo.wt\b", @"repo.wt\c", @"repo.wt\d", @"repo.wt\a"];
        Assert.Equal(ascending, ShownPaths(list));
        list.SortByCommand.Execute(SortKey.Merge);
        string[] descending = [@"repo.wt\a", @"repo.wt\d", @"repo.wt\c", @"repo.wt\b"];
        Assert.Equal(descending, ShownPaths(list));
    }

    /// <summary>The Dirty column sorts the riskiest first, then by its text.</summary>
    [Fact]
    public void SortByDirtyOrdersByRiskThenText()
    {
        ListViewModel list = ListFor(
            Worktree("a", MergeState.Ancestor, 10),
            WithSignals(Worktree("b", MergeState.Ancestor, 10), signals => signals with { Dirty = new Dirty { Modified = 2 } }),
            WithSignals(Worktree("c", MergeState.Ancestor, 10), signals => signals with { Dirty = new Dirty { Untracked = 1 } })
        );

        list.SortByCommand.Execute(SortKey.Dirty);

        string[] ascending = [@"repo.wt\c", @"repo.wt\b", @"repo.wt\a"];
        Assert.Equal(ascending, ShownPaths(list));
    }

    /// <summary>The Upstream column sorts the riskiest first, then by its text.</summary>
    [Fact]
    public void SortByUpstreamOrdersByRiskThenText()
    {
        ListViewModel list = ListFor(
            Worktree("a", MergeState.Ancestor, 10),
            WithSignals(Worktree("b", MergeState.Ancestor, 10), signals => signals with { Upstream = Upstream.Tracking(2) }),
            WithSignals(Worktree("c", MergeState.Ancestor, 10), signals => signals with { Upstream = Upstream.Tracking(1) })
        );

        list.SortByCommand.Execute(SortKey.Upstream);

        string[] ascending = [@"repo.wt\c", @"repo.wt\b", @"repo.wt\a"];
        Assert.Equal(ascending, ShownPaths(list));
    }

    /// <summary>The Active column sorts by last activity, the most recent first, an unknown time last in either direction.</summary>
    [Fact]
    public void SortByActiveOrdersByTimeWithUnknownLast()
    {
        ListViewModel list = ListFor(
            ActiveAt(Worktree("a", MergeState.Ancestor, 10), ReportSamples.Now - ReportSamples.Day),
            Worktree("b", MergeState.Ancestor, 10),
            ActiveAt(Worktree("c", MergeState.Ancestor, 10), ReportSamples.Now - (3 * ReportSamples.Day))
        );

        list.SortByCommand.Execute(SortKey.Active);

        string[] ascending = [@"repo.wt\a", @"repo.wt\c", @"repo.wt\b"];
        Assert.Equal(ascending, ShownPaths(list));
        list.SortByCommand.Execute(SortKey.Active);
        string[] descending = [@"repo.wt\c", @"repo.wt\a", @"repo.wt\b"];
        Assert.Equal(descending, ShownPaths(list));
    }

    /// <summary>The Size column sorts by bytes, a link's unknown size last in either direction.</summary>
    [Fact]
    public void SortBySizeOrdersByBytesWithUnknownLast()
    {
        ListViewModel list = ListFor(
            Worktree("a", MergeState.Ancestor, 20),
            Worktree("b", MergeState.Ancestor, 10),
            Worktree("c", MergeState.Ancestor, 30),
            Link("l")
        );

        list.SortByCommand.Execute(SortKey.Size);

        string[] ascending = [@"repo.wt\b", @"repo.wt\a", @"repo.wt\c", @"repo.wt\l"];
        Assert.Equal(ascending, ShownPaths(list));
        list.SortByCommand.Execute(SortKey.Size);
        string[] descending = [@"repo.wt\c", @"repo.wt\a", @"repo.wt\b", @"repo.wt\l"];
        Assert.Equal(descending, ShownPaths(list));
    }

    /// <summary>The Flags column sorts by its text, ignoring case.</summary>
    [Fact]
    public void SortByFlagsOrdersByText()
    {
        RegisteredCandidate locked = Worktree("a", MergeState.Ancestor, 10);
        ListViewModel list = ListFor(
            ReportSamples.Released("repo", @"repo.wt\r"),
            locked with
            {
                Record = locked.Record with { Locked = "" },
            },
            Worktree("b", MergeState.Ancestor, 10)
        );

        list.SortByCommand.Execute(SortKey.Flags);

        string[] ascending = [@"repo.wt\b", @"repo.wt\a", @"repo.wt\r"];
        Assert.Equal(ascending, ShownPaths(list));
    }

    /// <summary>Rows that tie on the sort key keep their table order, ascending and descending.</summary>
    [Fact]
    public void SortTiesKeepTableOrder()
    {
        ListViewModel list = ListFor(
            Worktree("a", MergeState.Ancestor, 10),
            Worktree("b", MergeState.Ancestor, 10),
            Worktree("c", MergeState.Ancestor, 10)
        );
        string[] table = [@"repo.wt\a", @"repo.wt\b", @"repo.wt\c"];

        list.SortByCommand.Execute(SortKey.Merge);

        Assert.Equal(table, ShownPaths(list));
        list.SortByCommand.Execute(SortKey.Merge);
        Assert.Equal(table, ShownPaths(list));
    }

    /// <summary>The kind filter shows only the rows of that kind.</summary>
    [Fact]
    public void KindFilterShowsOnlyThatKind()
    {
        ListViewModel list = ListFor(Worktree("w", MergeState.Ancestor, 10), Link("l"), FolderOrphan(@"yaat.wt\o"));

        list.KindFilter = KindFilter.Orphan;

        Assert.Equal([@"yaat.wt\o"], ShownPaths(list));
        list.KindFilter = KindFilter.Link;
        Assert.Equal([@"repo.wt\l"], ShownPaths(list));
        list.KindFilter = KindFilter.Worktree;
        Assert.Equal([@"repo.wt\w"], ShownPaths(list));
        list.KindFilter = KindFilter.All;
        Assert.Equal(3, list.Shown.Count);
    }

    /// <summary>Each filter button's count is over every row, whatever the filters show.</summary>
    [Fact]
    public void FilterCountsAreOverEveryRow()
    {
        ListViewModel list = ListFor(
            Worktree("w", MergeState.Ancestor, 10),
            Worktree("u", MergeState.Unmerged(1), 10),
            Link("l"),
            RegisteredElsewhere(FolderOrphan(@"yaat.wt\o"))
        );

        list.KindFilter = KindFilter.Link;
        list.Search = "nothing matches this";

        Assert.Equal(4, list.AllCount);
        Assert.Equal(2, list.WorktreeCount);
        Assert.Equal(1, list.OrphanCount);
        Assert.Equal(1, list.LinkCount);
        Assert.Equal(1, list.NothingLostCount);
    }

    /// <summary>Nothing lost shows only the rows whose removal loses nothing.</summary>
    [Fact]
    public void NothingLostShowsOnlyRowsThatLoseNothing()
    {
        ListViewModel list = ListFor(Worktree("risky", MergeState.Unmerged(2), 10), Worktree("safe", MergeState.Ancestor, 10));

        list.NothingLost = true;

        Assert.Equal([@"repo.wt\safe"], ShownPaths(list));
    }

    /// <summary>The search matches the path or the branch, ignoring case.</summary>
    [Fact]
    public void SearchMatchesPathOrBranchIgnoringCase()
    {
        ListViewModel list = ListFor(
            OnBranch(Worktree("alpha", MergeState.Ancestor, 10), "Feature/X"),
            OnBranch(Worktree("beta", MergeState.Ancestor, 10), "fix/y")
        );

        list.Search = "ALPHA";

        Assert.Equal([@"repo.wt\alpha"], ShownPaths(list));
        list.Search = "feature";
        Assert.Equal([@"repo.wt\alpha"], ShownPaths(list));
        list.Search = "FIX/";
        Assert.Equal([@"repo.wt\beta"], ShownPaths(list));
        list.Search = "repo.wt";
        Assert.Equal(2, list.Shown.Count);
    }

    /// <summary>The kind filter, Nothing lost and the search combine: a row is shown only when it passes all three.</summary>
    [Fact]
    public void FiltersCombine()
    {
        ListViewModel list = ListFor(
            Worktree("safe-a", MergeState.Ancestor, 10),
            Worktree("risky-a", MergeState.Unmerged(1), 10),
            Worktree("safe-b", MergeState.Ancestor, 10),
            Link("link-a")
        );

        list.KindFilter = KindFilter.Worktree;
        list.NothingLost = true;
        list.Search = "-a";

        Assert.Equal([@"repo.wt\safe-a"], ShownPaths(list));
    }

    /// <summary>A filter that hides every row shows the empty state; clearing it brings the rows back.</summary>
    [Fact]
    public void NoMatchWhenTheFilterHidesEveryRow()
    {
        ListViewModel list = ListFor(Worktree("a", MergeState.Ancestor, 10));
        Assert.False(list.NoMatch);

        list.Search = "zzz";

        Assert.True(list.NoMatch);
        Assert.Empty(list.Shown);
    }

    /// <summary>Clear the filter resets the kind, Nothing lost and the search, and every row is shown again.</summary>
    [Fact]
    public void ClearFiltersResetsEveryFilter()
    {
        ListViewModel list = ListFor(Worktree("a", MergeState.Ancestor, 10), Link("l"));
        list.KindFilter = KindFilter.Link;
        list.NothingLost = true;
        list.Search = "zzz";

        list.ClearFiltersCommand.Execute(null);

        Assert.Equal(KindFilter.All, list.KindFilter);
        Assert.False(list.NothingLost);
        Assert.Equal("", list.Search);
        Assert.Equal(2, list.Shown.Count);
        Assert.False(list.NoMatch);
    }

    /// <summary>Tick shown ticks the shown rows and leaves the hidden ones as they were.</summary>
    [Fact]
    public void TickShownTicksOnlyShownRows()
    {
        ListViewModel list = ListFor(Worktree("a", MergeState.Ancestor, 10), Worktree("b", MergeState.Ancestor, 10), Link("l"));
        list.KindFilter = KindFilter.Worktree;

        list.TickShownCommand.Execute(null);

        bool[] ticks = [.. list.Rows.Select(row => row.IsTicked)];
        Assert.Equal([true, true, false], ticks);
    }

    /// <summary>Untick all unticks every row, the hidden ones too.</summary>
    [Fact]
    public void UntickAllUnticksHiddenRowsToo()
    {
        ListViewModel list = ListFor(Worktree("a", MergeState.Ancestor, 10), Link("l"));
        list.TickAllCommand.Execute(null);
        list.KindFilter = KindFilter.Link;

        list.UntickAllCommand.Execute(null);

        Assert.All(list.Rows, row => Assert.False(row.IsTicked));
    }

    /// <summary>The header checkbox is ticked when every shown row is, and pressing it ticks or unticks only the shown rows.</summary>
    [Fact]
    public void HeaderCheckboxTicksAndUnticksShownRows()
    {
        ListViewModel list = ListFor(Worktree("a", MergeState.Ancestor, 10), Worktree("b", MergeState.Ancestor, 10), Link("l"));
        list.Rows[2].IsTicked = true;
        list.KindFilter = KindFilter.Worktree;
        Assert.False(list.AllShownTicked);

        list.AllShownTicked = true;

        Assert.True(list.AllShownTicked);
        bool[] ticked = [.. list.Rows.Select(row => row.IsTicked)];
        Assert.Equal([true, true, true], ticked);
        list.AllShownTicked = false;
        bool[] unticked = [.. list.Rows.Select(row => row.IsTicked)];
        Assert.Equal([false, false, true], unticked);
        list.Search = "zzz";
        Assert.False(list.AllShownTicked);
    }

    /// <summary>The ticked rows the filter hides are counted and said, and the sentence goes once none is hidden.</summary>
    [Fact]
    public void HiddenTickedCountsTickedRowsTheFilterHides()
    {
        ListViewModel list = ListFor(Worktree("a", MergeState.Ancestor, 10), Worktree("b", MergeState.Ancestor, 10), Link("l"));
        list.TickAllCommand.Execute(null);
        Assert.Equal(0, list.HiddenTicked);
        Assert.Null(list.HiddenTickedText);

        list.KindFilter = KindFilter.Link;

        Assert.Equal(2, list.HiddenTicked);
        Assert.Equal("2 ticked rows are hidden by the filter.", list.HiddenTickedText);
        list.KindFilter = KindFilter.Worktree;
        Assert.Equal(1, list.HiddenTicked);
        Assert.Equal("1 ticked row is hidden by the filter.", list.HiddenTickedText);
        list.Rows[2].IsTicked = false;
        Assert.Equal(0, list.HiddenTicked);
        Assert.Null(list.HiddenTickedText);
    }

    /// <summary>Review sends every ticked row's table-order index, a row the filter hides and a sorted list included.</summary>
    [Fact]
    public void ReviewSendsHiddenTickedRowsInTableOrder()
    {
        IReadOnlyList<int>? sent = null;
        var list = new ListViewModel(
            ReportSamples.ReportOf(Worktree("a", MergeState.Ancestor, 10), Worktree("b", MergeState.Ancestor, 10), Link("l")),
            ReportSamples.Now,
            _ => TimeSpan.Zero,
            picked => sent = picked
        );
        list.Rows[0].IsTicked = true;
        list.Rows[2].IsTicked = true;
        list.SortByCommand.Execute(SortKey.Path);
        list.SortByCommand.Execute(SortKey.Path);
        list.KindFilter = KindFilter.Link;

        list.ReviewCommand.Execute(null);

        Assert.Equal([0, 2], sent);
    }

    /// <summary>A filter that hides the selected row clears the selection; one that keeps it, or a sort, does not.</summary>
    [Fact]
    public void FilterThatHidesTheSelectedRowClearsTheSelection()
    {
        ListViewModel list = ListFor(Worktree("a", MergeState.Ancestor, 10), Link("l"));
        list.Selected = list.Rows[0];

        list.KindFilter = KindFilter.Worktree;
        list.SortByCommand.Execute(SortKey.Path);

        Assert.Same(list.Rows[0], list.Selected);
        list.KindFilter = KindFilter.Link;
        Assert.Null(list.Selected);
        Assert.Empty(list.Detail);
    }

    /// <summary>The scan's discovery errors become the notice above the grid, each relative to the root and on one line.</summary>
    [Fact]
    public void DiscoveryErrorsShowAsANotice()
    {
        ScanReport report = ReportSamples.ReportOf(Worktree("a", MergeState.Ancestor, 10)) with
        {
            DiscoveryErrors = [new DiscoveryError(ReportSamples.Under("gate"), ReportSamples.Under("gate"), "git worktree list failed\nexit 128")],
        };

        var list = new ListViewModel(report, ReportSamples.Now, _ => TimeSpan.Zero, _ => { });

        Assert.Equal("1 discovery error.", list.DiscoveryErrorsTitle);
        Assert.Equal(["gate: git worktree list failed; exit 128"], list.DiscoveryErrors);
        Assert.Null(ListFor(Worktree("a", MergeState.Ancestor, 10)).DiscoveryErrorsTitle);
    }

    /// <summary>The List of a window whose scan found <paramref name="candidates"/>.</summary>
    /// <param name="candidates">The candidates.</param>
    /// <returns>The list.</returns>
    private static ListViewModel ListFor(params Candidate[] candidates) => ListOf(Listed(candidates));

    /// <summary>The shown rows' paths, in shown order.</summary>
    /// <param name="list">The list.</param>
    /// <returns>The paths.</returns>
    private static string[] ShownPaths(ListViewModel list) => [.. list.Shown.Select(row => row.Path)];

    /// <summary><paramref name="candidate"/> on <paramref name="branch"/>.</summary>
    /// <param name="candidate">The worktree.</param>
    /// <param name="branch">The branch.</param>
    /// <returns>The worktree on the branch.</returns>
    private static RegisteredCandidate OnBranch(RegisteredCandidate candidate, string branch) =>
        candidate with
        {
            Record = candidate.Record with { Branch = branch },
        };

    /// <summary><paramref name="candidate"/> with its signals changed by <paramref name="change"/>.</summary>
    /// <param name="candidate">The worktree.</param>
    /// <param name="change">The change.</param>
    /// <returns>The changed worktree.</returns>
    private static RegisteredCandidate WithSignals(RegisteredCandidate candidate, Func<WorktreeSignals, WorktreeSignals> change) =>
        candidate with
        {
            Signals = change(candidate.Signals),
        };

    /// <summary><paramref name="candidate"/> last active at <paramref name="unix"/>.</summary>
    /// <param name="candidate">The worktree.</param>
    /// <param name="unix">The last activity, in Unix seconds.</param>
    /// <returns>The changed worktree.</returns>
    private static RegisteredCandidate ActiveAt(RegisteredCandidate candidate, long unix) =>
        WithSignals(candidate, signals => signals with { LastActivityUnix = unix });

    /// <summary>A folder orphan of 5 bytes, written at <see cref="ReportSamples.Now"/>.</summary>
    /// <param name="path">Its path relative to the root.</param>
    /// <returns>The orphan.</returns>
    private static OrphanCandidate FolderOrphan(string path) =>
        ReportSamples.Orphan(path, OrphanKind.Folder, ReportSamples.Size(5, ReportSamples.Now));

    /// <summary><paramref name="candidate"/> still registered in <c>D:\repo</c>, so removing it loses a registration.</summary>
    /// <param name="candidate">The orphan.</param>
    /// <returns>The changed orphan.</returns>
    private static OrphanCandidate RegisteredElsewhere(OrphanCandidate candidate) =>
        candidate with
        {
            Orphan = candidate.Orphan with { LiveGitdir = ReportSamples.Under(@"repo\.git\worktrees\o") },
        };
}
