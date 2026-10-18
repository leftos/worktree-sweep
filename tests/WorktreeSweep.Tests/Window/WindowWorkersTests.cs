using System.Collections.Concurrent;
using WorktreeSweep.Recycle;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Scan;
using WorktreeSweep.Signals;
using WorktreeSweep.Tests.ViewModels;
using WorktreeSweep.Unlock;
using WorktreeSweep.ViewModels;
using WorktreeSweep.Window;

namespace WorktreeSweep.Tests.Window;

/// <summary>
/// The window's background jobs and its close rule, with every job run at once on the test's thread unless a test holds it back. Each
/// test scans its own empty folder under <c>%TEMP%</c>, which also keeps its recent folders.
/// </summary>
public sealed class WindowWorkersTests : IDisposable
{
    /// <summary>The worktree every removal test picks.</summary>
    private static readonly RegisteredCandidate A = ViewModelSamples.Worktree("a", MergeState.Ancestor, 10);

    /// <summary>How long a test waits on a thread it started before failing; a passing run never comes near it.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(5);

    /// <summary>The folder the tests scan, which also holds their recent folders file.</summary>
    private readonly string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"wts-workers-{Guid.NewGuid():N}")).FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(folder, recursive: true);

    /// <summary>A new scan cancels the one still running, and the old scan's cancellation is not shown.</summary>
    [Fact]
    public void NewScanCancelsTheOld()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var background = new List<Action>();
        var tokens = new List<CancellationToken>();
        WindowSeams seams = Seams(A) with
        {
            RunInBackground = background.Add,
            Scan = (_, _, token) =>
            {
                tokens.Add(token);
                token.ThrowIfCancellationRequested();
                return ReportSamples.ReportOf(A);
            },
        };
        Started(main, seams);
        main.StopScanCommand.Execute(null);
        StartScan(main);
        Assert.Equal(3, background.Count);

        background[1]();
        background[0]();

        Assert.True(tokens[0].IsCancellationRequested, "the new scan did not cancel the old");
        Assert.Equal(Screen.Scanning, main.Screen);
        background[2]();
        Assert.False(tokens[1].IsCancellationRequested, "the new scan was cancelled");
        Assert.Equal(Screen.List, main.Screen);
    }

    /// <summary>A scan that was stopped and replaced has its late progress and report dropped: the new scan's screen stays as it was.</summary>
    [Fact]
    public void StaleScanProgressAndResultAreDropped()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var background = new List<Action>();
        WindowSeams seams = Seams(A) with
        {
            RunInBackground = background.Add,
            Scan = (_, onProgress, _) =>
            {
                onProgress(new ScanProgress.Discovered(1, 1, 1, 0, 0));
                return ReportSamples.ReportOf(A);
            },
        };
        Started(main, seams);
        main.StopScanCommand.Execute(null);
        StartScan(main);
        ScanningViewModel scanning = main.Scanning ?? throw new InvalidOperationException("the new scan did not begin");

        background[0]();

        Assert.Equal(Screen.Scanning, main.Screen);
        Assert.Same(scanning, main.Scanning);
        Assert.Equal(StepState.Running, scanning.Find.State);
        Assert.Null(main.List);
        Assert.Empty(main.Recent);
    }

    /// <summary>A scan that was stopped and replaced has its late failure dropped.</summary>
    [Fact]
    public void StaleScanFailureIsDropped()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var background = new List<Action>();
        WindowSeams seams = Seams(A) with { RunInBackground = background.Add, Scan = (_, _, _) => throw new IOException("boom") };
        Started(main, seams);
        main.StopScanCommand.Execute(null);
        StartScan(main);

        background[0]();

        Assert.Equal(Screen.Scanning, main.Screen);
        Assert.Equal("", main.Message);
    }

    /// <summary>
    /// Progress is posted at most once per 100 ms of the scan's clock, and the last state is posted when the scan ends even inside the
    /// interval, before the report.
    /// </summary>
    [Fact]
    public void ProgressIsCoalescedToOnePostPer100MsAndTheLastIsPosted()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        TimeSpan now = TimeSpan.Zero;
        var shown = new List<(int Step, int Percent)>();
        var dispatcher = new RecordingDispatcher(() => shown.Add((main.Scanning?.Step ?? 0, main.Scanning?.Percent ?? -1)));
        WindowSeams seams = Seams(A) with
        {
            Dispatcher = dispatcher,
            Clock = () => now,
            Scan = (_, onProgress, _) =>
            {
                (int Ms, ScanProgress Progress)[] events =
                [
                    (0, new ScanProgress.Discovering(1, 2)),
                    (50, new ScanProgress.Discovering(2, 2)),
                    (99, new ScanProgress.Discovered(1, 1, 1, 0, 0)),
                    (100, new ScanProgress.RepoLookedUp(1, 1)),
                    (150, new ScanProgress.CandidateRead(A.Path, 1, 1)),
                ];
                foreach ((int ms, ScanProgress progress) in events)
                {
                    now = TimeSpan.FromMilliseconds(ms);
                    onProgress(progress);
                }
                return ReportSamples.ReportOf(A);
            },
        };

        Started(main, seams);

        Assert.Equal([(1, 0), (3, 0), (3, 100), (3, 100)], shown);
        Assert.Equal(Screen.List, main.Screen);
    }

    /// <summary>Closing while scanning is allowed and cancels the scan without waiting for it; its cancellation is not shown.</summary>
    [Fact]
    public void CloseWhileScanningCancelsAndIsAllowed()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var background = new List<Action>();
        CancellationToken seen = CancellationToken.None;
        WindowSeams seams = Seams(A) with
        {
            RunInBackground = background.Add,
            Scan = (_, _, token) =>
            {
                seen = token;
                token.ThrowIfCancellationRequested();
                return ReportSamples.ReportOf(A);
            },
        };
        WindowWorkers workers = Started(main, seams);

        bool allowed = workers.RequestClose();
        background[1]();
        background[0]();

        Assert.True(allowed);
        Assert.True(seen.IsCancellationRequested, "closing did not cancel the scan");
        Assert.Equal(Screen.Scanning, main.Screen);
    }

    /// <summary>A progress event held back by the throttle is posted on the next elapsed-time tick, with no further event.</summary>
    [Fact]
    public void HeldProgressIsPostedOnTheNextTick()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        TimeSpan now = TimeSpan.Zero;
        var timer = new FakeTimer();
        string? shownOnTick = null;
        WindowSeams seams = Seams(A) with
        {
            Clock = () => now,
            StartTimer = timer.Start,
            Scan = (_, onProgress, _) =>
            {
                onProgress(new ScanProgress.Discovering(1, 3));
                now = TimeSpan.FromMilliseconds(50);
                onProgress(new ScanProgress.Discovering(2, 3));
                now = TimeSpan.FromSeconds(1);
                timer.Tick();
                shownOnTick = main.Scanning?.Find.Detail;
                return ReportSamples.ReportOf(A);
            },
        };

        Started(main, seams);

        Assert.Equal("folder 2 of 3", shownOnTick);
    }

    /// <summary>A candidate leaves the working list on its own read, whatever order the reads come in.</summary>
    [Fact]
    public void WorkingRowIsDroppedOnItsCandidatesRead()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        TimeSpan now = TimeSpan.Zero;
        IReadOnlyList<WorkingRow>? working = null;
        WindowSeams seams = Seams(A) with
        {
            Clock = () => now,
            Scan = (root, onProgress, _) =>
            {
                string first = Path.Join(root, "first");
                string second = Path.Join(root, "second");
                ScanProgress[] events =
                [
                    new ScanProgress.CandidateStep(first, ScanStep.Status, 0),
                    new ScanProgress.CandidateStep(second, ScanStep.Size, 1234),
                    new ScanProgress.CandidateRead(first, 1, 2),
                ];
                foreach (ScanProgress progress in events)
                {
                    now += TimeSpan.FromSeconds(1);
                    onProgress(progress);
                }
                working = main.Scanning?.Working;
                return ReportSamples.ReportOf(A);
            },
        };

        Started(main, seams);

        Assert.Equal([new WorkingRow("second", "measuring size · 1,234 files so far")], working);
    }

    /// <summary>
    /// Closing while scanning completes <see cref="WindowWorkers.CloseCancelled"/> only after the scan's cancel has run its kill
    /// registrations, so the process can wait for them before it exits.
    /// </summary>
    [Fact]
    public void CloseCancelCompletesBeforeCloseCancelledDoes()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var background = new List<Action>();
        var killSawCloseCancelled = new ConcurrentQueue<bool>();
        WindowWorkers? workers = null;
        using var scanning = new ManualResetEventSlim();
        WindowSeams seams = Seams(A) with
        {
            RunInBackground = background.Add,
            Scan = (_, _, token) =>
            {
                using CancellationTokenRegistration kill = token.Register(() =>
                    killSawCloseCancelled.Enqueue(workers?.CloseCancelled.IsCompleted ?? true)
                );
                scanning.Set();
                token.WaitHandle.WaitOne(HangGuard);
                token.ThrowIfCancellationRequested();
                return ReportSamples.ReportOf(A);
            },
        };
        workers = Started(main, seams);
        var scan = new Thread(new ThreadStart(background[0]));
        scan.Start();
        Assert.True(scanning.Wait(HangGuard, TestContext.Current.CancellationToken), "the scan did not start");

        Assert.True(workers.RequestClose());
        Assert.False(workers.CloseCancelled.IsCompleted, "the close's cancel completed before it ran");
        background[1]();

        Assert.True(workers.CloseCancelled.IsCompleted, "the close's cancel did not complete");
        Assert.Equal([false], killSawCloseCancelled);
        Assert.True(scan.Join(HangGuard), "the cancelled scan did not end");
    }

    /// <summary>The recent folders' ages are measured again each time the Choose screen shows, Stop scan included.</summary>
    [Fact]
    public void RecentAgesAreMeasuredAgainOnChoose()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        long now = ReportSamples.Now;
        bool hold = false;
        var held = new List<Action>();
        WindowSeams seams = Seams(A) with
        {
            NowUnix = () => now,
            RunInBackground = action =>
            {
                if (hold)
                {
                    held.Add(action);
                }
                else
                {
                    action();
                }
            },
        };
        Started(main, seams);
        Assert.StartsWith("just now · ", Assert.Single(main.Recent).Summary, StringComparison.Ordinal);
        now += 2 * 24 * 60 * 60;
        hold = true;

        StartScan(main);
        main.StopScanCommand.Execute(null);

        Assert.Equal(Screen.Choose, main.Screen);
        Assert.StartsWith("2 days ago · ", Assert.Single(main.Recent).Summary, StringComparison.Ordinal);
    }

    /// <summary>An elapsed-time tick sets the running scan's elapsed time from the clock.</summary>
    [Fact]
    public void TimerTickAdvancesTheRunningScansElapsedTime()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var now = TimeSpan.FromSeconds(10);
        var timer = new FakeTimer();
        Started(main, Seams(A) with { Clock = () => now, StartTimer = timer.Start, RunInBackground = _ => { } });

        now = TimeSpan.FromSeconds(13);
        timer.Tick();

        Assert.Equal(TimeSpan.FromSeconds(3), main.Scanning?.Elapsed);
        Assert.Equal("step 1 of 3 · 3 s", main.Scanning?.StepText);
    }

    /// <summary>A tick from a scan that was stopped and replaced changes neither scan's elapsed time, and ending a scan stops its timer.</summary>
    [Fact]
    public void StaleTimerTickChangesNothingAndEndingAScanStopsItsTimer()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        TimeSpan now = TimeSpan.Zero;
        var timer = new FakeTimer();
        Started(main, Seams(A) with { Clock = () => now, StartTimer = timer.Start, RunInBackground = _ => { } });
        ScanningViewModel stopped = main.Scanning ?? throw new InvalidOperationException("the scan did not begin");
        main.StopScanCommand.Execute(null);
        Assert.Equal(1, timer.Stops);
        StartScan(main);

        now = TimeSpan.FromSeconds(5);
        timer.Ticks[0]();

        Assert.Equal(TimeSpan.Zero, stopped.Elapsed);
        Assert.Equal(TimeSpan.Zero, main.Scanning?.Elapsed);
    }

    /// <summary>A completed scan stops its elapsed-time timer.</summary>
    [Fact]
    public void CompletedScanStopsItsTimer()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var timer = new FakeTimer();

        Started(main, Seams(A) with { StartTimer = timer.Start });

        Assert.Equal(Screen.List, main.Screen);
        Assert.Single(timer.Ticks);
        Assert.Equal(1, timer.Stops);
    }

    /// <summary>A scan cancelled while it is the running one returns to the Choose screen with the folder box kept.</summary>
    [Fact]
    public void CancelledCurrentScanGoesToChoose()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        WindowSeams seams = Seams(A) with { Scan = (_, _, _) => throw new OperationCanceledException() };

        Started(main, seams);

        Assert.Equal(Screen.Choose, main.Screen);
        Assert.Equal(folder, main.Folder);
        Assert.Empty(main.Recent);
    }

    /// <summary>A completed scan shows its candidates on the List.</summary>
    [Fact]
    public void ScanShowsTheList()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var scanned = new List<string>();
        WindowSeams seams = Seams() with
        {
            Scan = (root, _, _) =>
            {
                scanned.Add(root);
                return ReportSamples.ReportOf(A);
            },
        };

        Started(main, seams);

        Assert.Equal([folder], scanned);
        Assert.Equal(Screen.List, main.Screen);
        Assert.Single(ViewModelSamples.ListOf(main).Rows);
        Assert.Equal(ReportSamples.Root, Assert.Single(main.Recent).Folder);
    }

    /// <summary>A scan that throws shows the Failed screen with the failure's message.</summary>
    [Fact]
    public void ScanThatThrowsShowsScanFailed()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        WindowSeams seams = Seams() with { Scan = (_, _, _) => throw new IOException("boom") };

        Started(main, seams);

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
        Started(main, seams);

        ListViewModel list = ViewModelSamples.ListOf(main);
        list.TickShownCommand.Execute(null);
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
            RemovePicks = (decisions, _, _, _, _) =>
            {
                removedOnSta = onSta;
                return Recycled(decisions);
            },
        };
        Started(main, seams);

        Confirm(main);

        Assert.True(removedOnSta, "the removal did not run on the STA thread seam");
        Assert.Equal(Screen.Results, main.Screen);
    }

    /// <summary>The window's handle reaches the removal, so the Shell owns its prompts by the window.</summary>
    [Fact]
    public void RemovalPassesTheOwnerWindowToTheRemoval()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        nint seen = -1;
        WindowSeams seams = Seams(A) with
        {
            RemovePicks = (decisions, owner, _, _, _) =>
            {
                seen = owner;
                return Recycled(decisions);
            },
        };
        var workers = new WindowWorkers(main, seams) { OwnerWindow = 4242 };
        StartScan(main);

        Confirm(main);

        Assert.Equal((nint)4242, seen);
    }

    /// <summary>A removal before the window has a handle passes no owner, so nothing owns the Shell's prompts.</summary>
    [Fact]
    public void RemovalWithoutAWindowHandlePassesNoOwner()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        nint seen = -1;
        WindowSeams seams = Seams(A) with
        {
            RemovePicks = (decisions, owner, _, _, _) =>
            {
                seen = owner;
                return Recycled(decisions);
            },
        };
        var workers = new WindowWorkers(main, seams);
        StartScan(main);

        Confirm(main);

        Assert.Equal(ShellRecycler.NoOwner, seen);
    }

    /// <summary>The window shows the unlock step before the offer runs and its outcome after.</summary>
    [Fact]
    public void UnlockStartedIsShownBeforeTheOfferAndDoneAfter()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        var calls = new List<string>();
        WindowSeams seams = Seams(A) with
        {
            RemovePicks = (decisions, _, _, offerUnlock, _) =>
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
        Started(main, seams);

        Confirm(main);

        Assert.Equal(["UnlockStarted", "OfferUnlock", "UnlockDone"], calls);
        Assert.Equal("Unlock: Unlocked", main.Results?.Banner);
    }

    /// <summary>A removal that throws shows the Failed screen with the failure's message.</summary>
    [Fact]
    public void RemovalThatThrowsShowsRemovalFailed()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        WindowSeams seams = Seams(A) with { RemovePicks = (_, _, _, _, _) => throw new InvalidOperationException("boom") };
        Started(main, seams);

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
        StartScan(main);

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
        StartScan(main);
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
            RemovePicks = (decisions, _, _, offerUnlock, _) =>
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
        StartScan(main);

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
        StartScan(main);
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
        Started(main, seams);

        ListViewModel list = ViewModelSamples.ListOf(main);
        list.TickShownCommand.Execute(null);
        list.ReviewCommand.Execute(null);

        Assert.Equal(Screen.Review, main.Screen);
        Assert.NotEqual(ReviewState.Preparing, main.Review?.State);
    }

    /// <summary>A completed scan the window cannot show is shown as a failed scan.</summary>
    [Fact]
    public void ScanCompletedThatThrowsShowsScanFailed()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        WindowSeams seams = Seams() with { Scan = (_, _, _) => null! };

        Started(main, seams);

        Assert.Equal(Screen.Failed, main.Screen);
        Assert.StartsWith("Scan failed: ", main.Message, StringComparison.Ordinal);
    }

    /// <summary>A finished removal the window cannot show is shown as a failed removal.</summary>
    [Fact]
    public void FinishedThatThrowsShowsRemovalFailed()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        WindowSeams seams = Seams(A) with { RemovePicks = (_, _, _, _, _) => null! };
        Started(main, seams);

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
            RemovePicks = (decisions, _, _, offerUnlock, _) =>
            {
                offerUnlock([A.Path]);
                return Recycled(decisions);
            },
            OfferUnlock = _ => throw new InvalidOperationException("boom"),
        };
        Started(main, seams);

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
    private WindowSeams Seams(params Candidate[] candidates) =>
        new()
        {
            Scan = (_, _, _) => ReportSamples.ReportOf(candidates),
            Clock = () => TimeSpan.Zero,
            NowUnix = () => ReportSamples.Now,
            StartTimer = (_, _) => () => { },
            RecentFoldersPath = Path.Combine(folder, "recent.json"),
            ReadCapacity = _ => ViewModelSamples.Roomy,
            RemovePicks = (decisions, _, _, _, _) => Recycled(decisions),
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
        list.TickShownCommand.Execute(null);
        list.ReviewCommand.Execute(null);
        ReviewViewModel review = main.Review ?? throw new InvalidOperationException("Review was not entered");
        review.Answer(true);
        review.ConfirmCommand.Execute(null);
    }

    /// <summary>Workers for <paramref name="main"/> with <paramref name="seams"/>, scanning the test's folder.</summary>
    /// <param name="main">The window, on the Choose screen.</param>
    /// <param name="seams">What the jobs run with.</param>
    /// <returns>The workers.</returns>
    private WindowWorkers Started(MainViewModel main, WindowSeams seams)
    {
        var workers = new WindowWorkers(main, seams);
        StartScan(main);
        return workers;
    }

    /// <summary>Puts the test's folder in the folder box and presses Scan.</summary>
    /// <param name="main">The window.</param>
    private void StartScan(MainViewModel main)
    {
        main.Folder = folder;
        main.ScanCommand.Execute(null);
    }

    /// <summary>An elapsed-time timer that ticks only when the test says so, counting how often it is stopped.</summary>
    private sealed class FakeTimer
    {
        /// <summary>Gets each started timer's tick, in start order.</summary>
        public List<Action> Ticks { get; } = [];

        /// <summary>Gets how many times a timer was stopped.</summary>
        public int Stops { get; private set; }

        /// <summary>Gets what starts a timer, as <see cref="WindowSeams.StartTimer"/> does, ignoring its interval: the test ticks by hand.</summary>
        public Func<TimeSpan, Action, Action> Start =>
            (_, tick) =>
            {
                Ticks.Add(tick);
                return () => Stops++;
            };

        /// <summary>Ticks the timer started last.</summary>
        public void Tick() => Ticks[^1]();
    }

    /// <summary>A UI thread that is the calling thread, noting the window's state after every post it runs.</summary>
    /// <param name="afterPost">Notes the state.</param>
    private sealed class RecordingDispatcher(Action afterPost) : IUiDispatcher
    {
        /// <inheritdoc/>
        public void Post(Action action)
        {
            action();
            afterPost();
        }

        /// <inheritdoc/>
        public void Invoke(Action action) => action();
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
