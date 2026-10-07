using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;
using WorktreeSweep.Unlock;
using WorktreeSweep.ViewModels;

namespace WorktreeSweep.Tests.ViewModels;

/// <summary>The Removing screen's progress, cancel and unlock notice, and the Results screen it leads to.</summary>
public sealed class RemovingViewModelTests
{
    /// <summary>A runnable row goes from pending through removing… to done, and the heading counts the done ones.</summary>
    [Fact]
    public void ProgressMovesRowsThroughRemovingToDone()
    {
        RemovingViewModel removing = SkipThenTwoRuns();
        Assert.Equal(2, removing.Rows.Count);
        Assert.Equal(@"repo.wt\b", removing.Rows[0].Path);
        Assert.Equal("pending", removing.Rows[0].Status);
        Assert.Equal("Removing 0/2", removing.Heading);

        removing.OnProgress(new Progress.Started(1));
        Assert.Equal("removing…", removing.Rows[0].Status);
        removing.OnProgress(new Progress.Done(1));

        Assert.Equal("done", removing.Rows[0].Status);
        Assert.Equal("pending", removing.Rows[1].Status);
        Assert.Equal("Removing 1/2", removing.Heading);
    }

    /// <summary>Progress for a skipped decision or an index past the decisions changes nothing.</summary>
    [Fact]
    public void ProgressForAnUnknownIndexIsIgnored()
    {
        RemovingViewModel removing = SkipThenTwoRuns();

        removing.OnProgress(new Progress.Started(0));
        removing.OnProgress(new Progress.Done(0));
        removing.OnProgress(new Progress.Started(9));
        removing.OnProgress(new Progress.Done(-1));

        Assert.All(removing.Rows, row => Assert.Equal("pending", row.Status));
        Assert.Equal("Removing 0/2", removing.Heading);
    }

    /// <summary>Cancel cancels the removal's token once, says the removal stops after the current item, and disables itself.</summary>
    [Fact]
    public void CancelStopsAfterTheCurrentItem()
    {
        RemovingViewModel removing = SkipThenTwoRuns();
        int cancellations = 0;
        using CancellationTokenRegistration registration = removing.Token.Register(() => cancellations++);
        Assert.True(removing.CancelCommand.CanExecute(null));

        removing.CancelCommand.Execute(null);
        removing.CancelCommand.Execute(null);

        Assert.True(removing.Token.IsCancellationRequested);
        Assert.Equal(1, cancellations);
        Assert.Equal("Removing 0/2  stopping after the current item…", removing.Heading);
        Assert.False(removing.CancelCommand.CanExecute(null));
    }

    /// <summary>The unlock hand-off shows a notice and disables Cancel; its outcome becomes the banner and clears the notice.</summary>
    [Fact]
    public void UnlockNoticeAndBanner()
    {
        RemovingViewModel removing = SkipThenTwoRuns();

        removing.UnlockStarted(1);
        Assert.Equal("1 pick locked; finish the unlock step in the terminal.", removing.Notice);
        Assert.False(removing.CancelCommand.CanExecute(null));
        removing.UnlockStarted(2);
        Assert.Equal("2 picks locked; finish the unlock step in the terminal.", removing.Notice);

        removing.UnlockDone(UnlockOutcome.PartlyUnlocked);

        Assert.Equal("Unlock: Partly unlocked", removing.Banner);
        Assert.Null(removing.Notice);
        Assert.True(removing.CancelCommand.CanExecute(null));
    }

    /// <summary>Finishing shows Results: one line per pick, the total apart, and the unlock banner.</summary>
    [Fact]
    public void FinishedShowsTheSummaryLinesAndTotal()
    {
        RegisteredCandidate a = ViewModelSamples.Worktree("a", MergeState.Ancestor, 10);
        MainViewModel main = ViewModelSamples.Listed(a);
        ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy).Answer(true);
        main.Review?.ConfirmCommand.Execute(null);
        RemovingViewModel? removing = main.Removing;
        Assert.NotNull(removing);
        removing.UnlockDone(UnlockOutcome.Skipped);
        Swept[] swept = [new(a, new Outcome.Recycled(a.KnownSize), ["branch a deleted"])];

        removing.Finished(swept);

        Assert.Equal(Screen.Results, main.Screen);
        ResultsViewModel? results = main.Results;
        Assert.NotNull(results);
        IReadOnlyList<string> lines = SweepSummary.Lines(swept, ReportSamples.Root);
        Assert.Equal([@"repo.wt\a: removed (recycled); branch a deleted"], results.Lines);
        Assert.Equal(lines[^1], results.Total);
        Assert.Equal("Unlock: Skipped", results.Banner);
    }

    /// <summary>Showing the Results drops the finished removal and releases its cancellation.</summary>
    [Fact]
    public void ResultsLeaveNoRemoving()
    {
        RegisteredCandidate a = ViewModelSamples.Worktree("a", MergeState.Ancestor, 10);
        MainViewModel main = ViewModelSamples.Listed(a);
        ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy).Answer(true);
        main.Review?.ConfirmCommand.Execute(null);
        RemovingViewModel? removing = main.Removing;
        Assert.NotNull(removing);

        removing.Finished([new(a, new Outcome.Recycled(a.KnownSize), [])]);

        Assert.Equal(Screen.Results, main.Screen);
        Assert.Null(main.Removing);
        Assert.Throws<ObjectDisposedException>(() => removing.Token);
    }

    /// <summary>A Removing screen over a skipped pick <c>a</c> and runnable picks <c>b</c> and <c>c</c>.</summary>
    /// <returns>The view model.</returns>
    private static RemovingViewModel SkipThenTwoRuns()
    {
        var recycle = new Plan.Run(new RemoveAction.Delete(DeleteMethod.Recycle));
        Decision[] decisions =
        [
            new(ViewModelSamples.Worktree("a", MergeState.Ancestor, 10), new Plan.Skip("not confirmed"), new BranchChoice.NotOffered()),
            new(ViewModelSamples.Worktree("b", MergeState.Ancestor, 10), recycle, new BranchChoice.NotOffered()),
            new(ViewModelSamples.Worktree("c", MergeState.Ancestor, 10), recycle, new BranchChoice.NotOffered()),
        ];
        return new RemovingViewModel(decisions, [@"repo.wt\a", @"repo.wt\b", @"repo.wt\c"], _ => { });
    }
}
