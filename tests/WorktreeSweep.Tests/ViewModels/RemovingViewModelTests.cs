using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;
using WorktreeSweep.Unlock;
using WorktreeSweep.ViewModels;

namespace WorktreeSweep.Tests.ViewModels;

/// <summary>The Removing screen's progress, action text, byte line, cancel and unlock notice, and the Results screen it leads to.</summary>
public sealed class RemovingViewModelTests
{
    /// <summary>A runnable row goes from pending through removing… to done, and the heading counts the done ones of the runnable.</summary>
    [Fact]
    public void ProgressMovesRowsThroughRemovingToDone()
    {
        RemovingViewModel removing = SkipThenTwoRuns();
        Assert.Equal(2, removing.Rows.Count);
        Assert.Equal(@"repo.wt\b", removing.Rows[0].Path);
        Assert.Equal("pending", removing.Rows[0].Status);
        Assert.Equal("Removing 0 of 2", removing.Heading);

        removing.OnProgress(new Progress.Started(1));
        Assert.Equal("removing…", removing.Rows[0].Status);

        removing.OnProgress(new Progress.Done(1, new Outcome.Recycled(new KnownSize(10, false))));

        Assert.Equal("done", removing.Rows[0].Status);
        Assert.Equal("pending", removing.Rows[1].Status);
        Assert.Equal("Removing 1 of 2", removing.Heading);
    }

    /// <summary>Progress for a skipped decision or an index past the decisions changes nothing.</summary>
    [Fact]
    public void ProgressForAnUnknownIndexIsIgnored()
    {
        RemovingViewModel removing = SkipThenTwoRuns();

        removing.OnProgress(new Progress.Started(0));
        removing.OnProgress(new Progress.Done(0, new Outcome.Skipped("not confirmed")));
        removing.OnProgress(new Progress.Started(9));
        removing.OnProgress(new Progress.Done(-1, new Outcome.Permanent(null)));

        Assert.All(removing.Rows, row => Assert.Equal("pending", row.Status));
        Assert.Equal("Removing 0 of 2", removing.Heading);
        Assert.Equal(0, removing.BytesDone);
    }

    /// <summary>Done and Total count the runnable picks only, and Done advances with each finished one.</summary>
    [Fact]
    public void DoneAndTotalAdvanceWithProgress()
    {
        RemovingViewModel removing = SkipThenTwoRuns();
        Assert.Equal(2, removing.Total);
        Assert.Equal(0, removing.Done);

        removing.OnProgress(new Progress.Started(1));
        Assert.Equal(0, removing.Done);

        removing.OnProgress(new Progress.Done(1, new Outcome.LinkRemoved()));
        Assert.Equal(1, removing.Done);

        removing.OnProgress(new Progress.Done(2, new Outcome.Failed("locked (a.txt)")));
        Assert.Equal(2, removing.Done);
        Assert.Equal(2, removing.Total);
    }

    /// <summary>The heading reads <c>Removing {done} of {total}</c> and does not move on a start.</summary>
    [Fact]
    public void HeadingCountsDoneOfTotal()
    {
        RemovingViewModel removing = SkipThenTwoRuns();
        Assert.Equal("Removing 0 of 2", removing.Heading);

        removing.OnProgress(new Progress.Started(2));
        Assert.Equal("Removing 0 of 2", removing.Heading);

        removing.OnProgress(new Progress.Done(2, new Outcome.Pruned()));
        Assert.Equal("Removing 1 of 2", removing.Heading);
    }

    /// <summary>BytesTotal adds every runnable pick's size; BytesDone adds a removed pick's size only, an unknown size none.</summary>
    [Fact]
    public void BytesCountKnownSizesAndUnknownAsZero()
    {
        RemovingViewModel removing = Runs(
            (Recycle(), 2 * ViewModelSamples.Mb),
            (Recycle(), null),
            (Recycle(), ViewModelSamples.Mb),
            (Recycle(), ViewModelSamples.Mb)
        );

        Assert.Equal(4 * ViewModelSamples.Mb, removing.BytesTotal);
        Assert.Equal(0, removing.BytesDone);

        removing.OnProgress(new Progress.Done(0, new Outcome.Recycled(new KnownSize(2 * ViewModelSamples.Mb, false))));
        Assert.Equal(2 * ViewModelSamples.Mb, removing.BytesDone);

        removing.OnProgress(new Progress.Done(1, new Outcome.Recycled(null)));
        Assert.Equal(2 * ViewModelSamples.Mb, removing.BytesDone);

        removing.OnProgress(new Progress.Done(2, new Outcome.Failed("locked (a.txt)")));
        Assert.Equal(2 * ViewModelSamples.Mb, removing.BytesDone);

        removing.OnProgress(new Progress.Done(3, new Outcome.Recycled(new KnownSize(ViewModelSamples.Mb, false))));
        Assert.Equal(3 * ViewModelSamples.Mb, removing.BytesDone);
        Assert.Equal(4 * ViewModelSamples.Mb, removing.BytesTotal);
    }

    /// <summary>The byte line names the Recycle Bin only when every runnable pick recycles, and plain removal otherwise.</summary>
    [Fact]
    public void BytesLineNamesTheRecycleBinOnlyWhenEveryPickRecycles()
    {
        RemovingViewModel recycling = Runs((Recycle(), 2 * ViewModelSamples.Mb), (Recycle(), ViewModelSamples.Mb));
        Assert.Equal("0 B of 3.0 MB moved to the Recycle Bin", recycling.BytesLine);

        recycling.OnProgress(new Progress.Done(0, new Outcome.Recycled(new KnownSize(2 * ViewModelSamples.Mb, false))));
        Assert.Equal("2.0 MB of 3.0 MB moved to the Recycle Bin", recycling.BytesLine);

        RemovingViewModel mixed = Runs((Recycle(), 2 * ViewModelSamples.Mb), (Permanent(), ViewModelSamples.Mb));
        Assert.Equal("0 B of 3.0 MB removed", mixed.BytesLine);

        RemovingViewModel link = Runs((new RemoveAction.RemoveLink(), null));
        Assert.Equal("", link.BytesLine);
    }

    /// <summary>No pick with a size the scan read means no byte line at all.</summary>
    [Fact]
    public void BytesLineIsHiddenWhenNoSizeIsKnown()
    {
        RemovingViewModel removing = Runs((Recycle(), null), (Recycle(), null));

        Assert.Equal(0, removing.BytesTotal);
        Assert.Equal("", removing.BytesLine);
    }

    /// <summary>A size the scan read only in part shows as "at least", as the List and Review screens do.</summary>
    [Fact]
    public void RowSizeTextSaysAtLeastForAPartlyReadSize()
    {
        RemovingViewModel partial = RunOfPartialSize(Recycle(), 2 * ViewModelSamples.Mb);
        Assert.Equal("at least 2.0 MB", partial.Rows[0].SizeText);

        RemovingViewModel whole = Runs((Recycle(), 2 * ViewModelSamples.Mb));
        Assert.Equal("2.0 MB", whole.Rows[0].SizeText);

        RemovingViewModel unknown = Runs((Recycle(), null));
        Assert.Equal("", unknown.Rows[0].SizeText);
    }

    /// <summary>The byte line says "at least" on either side when a size it counts was read only in part.</summary>
    [Fact]
    public void BytesLineSaysAtLeastForPartlyReadSizes()
    {
        RemovingViewModel removing = RunOfPartialSize(Recycle(), 2 * ViewModelSamples.Mb);

        Assert.Equal("0 B of at least 2.0 MB moved to the Recycle Bin", removing.BytesLine);

        removing.OnProgress(new Progress.Done(0, new Outcome.Recycled(new KnownSize(2 * ViewModelSamples.Mb, true))));

        Assert.Equal("at least 2.0 MB of at least 2.0 MB moved to the Recycle Bin", removing.BytesLine);
    }

    /// <summary>A pending row says what its plan will do, in each plan's words.</summary>
    [Fact]
    public void PendingActionTextNamesThePlan()
    {
        RemovingViewModel removing = EachPlan();

        string[] texts = [.. removing.Rows.Select(row => row.ActionText)];

        Assert.Equal(
            [
                "Waiting · will recycle",
                "Waiting · will delete permanently",
                "Waiting · will remove the link",
                "Waiting · will prune the registration",
            ],
            texts
        );
    }

    /// <summary>A removing row says what it is doing now, in each plan's words.</summary>
    [Fact]
    public void RemovingActionTextNamesThePlan()
    {
        RemovingViewModel removing = EachPlan();

        foreach (int index in Enumerable.Range(0, removing.Rows.Count))
        {
            removing.OnProgress(new Progress.Started(index));
        }

        string[] texts = [.. removing.Rows.Select(row => row.ActionText)];

        Assert.Equal(["Recycling…", "Deleting permanently…", "Removing the link…", "Pruning the registration…"], texts);
    }

    /// <summary>A done row says what its removal came to: the plan's past tense, or the words Results uses for a failure.</summary>
    [Fact]
    public void DoneActionTextNamesTheOutcome()
    {
        RemovingViewModel removing = Runs(
            (Recycle(), 10),
            (Permanent(), 10),
            (new RemoveAction.RemoveLink(), 10),
            (new RemoveAction.PruneRegistration(), 10),
            (Recycle(), 10)
        );

        removing.OnProgress(new Progress.Done(0, new Outcome.Recycled(new KnownSize(10, false))));
        removing.OnProgress(new Progress.Done(1, new Outcome.Permanent(new KnownSize(10, false))));
        removing.OnProgress(new Progress.Done(2, new Outcome.LinkRemoved()));
        removing.OnProgress(new Progress.Done(3, new Outcome.Pruned()));
        removing.OnProgress(new Progress.Done(4, new Outcome.Failed("locked (a.txt)")));

        string[] texts = [.. removing.Rows.Select(row => row.ActionText)];

        Assert.Equal(["Recycled", "Deleted permanently", "Link removed", "Registration pruned", "failed: locked (a.txt)"], texts);
    }

    /// <summary>A retried pick reports started and done again; its row and the bytes follow the latest report.</summary>
    [Fact]
    public void RetriedPickFollowsTheLatestReport()
    {
        RemovingViewModel removing = Runs((Permanent(), 2 * ViewModelSamples.Mb));

        removing.OnProgress(new Progress.Started(0));
        removing.OnProgress(new Progress.Done(0, new Outcome.Failed("locked (held.txt)")));
        Assert.Equal("failed: locked (held.txt)", removing.Rows[0].ActionText);
        Assert.Equal(0, removing.BytesDone);

        removing.OnProgress(new Progress.Started(0));
        Assert.Equal("Deleting permanently…", removing.Rows[0].ActionText);

        removing.OnProgress(new Progress.Done(0, new Outcome.Permanent(new KnownSize(2 * ViewModelSamples.Mb, false))));
        Assert.Equal("Deleted permanently", removing.Rows[0].ActionText);
        Assert.Equal(2 * ViewModelSamples.Mb, removing.BytesDone);
        Assert.Equal(1, removing.Done);
    }

    /// <summary>A pick the Stop token skips in the retry pass says so, in the words the Results screen uses.</summary>
    [Fact]
    public void SkippedRetryShowsTheSkippedWording()
    {
        RemovingViewModel removing = Runs((Permanent(), 2 * ViewModelSamples.Mb));

        removing.OnProgress(new Progress.Started(0));
        removing.OnProgress(new Progress.Done(0, new Outcome.Failed("locked (held.txt)")));
        Assert.Equal("failed: locked (held.txt)", removing.Rows[0].ActionText);
        Assert.Equal(0, removing.BytesDone);

        removing.OnProgress(new Progress.Done(0, new Outcome.Skipped("cancelled")));

        Assert.Equal("skipped (cancelled)", removing.Rows[0].ActionText);
        Assert.Equal("done", removing.Rows[0].Status);
        Assert.Equal(0, removing.BytesDone);
        Assert.Equal(1, removing.Done);
    }

    /// <summary>Stop stops the removal once, says the removal stops after the current item, and disables itself.</summary>
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
        Assert.Equal("Removing 0 of 2  stopping after the current item…", removing.Heading);
        Assert.False(removing.CancelCommand.CanExecute(null));
    }

    /// <summary>Stop keeps its rules under its new text: the removal finishing refuses it.</summary>
    [Fact]
    public void StopIsRefusedOnceTheRemovalFinishes()
    {
        RemovingViewModel removing = SkipThenTwoRuns();
        Assert.True(removing.CancelCommand.CanExecute(null));

        removing.Finished([]);

        Assert.False(removing.CancelCommand.CanExecute(null));
    }

    /// <summary>The unlock hand-off shows a notice and disables Stop; its outcome becomes the banner and clears the notice.</summary>
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

    /// <summary>The plan that recycles a pick.</summary>
    /// <returns>The action.</returns>
    private static RemoveAction.Delete Recycle() => new(DeleteMethod.Recycle);

    /// <summary>The plan that deletes a pick for good.</summary>
    /// <returns>The action.</returns>
    private static RemoveAction.Delete Permanent() => new(DeleteMethod.Permanent);

    /// <summary>A Removing screen over one runnable pick per action and size, each with the size the scan read, or none.</summary>
    /// <param name="picks">Each pick's action and its size, <see langword="null"/> when the scan read none.</param>
    /// <returns>The view model.</returns>
    private static RemovingViewModel Runs(params (RemoveAction Action, long? Bytes)[] picks)
    {
        Decision[] decisions =
        [
            .. picks.Select(
                (pick, index) => new Decision(Sized(index, pick.Bytes, partial: false), new Plan.Run(pick.Action), new BranchChoice.NotOffered())
            ),
        ];
        string[] paths = [.. picks.Select((_, index) => $@"repo.wt\p{index}")];
        return new RemovingViewModel(decisions, paths, _ => { });
    }

    /// <summary>A Removing screen over one runnable pick whose size the scan read only in part.</summary>
    /// <param name="action">How the pick is removed.</param>
    /// <param name="bytes">The size the scan read, a lower bound.</param>
    /// <returns>The view model.</returns>
    private static RemovingViewModel RunOfPartialSize(RemoveAction action, long bytes) =>
        new([new Decision(Sized(0, bytes, partial: true), new Plan.Run(action), new BranchChoice.NotOffered())], [@"repo.wt\p0"], _ => { });

    /// <summary>A Removing screen over one runnable pick of each plan: recycle, permanent delete, link and prune.</summary>
    /// <returns>The view model.</returns>
    private static RemovingViewModel EachPlan() =>
        Runs((Recycle(), 10), (Permanent(), 10), (new RemoveAction.RemoveLink(), 10), (new RemoveAction.PruneRegistration(), 10));

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

    /// <summary>The scanned candidate <c>repo.wt\p{index}</c>: its size, none, or a size the scan read only in part.</summary>
    /// <param name="index">The pick's number.</param>
    /// <param name="bytes">Its size, <see langword="null"/> when the scan read none.</param>
    /// <param name="partial">Whether the scan could not read every entry, so the bytes are a lower bound.</param>
    /// <returns>The candidate.</returns>
    private static RegisteredCandidate Sized(int index, long? bytes, bool partial)
    {
        RegisteredCandidate candidate = ViewModelSamples.Worktree($"p{index}", MergeState.Ancestor, bytes ?? 0);
        if (bytes is null)
        {
            return candidate with { Signals = candidate.Signals with { Size = null } };
        }
        return partial
            ? candidate with
            {
                Signals = candidate.Signals with
                {
                    Size = new SizeInfo { Bytes = bytes.Value, Unreadable = 1 },
                },
            }
            : candidate;
    }
}
