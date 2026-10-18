using System.ComponentModel;
using System.Diagnostics;
using WorktreeSweep.Recycle;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Scan;
using WorktreeSweep.Unlock;
using WorktreeSweep.ViewModels;

namespace WorktreeSweep.Window;

/// <summary>
/// Runs the window's background jobs: one scan at a time off the UI thread, with its progress, cancellation and recent-folder record;
/// the Recycle Bin capacity reads when Review is entered; and the removal on its own STA thread with the unlock step on that thread.
/// Decides whether the window may close.
/// </summary>
public sealed class WindowWorkers
{
    /// <summary>The longest a scan's progress waits before it is posted to the window: one post per interval at most.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How often the Scanning screen's elapsed time advances.</summary>
    private static readonly TimeSpan ElapsedInterval = TimeSpan.FromSeconds(1);

    /// <summary>The window's view model.</summary>
    private readonly MainViewModel main;

    /// <summary>What the jobs run with.</summary>
    private readonly WindowSeams seams;

    /// <summary>The recent folders, which every completed scan is recorded in.</summary>
    private readonly RecentFolders recent;

    /// <summary>The running scan, the only one whose posts are shown; <see langword="null"/> when none runs. UI thread only.</summary>
    private ScanRun? current;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowWorkers"/> class, which loads the recent folders onto the window and starts
    /// each job as its screen is entered.
    /// </summary>
    /// <param name="main">The window's view model.</param>
    /// <param name="seams">What the jobs run with.</param>
    public WindowWorkers(MainViewModel main, WindowSeams seams)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(seams);
        this.main = main;
        this.seams = seams;
        recent = RecentFolders.Load(seams.RecentFoldersPath);
        main.ShowRecent(recent.Entries, seams.NowUnix());
        main.PropertyChanged += OnMainChanged;
    }

    /// <summary>
    /// Gets or sets the window that owns the Shell's prompts during removal; <see cref="ShellRecycler.NoOwner"/> until the window has
    /// a handle.
    /// </summary>
    public nint OwnerWindow { get; set; } = ShellRecycler.NoOwner;

    /// <summary>
    /// Gets a task that completes once the cancel of the scan the close stopped has returned, its git process trees killed; it is
    /// complete when the close stopped no scan. The process waits for it after the window closes, so it does not exit mid-kill.
    /// </summary>
    public Task CloseCancelled { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Decides whether the window may close now: not while removing, where it presses Cancel instead, which stops the removal after
    /// the current item and does nothing while the unlock step runs; on every other screen it may, and a running scan is cancelled
    /// off the UI thread, with <see cref="CloseCancelled"/> completing when that cancel returns.
    /// </summary>
    /// <returns><see langword="true"/> when the window may close.</returns>
    public bool RequestClose()
    {
        if (main.Screen != Screen.Removing)
        {
            if (current is { } run)
            {
                var cancelled = new TaskCompletionSource();
                CloseCancelled = cancelled.Task;
                EndScan(run);
                seams.RunInBackground(() =>
                {
                    try
                    {
                        run.Cancel();
                    }
                    finally
                    {
                        cancelled.SetResult();
                    }
                });
            }
            return true;
        }
        main.Removing?.CancelCommand.Execute(null);
        return false;
    }

    /// <summary>
    /// Wraps a window callback so that what it throws is shown on the window by <paramref name="fail"/>, with the exception's message,
    /// instead of ending the UI thread.
    /// </summary>
    /// <param name="action">The callback.</param>
    /// <param name="fail">Shows the failure's message.</param>
    /// <returns>The wrapped callback.</returns>
    private static Action Guarded(Action action, Action<string> fail) =>
        () =>
        {
            try
            {
                action();
            }
#pragma warning disable CA1031 // A window callback's failure must reach the window as a screen, not end the process.
            catch (Exception error)
#pragma warning restore CA1031
            {
                fail(error.Message);
            }
        };

    /// <summary>Shows the Failed screen for a scan that failed.</summary>
    /// <param name="message">Why.</param>
    private void ScanFailure(string message) => main.ScanFailed($"Scan failed: {message}");

    /// <summary>Shows the Failed screen for capacities that could not be shown.</summary>
    /// <param name="message">Why.</param>
    private void CapacityFailure(string message) => main.RemovalFailed($"Reading the Recycle Bin sizes failed: {message}");

    /// <summary>Shows the Failed screen for a removal that failed.</summary>
    /// <param name="message">Why.</param>
    private void RemovalFailure(string message) => main.RemovalFailed($"Removal failed: {message}");

    /// <summary>
    /// Starts the scan <paramref name="scanning"/> shows, in the background, after cancelling any scan still running; every second its
    /// elapsed time advances and progress held back by the throttle is posted.
    /// </summary>
    /// <param name="scanning">The Scanning screen just entered.</param>
    private void BeginScan(ScanningViewModel scanning)
    {
        CancelScan();
#pragma warning disable CA2000 // The source is cancelled off the UI thread while the scan may still use its token; it holds no timer or handle.
        var source = new CancellationTokenSource();
#pragma warning restore CA2000
        var run = new ScanRun(scanning, source.Cancel, seams.Clock(), source.Token);
        current = run;
        run.StopTimer = seams.StartTimer(
            ElapsedInterval,
            () =>
            {
                if (IsCurrent(run))
                {
                    scanning.Elapsed = seams.Clock() - run.Started;
                    PostPendingProgress(run);
                }
            }
        );
        seams.RunInBackground(() => Scan(run));
    }

    /// <summary>
    /// Cancels the running scan, if any, so that nothing it posts is shown; the cancel runs off the UI thread, since it kills the
    /// scan's git processes, and is not waited for.
    /// </summary>
    private void CancelScan()
    {
        if (current is not { } run)
        {
            return;
        }
        EndScan(run);
        seams.RunInBackground(run.Cancel);
    }

    /// <summary>Stops the scan's timer and makes it no longer the running scan, so its later posts are dropped.</summary>
    /// <param name="run">The running scan.</param>
    private void EndScan(ScanRun run)
    {
        current = null;
        run.StopTimer();
    }

    /// <summary>Whether <paramref name="run"/> is the running scan, the only one whose posts are shown.</summary>
    /// <param name="run">A scan.</param>
    /// <returns><see langword="true"/> when no scan has begun since it and it was not ended.</returns>
    private bool IsCurrent(ScanRun run) => ReferenceEquals(current, run);

    /// <summary>Runs the scan and posts its last progress and its report, its cancellation or its failure, to the window.</summary>
    /// <param name="run">The scan.</param>
    private void Scan(ScanRun run)
    {
        Action show;
        try
        {
            ScanReport report = seams.Scan(run.Scanning.Root, progress => OnProgress(run, progress), run.Token);
            PostPendingProgress(run);
            long nowUnix = seams.NowUnix();
            TimeSpan took = seams.Clock() - run.Started;
            show = () => ScanCompleted(run, report, nowUnix, took);
        }
        catch (OperationCanceledException)
        {
            show = () => ScanStopped(run);
        }
#pragma warning disable CA1031 // A background job's failure must reach the window as a screen, not end the process.
        catch (Exception error)
#pragma warning restore CA1031
        {
            string message = error.Message;
            show = () => ScanFailed(run, message);
        }
        seams.Dispatcher.Post(Guarded(show, ScanFailure));
    }

    /// <summary>
    /// Adds a progress event to the scan's tally and posts the tally when <see cref="ProgressInterval"/> has passed since the last
    /// post; otherwise the post waits for a later event or the scan's end. Runs on the scan's threads.
    /// </summary>
    /// <param name="run">The scan.</param>
    /// <param name="progress">The event.</param>
    private void OnProgress(ScanRun run, ScanProgress progress)
    {
        lock (run.Gate)
        {
            run.Tally.Apply(progress);
            TimeSpan now = seams.Clock();
            if (run.LastPost is { } last && now - last < ProgressInterval)
            {
                run.Pending = true;
                return;
            }
            run.LastPost = now;
            run.Pending = false;
            PostProgress(run, run.Tally.Snapshot());
        }
    }

    /// <summary>
    /// Posts the scan's tally if an event since the last post is not yet shown: on each elapsed-time tick and when the scan ends.
    /// </summary>
    /// <param name="run">The scan.</param>
    private void PostPendingProgress(ScanRun run)
    {
        lock (run.Gate)
        {
            if (run.Pending)
            {
                run.Pending = false;
                PostProgress(run, run.Tally.Snapshot());
            }
        }
    }

    /// <summary>Posts <paramref name="snapshot"/> to the Scanning screen, shown only while the scan is the running one.</summary>
    /// <param name="run">The scan.</param>
    /// <param name="snapshot">Its tally.</param>
    private void PostProgress(ScanRun run, ScanSnapshot snapshot) =>
        seams.Dispatcher.Post(
            Guarded(
                () =>
                {
                    if (IsCurrent(run))
                    {
                        main.ScanProgressed(snapshot);
                    }
                },
                ScanFailure
            )
        );

    /// <summary>Shows the running scan's report and records its folder in the recent folders; a stale scan's report is dropped.</summary>
    /// <param name="run">The scan.</param>
    /// <param name="report">What it found.</param>
    /// <param name="nowUnix">When it completed, in Unix seconds.</param>
    /// <param name="took">How long it ran.</param>
    private void ScanCompleted(ScanRun run, ScanReport report, long nowUnix, TimeSpan took)
    {
        if (!IsCurrent(run))
        {
            return;
        }
        EndScan(run);
        run.Scanning.Elapsed = took;
        main.ScanCompleted(report, nowUnix);
        long bytes = report.Candidates.Sum(candidate => candidate.KnownSize?.Bytes ?? 0);
        recent.Record(new RecentFolder(report.Root, nowUnix, report.Candidates.Count, bytes));
        main.ShowRecent(recent.Entries, nowUnix);
    }

    /// <summary>Returns to the Choose screen after the running scan was cancelled; a stale scan's cancellation is dropped.</summary>
    /// <param name="run">The scan.</param>
    private void ScanStopped(ScanRun run)
    {
        if (!IsCurrent(run))
        {
            return;
        }
        EndScan(run);
        main.ScanStopped();
    }

    /// <summary>Shows why the running scan failed; a stale scan's failure is dropped.</summary>
    /// <param name="run">The scan.</param>
    /// <param name="message">Why.</param>
    private void ScanFailed(ScanRun run, string message)
    {
        if (!IsCurrent(run))
        {
            return;
        }
        EndScan(run);
        ScanFailure(message);
    }

    /// <summary>
    /// Starts the job of the screen just entered: the scan on Scanning, the capacity reads on Review and the removal on Removing. A
    /// scan still running when its screen is left, by Stop scan, is cancelled; the recent folders' ages are measured again on Choose.
    /// </summary>
    /// <param name="sender">The window's view model.</param>
    /// <param name="e">Which property changed.</param>
    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Screen))
        {
            return;
        }
        if (main.Screen == Screen.Scanning && main.Scanning is { } scanning)
        {
            BeginScan(scanning);
        }
        else
        {
            CancelScan();
        }
        if (main.Screen == Screen.Choose)
        {
            main.ShowRecent(recent.Entries, seams.NowUnix());
        }
        if (main.Screen == Screen.Review && main.Review is { State: ReviewState.Preparing } review)
        {
            ReadCapacities(review);
        }
        else if (main.Screen == Screen.Removing && main.Removing is { } removing)
        {
            StartRemoval(removing);
        }
    }

    /// <summary>Reads the capacity of every path <paramref name="review"/> asks for in the background, then posts them to it.</summary>
    /// <param name="review">The review, Preparing.</param>
    private void ReadCapacities(ReviewViewModel review)
    {
        IReadOnlyList<string> paths = review.CapacityPaths;
        seams.RunInBackground(() =>
        {
            var capacities = new Dictionary<string, BinCapacity?>();
            foreach (string path in paths)
            {
                capacities[path] = ReadCapacity(path);
            }
            seams.Dispatcher.Post(Guarded(() => review.CapacitiesRead(capacities), CapacityFailure));
        });
    }

    /// <summary>The Recycle Bin settings of the volume <paramref name="path"/> is on; <see langword="null"/>, with a warning, when the
    /// read throws.</summary>
    /// <param name="path">A path on the volume.</param>
    /// <returns>The settings, or <see langword="null"/>.</returns>
    private BinCapacity? ReadCapacity(string path)
    {
        try
        {
            return seams.ReadCapacity(path);
        }
#pragma warning disable CA1031 // A capacity that cannot be read is unknown, as RemovalPlanner.ReadCapacity counts an I/O failure.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"cannot read the Recycle Bin size for {path}: {error.Message}");
            return null;
        }
    }

    /// <summary>Starts <paramref name="removing"/>'s removal on its own STA thread, reading the owner window on this UI thread.</summary>
    /// <param name="removing">The Removing screen, captured once so every callback reaches this removal.</param>
    private void StartRemoval(RemovingViewModel removing)
    {
        CancellationToken token = removing.Token;
        nint owner = OwnerWindow;
        seams.RunOnStaThread(() => Remove(removing, owner, token));
    }

    /// <summary>Runs the removal and posts its results, or its failure, to the window.</summary>
    /// <param name="removing">The Removing screen.</param>
    /// <param name="owner">The window that owns the Shell's prompts.</param>
    /// <param name="token">The removal's cancellation.</param>
    private void Remove(RemovingViewModel removing, nint owner, CancellationToken token)
    {
        Action show;
        try
        {
            IReadOnlyList<Swept> swept = seams.RemovePicks(
                removing.Decisions,
                owner,
                progress => seams.Dispatcher.Post(Guarded(() => removing.OnProgress(progress), RemovalFailure)),
                paths => OfferUnlock(removing, paths),
                token
            );
            show = () => removing.Finished(swept);
        }
#pragma warning disable CA1031 // A background job's failure must reach the window as a screen, not end the process.
        catch (Exception error)
#pragma warning restore CA1031
        {
            string message = error.Message;
            show = () => RemovalFailure(message);
        }
        seams.Dispatcher.Post(Guarded(show, RemovalFailure));
    }

    /// <summary>
    /// Shows that the unlock step runs in the terminal, runs it on this thread, then shows its outcome. An offer that throws is shown as
    /// <see cref="UnlockOutcome.Skipped"/>, as the removal counts it, and the exception goes on to the removal.
    /// </summary>
    /// <param name="removing">The Removing screen.</param>
    /// <param name="paths">The locked paths.</param>
    /// <returns>What the unlock step did.</returns>
    private UnlockOutcome OfferUnlock(RemovingViewModel removing, IReadOnlyList<string> paths)
    {
        seams.Dispatcher.Invoke(Guarded(() => removing.UnlockStarted(paths.Count), RemovalFailure));
        UnlockOutcome outcome = UnlockOutcome.Skipped;
        try
        {
            outcome = seams.OfferUnlock(paths);
            return outcome;
        }
        finally
        {
            seams.Dispatcher.Invoke(Guarded(() => removing.UnlockDone(outcome), RemovalFailure));
        }
    }

    /// <summary>One scan: its screen, its cancellation, its timer and its progress tally.</summary>
    /// <param name="scanning">Its Scanning screen.</param>
    /// <param name="cancel">Cancels it.</param>
    /// <param name="started">When it began, on <see cref="WindowSeams.Clock"/>.</param>
    /// <param name="token">Its cancellation.</param>
    private sealed class ScanRun(ScanningViewModel scanning, Action cancel, TimeSpan started, CancellationToken token)
    {
        /// <summary>Gets its Scanning screen.</summary>
        public ScanningViewModel Scanning { get; } = scanning;

        /// <summary>Gets its cancellation.</summary>
        public CancellationToken Token { get; } = token;

        /// <summary>Gets what cancels it.</summary>
        public Action Cancel { get; } = cancel;

        /// <summary>Gets when it began, on <see cref="WindowSeams.Clock"/>.</summary>
        public TimeSpan Started { get; } = started;

        /// <summary>Gets or sets what stops its elapsed-time timer.</summary>
        public Action StopTimer { get; set; } = () => { };

        /// <summary>Gets the lock its progress events are added under.</summary>
        public Lock Gate { get; } = new();

        /// <summary>Gets its progress so far.</summary>
        public ScanTally Tally { get; } = new();

        /// <summary>Gets or sets when its progress was last posted; <see langword="null"/> before the first post.</summary>
        public TimeSpan? LastPost { get; set; }

        /// <summary>Gets or sets a value indicating whether an event since the last post is not yet posted.</summary>
        public bool Pending { get; set; }
    }
}
