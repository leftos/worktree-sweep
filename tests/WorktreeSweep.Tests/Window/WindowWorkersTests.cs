using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;
using WorktreeSweep.Tests.ViewModels;
using WorktreeSweep.Unlock;
using WorktreeSweep.ViewModels;
using WorktreeSweep.Window;

namespace WorktreeSweep.Tests.Window;

/// <summary>The window's background jobs and its close rule, with every job run at once on the test's thread.</summary>
public sealed class WindowWorkersTests
{
    /// <summary>The worktree every removal test picks.</summary>
    private static readonly RegisteredCandidate A = ViewModelSamples.Worktree("a", MergeState.Ancestor, 10);

    /// <summary>A completed scan shows its candidates on the List.</summary>
    [Fact]
    public void ScanShowsTheList()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var scanned = new List<string>();
        WindowSeams seams = Seams() with
        {
            Scan = root =>
            {
                scanned.Add(root);
                return ReportSamples.ReportOf(A);
            },
        };

        new WindowWorkers(main, seams).Start(ReportSamples.Root);

        Assert.Equal([ReportSamples.Root], scanned);
        Assert.Equal(Screen.List, main.Screen);
        Assert.Single(ViewModelSamples.ListOf(main).Rows);
    }

    /// <summary>A scan that throws shows the Failed screen with the failure's message.</summary>
    [Fact]
    public void ScanThatThrowsShowsScanFailed()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        WindowSeams seams = Seams() with { Scan = _ => throw new IOException("boom") };

        new WindowWorkers(main, seams).Start(ReportSamples.Root);

        Assert.Equal(Screen.Failed, main.Screen);
        Assert.Equal("Scan failed: boom", main.Message);
    }

    /// <summary>Entering Review reads the capacity of every path it asks for, once each, and Review leaves Preparing.</summary>
    [Fact]
    public void EnteringReviewReadsEveryCapacityPath()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var asked = new List<string>();
        WindowSeams seams = Seams(
            ViewModelSamples.Worktree("a", MergeState.Ancestor, 10),
            ViewModelSamples.Worktree("b", MergeState.Ancestor, 20)
        ) with
        {
            ReadCapacity = path =>
            {
                asked.Add(path);
                return ViewModelSamples.Roomy;
            },
        };
        new WindowWorkers(main, seams).Start(ReportSamples.Root);

        ListViewModel list = ViewModelSamples.ListOf(main);
        list.TickAllCommand.Execute(null);
        list.ReviewCommand.Execute(null);

        ReviewViewModel review = main.Review ?? throw new InvalidOperationException("Review was not entered");
        Assert.Equal(2, review.CapacityPaths.Count);
        Assert.Equal(review.CapacityPaths, asked);
        Assert.NotEqual(ReviewState.Preparing, review.State);
    }

    /// <summary>The removal runs on the STA thread seam and its end shows the Results.</summary>
    [Fact]
    public void RemovalRunsOnTheStaThreadAndShowsResults()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        bool onSta = false;
        bool removedOnSta = false;
        WindowSeams seams = Seams(A) with
        {
            RunOnStaThread = action =>
            {
                onSta = true;
                action();
                onSta = false;
            },
            RemovePicks = (decisions, _, _, _) =>
            {
                removedOnSta = onSta;
                return Recycled(decisions);
            },
        };
        new WindowWorkers(main, seams).Start(ReportSamples.Root);

        Confirm(main);

        Assert.True(removedOnSta, "the removal did not run on the STA thread seam");
        Assert.Equal(Screen.Results, main.Screen);
    }

    /// <summary>The window shows the unlock step before the offer runs and its outcome after.</summary>
    [Fact]
    public void UnlockStartedIsShownBeforeTheOfferAndDoneAfter()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var calls = new List<string>();
        WindowSeams seams = Seams(A) with
        {
            RemovePicks = (decisions, _, offerUnlock, _) =>
            {
                offerUnlock([A.Path]);
                return Recycled(decisions);
            },
            OfferUnlock = _ =>
            {
                calls.Add("OfferUnlock");
                return UnlockOutcome.Unlocked;
            },
        };
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Removing) && main.Removing is { } removing)
            {
                removing.PropertyChanged += (_, changed) =>
                {
                    if (changed.PropertyName == nameof(RemovingViewModel.Notice) && removing.Notice is not null)
                    {
                        calls.Add("UnlockStarted");
                    }
                    if (changed.PropertyName == nameof(RemovingViewModel.Banner))
                    {
                        calls.Add("UnlockDone");
                    }
                };
            }
        };
        new WindowWorkers(main, seams).Start(ReportSamples.Root);

        Confirm(main);

        Assert.Equal(["UnlockStarted", "OfferUnlock", "UnlockDone"], calls);
        Assert.Equal("Unlock: Unlocked", main.Results?.Banner);
    }

    /// <summary>A removal that throws shows the Failed screen with the failure's message.</summary>
    [Fact]
    public void RemovalThatThrowsShowsRemovalFailed()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        WindowSeams seams = Seams(A) with { RemovePicks = (_, _, _, _) => throw new InvalidOperationException("boom") };
        new WindowWorkers(main, seams).Start(ReportSamples.Root);

        Confirm(main);

        Assert.Equal(Screen.Failed, main.Screen);
        Assert.Equal("Removal failed: boom", main.Message);
    }

    /// <summary>The window may close while the scan runs.</summary>
    [Fact]
    public void CloseIsAllowedWhileScanning()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var workers = new WindowWorkers(main, Seams(A) with { RunInBackground = _ => { } });
        workers.Start(ReportSamples.Root);

        bool allowed = workers.RequestClose();

        Assert.Equal(Screen.Scanning, main.Screen);
        Assert.True(allowed);
    }

    /// <summary>Closing during the removal cancels it and keeps the window open.</summary>
    [Fact]
    public void CloseDuringRemovalCancelsAndStaysOpen()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var workers = new WindowWorkers(main, Seams(A) with { RunOnStaThread = _ => { } });
        workers.Start(ReportSamples.Root);
        Confirm(main);
        RemovingViewModel removing = main.Removing ?? throw new InvalidOperationException("the removal did not start");

        bool allowed = workers.RequestClose();

        Assert.False(allowed);
        Assert.True(removing.Token.IsCancellationRequested, "closing did not cancel the removal");
        Assert.Equal(Screen.Removing, main.Screen);
    }

    /// <summary>Closing while the unlock step runs is refused and cancels nothing.</summary>
    [Fact]
    public void CloseDuringTheUnlockStepIsRefusedAndCancelsNothing()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        WindowWorkers? workers = null;
        bool? allowed = null;
        bool cancelled = true;
        WindowSeams seams = Seams(A) with
        {
            RemovePicks = (decisions, _, offerUnlock, _) =>
            {
                offerUnlock([A.Path]);
                return Recycled(decisions);
            },
            OfferUnlock = _ =>
            {
                allowed = workers?.RequestClose();
                cancelled = main.Removing?.Token.IsCancellationRequested ?? true;
                return UnlockOutcome.Skipped;
            },
        };
        workers = new WindowWorkers(main, seams);
        workers.Start(ReportSamples.Root);

        Confirm(main);

        Assert.False(allowed);
        Assert.False(cancelled, "closing during the unlock step cancelled the removal");
    }

    /// <summary>The window may close on the Results.</summary>
    [Fact]
    public void CloseIsAllowedOnResults()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var workers = new WindowWorkers(main, Seams(A));
        workers.Start(ReportSamples.Root);
        Confirm(main);
        Assert.Equal(Screen.Results, main.Screen);

        Assert.True(workers.RequestClose());
    }

    /// <summary>A capacity read that throws counts as an unknown capacity, and Review still leaves Preparing.</summary>
    [Fact]
    public void CapacityReadThatThrowsCountsAsUnknown()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        RegisteredCandidate b = ViewModelSamples.Worktree("b", MergeState.Ancestor, 20);
        WindowSeams seams = Seams(A, b) with
        {
            ReadCapacity = path => path == A.Path ? throw new ArgumentException("bad path") : ViewModelSamples.Roomy,
        };
        new WindowWorkers(main, seams).Start(ReportSamples.Root);

        ListViewModel list = ViewModelSamples.ListOf(main);
        list.TickAllCommand.Execute(null);
        list.ReviewCommand.Execute(null);

        Assert.Equal(Screen.Review, main.Screen);
        Assert.NotEqual(ReviewState.Preparing, main.Review?.State);
    }

    /// <summary>A completed scan the window cannot show is shown as a failed scan.</summary>
    [Fact]
    public void ScanCompletedThatThrowsShowsScanFailed()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        WindowSeams seams = Seams() with { Scan = _ => null! };

        new WindowWorkers(main, seams).Start(ReportSamples.Root);

        Assert.Equal(Screen.Failed, main.Screen);
        Assert.StartsWith("Scan failed: ", main.Message, StringComparison.Ordinal);
    }

    /// <summary>A finished removal the window cannot show is shown as a failed removal.</summary>
    [Fact]
    public void FinishedThatThrowsShowsRemovalFailed()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        WindowSeams seams = Seams(A) with { RemovePicks = (_, _, _, _) => null! };
        new WindowWorkers(main, seams).Start(ReportSamples.Root);

        Confirm(main);

        Assert.Equal(Screen.Failed, main.Screen);
        Assert.StartsWith("Removal failed: ", main.Message, StringComparison.Ordinal);
    }

    /// <summary>An unlock offer that throws is shown as skipped, and the removal it ends as failed.</summary>
    [Fact]
    public void OfferUnlockThatThrowsShowsSkippedThenRemovalFailed()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        RemovingViewModel? removing = null;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Removing) && main.Removing is { } started)
            {
                removing = started;
            }
        };
        WindowSeams seams = Seams(A) with
        {
            RemovePicks = (decisions, _, offerUnlock, _) =>
            {
                offerUnlock([A.Path]);
                return Recycled(decisions);
            },
            OfferUnlock = _ => throw new InvalidOperationException("boom"),
        };
        new WindowWorkers(main, seams).Start(ReportSamples.Root);

        Confirm(main);

        Assert.Equal("Unlock: Skipped", removing?.Banner);
        Assert.Null(removing?.Notice);
        Assert.Equal(Screen.Failed, main.Screen);
        Assert.Equal("Removal failed: boom", main.Message);
    }

    /// <summary>
    /// Seams whose scan finds <paramref name="candidates"/>, whose every volume has a roomy bin, whose removal recycles every pick,
    /// whose unlock step is skipped, and which run every job and every dispatch at once on the calling thread.
    /// </summary>
    /// <param name="candidates">What the scan finds.</param>
    /// <returns>The seams.</returns>
    private static WindowSeams Seams(params Candidate[] candidates) =>
        new()
        {
            Scan = _ => ReportSamples.ReportOf(candidates),
            ReadCapacity = _ => ViewModelSamples.Roomy,
            RemovePicks = (decisions, _, _, _) => Recycled(decisions),
            OfferUnlock = _ => UnlockOutcome.Skipped,
            RunInBackground = action => action(),
            RunOnStaThread = action => action(),
            Dispatcher = new ImmediateDispatcher(),
        };

    /// <summary>Every decision recycled.</summary>
    /// <param name="decisions">The decisions.</param>
    /// <returns>One recycled entry per decision.</returns>
    private static IReadOnlyList<Swept> Recycled(IReadOnlyList<Decision> decisions) =>
        [.. decisions.Select(decision => new Swept(decision.Candidate, new Outcome.Recycled(decision.Candidate.KnownSize), []))];

    /// <summary>Ticks every row, enters Review, deletes the branch and confirms the removal.</summary>
    /// <param name="main">The window, on the List screen.</param>
    private static void Confirm(MainViewModel main)
    {
        ListViewModel list = ViewModelSamples.ListOf(main);
        list.TickAllCommand.Execute(null);
        list.ReviewCommand.Execute(null);
        ReviewViewModel review = main.Review ?? throw new InvalidOperationException("Review was not entered");
        review.Answer(true);
        review.ConfirmCommand.Execute(null);
    }

    /// <summary>A UI thread that is the calling thread: every action runs at once.</summary>
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        /// <inheritdoc/>
        public void Post(Action action) => action();

        /// <inheritdoc/>
        public void Invoke(Action action) => action();
    }
}
