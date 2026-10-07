using WorktreeSweep.Recycle;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Scan;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Window;

/// <summary>What <see cref="WindowWorkers"/> scans, reads, removes and unlocks with, and the threads it runs each job on.</summary>
public sealed record WindowSeams
{
    /// <summary>Gets the scan of a root folder.</summary>
    public required Func<string, ScanReport> Scan { get; init; }

    /// <summary>Gets the read of the Recycle Bin settings of the volume a path is on; <see langword="null"/> when unknown.</summary>
    public required Func<string, BinCapacity?> ReadCapacity { get; init; }

    /// <summary>Gets the removal, with the signature of <see cref="Remover.RemovePicks"/>.</summary>
    public required Func<
        IReadOnlyList<Decision>,
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
    public static WindowSeams Production(IUiDispatcher dispatcher) =>
        new()
        {
            Scan = Scanner.Scan,
            ReadCapacity = RemovalPlanner.ReadCapacity,
            RemovePicks = Remover.RemovePicks,
            OfferUnlock = paths => new UnlockOffer(Console.In, Console.Out, new SudoRunner(), SweepProcess.Current()).Offer(paths),
            RunInBackground = action => new Thread(new ThreadStart(action)) { IsBackground = true }.Start(),
            RunOnStaThread = StartStaThread,
            Dispatcher = dispatcher,
        };

    /// <summary>Starts <paramref name="action"/> on a new foreground STA thread.</summary>
    /// <param name="action">The job.</param>
    private static void StartStaThread(Action action)
    {
        var thread = new Thread(new ThreadStart(action));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
