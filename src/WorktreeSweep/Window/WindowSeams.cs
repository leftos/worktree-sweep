using System.Diagnostics;
using System.IO;
using WorktreeSweep.Recycle;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Scan;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Window;

/// <summary>What <see cref="WindowWorkers"/> scans, reads, removes and unlocks with, and the threads it runs each job on.</summary>
public sealed record WindowSeams
{
    /// <summary>Gets the scan of a root folder, with the signature of <see cref="Scanner.Scan"/>.</summary>
    public required Func<string, Action<ScanProgress>, CancellationToken, ScanReport> Scan { get; init; }

    /// <summary>Gets a monotonic clock: the time since a fixed start, for the scan's elapsed time and its progress throttle.</summary>
    public required Func<TimeSpan> Clock { get; init; }

    /// <summary>Gets the present, in Unix seconds (UTC): when a scan completed, and what recent folders' ages are measured from.</summary>
    public required Func<long> NowUnix { get; init; }

    /// <summary>
    /// Gets what starts a repeating timer: given the interval and the tick, it runs the tick on the UI thread every interval and
    /// returns what stops it.
    /// </summary>
    public required Func<TimeSpan, Action, Action> StartTimer { get; init; }

    /// <summary>Gets the file the recent folders are kept in.</summary>
    public required string RecentFoldersPath { get; init; }

    /// <summary>Gets the read of the Recycle Bin settings of the volume a path is on; <see langword="null"/> when unknown.</summary>
    public required Func<string, BinCapacity?> ReadCapacity { get; init; }

    /// <summary>Gets the removal, with the signature of <see cref="Remover.RemovePicks"/>.</summary>
    public required Func<
        IReadOnlyList<Decision>,
        nint,
        Action<Progress>,
        Func<IReadOnlyList<string>, UnlockOutcome>,
        CancellationToken,
        IReadOnlyList<Swept>
    > RemovePicks { get; init; }

    /// <summary>Gets the unlock step for the locked paths, run in the terminal the tool was launched from.</summary>
    public required Func<IReadOnlyList<string>, UnlockOutcome> OfferUnlock { get; init; }

    /// <summary>Gets what starts a job off the UI thread.</summary>
    public required Action<Action> RunInBackground { get; init; }

    /// <summary>Gets what starts a job on its own STA thread.</summary>
    public required Action<Action> RunOnStaThread { get; init; }

    /// <summary>Gets the window's UI thread.</summary>
    public required IUiDispatcher Dispatcher { get; init; }

    /// <summary>The real scan, capacity read, removal and unlock step, on real threads.</summary>
    /// <param name="dispatcher">The window's UI thread.</param>
    /// <returns>The seams.</returns>
    public static WindowSeams Production(IUiDispatcher dispatcher)
    {
        long start = Stopwatch.GetTimestamp();
        return new()
        {
            Scan = Scanner.Scan,
            Clock = () => Stopwatch.GetElapsedTime(start),
            NowUnix = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            StartTimer = (interval, tick) => StartPostingTimer(dispatcher, interval, tick),
            RecentFoldersPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "worktree-sweep",
                "recent.json"
            ),
            ReadCapacity = RemovalPlanner.ReadCapacity,
            RemovePicks = Remover.RemovePicks,
            OfferUnlock = paths => new UnlockOffer(Console.In, Console.Out, new SudoRunner(), SweepProcess.Current()).Offer(paths),
            RunInBackground = action => new Thread(new ThreadStart(action)) { IsBackground = true }.Start(),
            RunOnStaThread = StartStaThread,
            Dispatcher = dispatcher,
        };
    }

    /// <summary>Starts a timer that posts <paramref name="tick"/> to <paramref name="dispatcher"/> every <paramref name="interval"/>.</summary>
    /// <param name="dispatcher">The window's UI thread.</param>
    /// <param name="interval">The time between ticks.</param>
    /// <param name="tick">What each tick runs on the UI thread.</param>
    /// <returns>What stops the timer.</returns>
    private static Action StartPostingTimer(IUiDispatcher dispatcher, TimeSpan interval, Action tick)
    {
        var timer = new Timer(_ => dispatcher.Post(tick), null, interval, interval);
        return timer.Dispose;
    }

    /// <summary>Starts <paramref name="action"/> on a new foreground STA thread.</summary>
    /// <param name="action">The job.</param>
    private static void StartStaThread(Action action)
    {
        var thread = new Thread(new ThreadStart(action));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
