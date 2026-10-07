using WorktreeSweep.Discovery;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;
using WorktreeSweep.ViewModels;

namespace WorktreeSweep.Tests.ViewModels;

/// <summary>The List screen: its rows, ticks, header, detail pane and the way into Review.</summary>
public sealed class ListViewModelTests
{
    /// <summary>A released worktree starts ticked; any other row starts unticked.</summary>
    [Fact]
    public void ReleasedWorktreesAreTickedByDefault()
    {
        MainViewModel main = ViewModelSamples.Listed(
            ViewModelSamples.Worktree("plain", MergeState.Ancestor, 10),
            ReportSamples.Released("yaat", @"yaat.wt\done")
        );

        IReadOnlyList<CandidateRowViewModel> rows = ViewModelSamples.ListOf(main).Rows;

        Assert.Equal(2, rows.Count);
        Assert.Equal(@"yaat.wt\done", rows[0].Path);
        Assert.True(rows[0].IsTicked);
        Assert.Equal(@"repo.wt\plain", rows[1].Path);
        Assert.False(rows[1].IsTicked);
    }

    /// <summary>The header counts the rows and the ticked ones and adds up the ticked sizes, following each tick.</summary>
    [Fact]
    public void HeaderCountsTickedAndTheirSize()
    {
        MainViewModel main = ViewModelSamples.Listed(
            ViewModelSamples.Worktree("a", MergeState.Ancestor, 1536),
            ViewModelSamples.Worktree("b", MergeState.Ancestor, 10)
        );
        ListViewModel list = ViewModelSamples.ListOf(main);
        Assert.Equal("2 candidates, 0 ticked, 0 B selected for removal", list.Header);

        list.Rows[0].IsTicked = true;

        Assert.Equal("2 candidates, 1 ticked, 1.5 KB selected for removal", list.Header);
        list.TickAllCommand.Execute(null);
        Assert.Equal("2 candidates, 2 ticked, 1.5 KB selected for removal", list.Header);
        list.TickNoneCommand.Execute(null);
        Assert.Equal("2 candidates, 0 ticked, 0 B selected for removal", list.Header);
    }

    /// <summary>A ticked size the scan read only partly makes the header's size a lower bound.</summary>
    [Fact]
    public void HeaderSaysAtLeastWhenATickedSizeIsPartial()
    {
        OrphanCandidate partial = ReportSamples.Orphan(@"repo.wt\stray", OrphanKind.Folder, new SizeInfo { Bytes = 10, Unreadable = 1 });
        MainViewModel main = ViewModelSamples.Listed(partial);
        ListViewModel list = ViewModelSamples.ListOf(main);

        list.TickAllCommand.Execute(null);

        Assert.Equal("1 candidates, 1 ticked, at least 10 B selected for removal", list.Header);
    }

    /// <summary>Review with no row ticked stays on the List and says what to do.</summary>
    [Fact]
    public void ReviewWithNothingTickedSaysNothingPicked()
    {
        MainViewModel main = ViewModelSamples.Listed(ViewModelSamples.Worktree("a", MergeState.Ancestor, 10));
        ListViewModel list = ViewModelSamples.ListOf(main);

        list.ReviewCommand.Execute(null);

        Assert.Equal(Screen.List, main.Screen);
        Assert.Equal("Nothing picked; tick a row first.", list.Message);
        Assert.Null(main.Review);
    }

    /// <summary>Changing a tick, or Tick all or Tick none, clears the Nothing picked message.</summary>
    [Fact]
    public void TickingARowClearsTheNothingPickedMessage()
    {
        MainViewModel main = ViewModelSamples.Listed(ViewModelSamples.Worktree("a", MergeState.Ancestor, 10));
        ListViewModel list = ViewModelSamples.ListOf(main);
        list.ReviewCommand.Execute(null);
        Assert.Equal(ListViewModel.NothingPicked, list.Message);

        list.Rows[0].IsTicked = true;

        Assert.Null(list.Message);
        list.Rows[0].IsTicked = false;
        list.ReviewCommand.Execute(null);
        Assert.Equal(ListViewModel.NothingPicked, list.Message);
        list.TickNoneCommand.Execute(null);
        Assert.Null(list.Message);
    }

    /// <summary>Tick all and Tick none each announce one header change, however many rows they tick.</summary>
    [Fact]
    public void TickAllRaisesHeaderOnce()
    {
        MainViewModel main = ViewModelSamples.Listed(
            ViewModelSamples.Worktree("a", MergeState.Ancestor, 10),
            ViewModelSamples.Worktree("b", MergeState.Ancestor, 10),
            ViewModelSamples.Worktree("c", MergeState.Ancestor, 10)
        );
        ListViewModel list = ViewModelSamples.ListOf(main);
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
        list.TickNoneCommand.Execute(null);
        Assert.Equal(2, headerChanges);
    }

    /// <summary>The selected row's detail is its seven detail lines, dated with the injected offset.</summary>
    [Fact]
    public void SelectedRowShowsItsSevenDetailLines()
    {
        RegisteredCandidate candidate = ViewModelSamples.Worktree("a", MergeState.Ancestor, 10);
        MainViewModel main = ViewModelSamples.Listed(candidate);
        ListViewModel list = ViewModelSamples.ListOf(main);
        Assert.Empty(list.Detail);

        list.Selected = list.Rows[0];

        Assert.Equal(7, list.Detail.Count);
        Assert.Equal(DetailText.Lines(candidate, ReportSamples.Now, _ => TimeSpan.Zero), list.Detail);
    }
}
