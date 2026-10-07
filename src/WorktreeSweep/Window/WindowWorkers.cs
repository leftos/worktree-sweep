using System.ComponentModel;
using WorktreeSweep.Recycle;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Unlock;
using WorktreeSweep.ViewModels;

namespace WorktreeSweep.Window;

/// <summary>
/// Runs the window's background jobs: the scan off the UI thread, the Recycle Bin capacity reads when Review is entered, and the
/// removal on its own STA thread with the unlock step on that thread. Decides whether the window may close.
/// </summary>
public sealed class WindowWorkers
{
    /// <summary>The window's view model.</summary>
    private readonly MainViewModel main;

    /// <summary>What the jobs run with.</summary>
    private readonly WindowSeams seams;

    /// <summary>Whether the unlock step runs in the terminal; read and written on the UI thread only.</summary>
    private bool unlocking;

    /// <summary>Initializes a new instance of the <see cref="WindowWorkers"/> class, which starts each job as its screen is entered.</summary>
    /// <param name="main">The window's view model.</param>
    /// <param name="seams">What the jobs run with.</param>
    public WindowWorkers(MainViewModel main, WindowSeams seams)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(seams);
        this.main = main;
        this.seams = seams;
        main.PropertyChanged += OnMainChanged;
    }

    /// <summary>Scans <paramref name="root"/> in the background, then shows what it found or why it failed.</summary>
    /// <param name="root">The folder to scan.</param>
    public void Start(string root) => seams.RunInBackground(() => Scan(root));

    /// <summary>
    /// Decides whether the window may close now: not while the unlock step runs, and not while removing, where it cancels the removal
    /// after the current item instead; on every other screen it may.
    /// </summary>
    /// <returns><see langword="true"/> when the window may close.</returns>
    public bool RequestClose()
    {
        if (main.Screen != Screen.Removing)
        {
            return true;
        }
        if (!unlocking)
        {
            main.Removing?.CancelCommand.Execute(null);
        }
        return false;
    }

    /// <summary>Runs the scan and posts its report, or its failure, to the window.</summary>
    /// <param name="root">The folder to scan.</param>
    private void Scan(string root)
    {
        Action show;
        try
        {
            ScanReport report = seams.Scan(root);
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            show = () => main.ScanCompleted(report, nowUnix);
        }
#pragma warning disable CA1031 // A background job's failure must reach the window as a screen, not end the process.
        catch (Exception error)
#pragma warning restore CA1031
        {
            string message = $"Scan failed: {error.Message}";
            show = () => main.ScanFailed(message);
        }
        seams.Dispatcher.Post(show);
    }

    /// <summary>Starts the job of the screen just entered.</summary>
    /// <param name="sender">The window's view model.</param>
    /// <param name="e">Which property changed.</param>
    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Screen))
        {
            return;
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
                capacities[path] = seams.ReadCapacity(path);
            }
            seams.Dispatcher.Post(() => review.CapacitiesRead(capacities));
        });
    }

    /// <summary>Starts <paramref name="removing"/>'s removal on its own STA thread.</summary>
    /// <param name="removing">The Removing screen, captured once so every callback reaches this removal.</param>
    private void StartRemoval(RemovingViewModel removing)
    {
        CancellationToken token = removing.Token;
        seams.RunOnStaThread(() => Remove(removing, token));
    }

    /// <summary>Runs the removal and posts its results, or its failure, to the window.</summary>
    /// <param name="removing">The Removing screen.</param>
    /// <param name="token">The removal's cancellation.</param>
    private void Remove(RemovingViewModel removing, CancellationToken token)
    {
        Action show;
        try
        {
            IReadOnlyList<Swept> swept = seams.RemovePicks(
                removing.Decisions,
                progress => seams.Dispatcher.Post(() => removing.OnProgress(progress)),
                paths => OfferUnlock(removing, paths),
                token
            );
            show = () => removing.Finished(swept);
        }
#pragma warning disable CA1031 // A background job's failure must reach the window as a screen, not end the process.
        catch (Exception error)
#pragma warning restore CA1031
        {
            string message = $"Removal failed: {error.Message}";
            show = () => main.RemovalFailed(message);
        }
        seams.Dispatcher.Post(show);
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
        seams.Dispatcher.Invoke(() =>
        {
            unlocking = true;
            removing.UnlockStarted(paths.Count);
        });
        UnlockOutcome outcome = UnlockOutcome.Skipped;
        try
        {
            outcome = seams.OfferUnlock(paths);
            return outcome;
        }
        finally
        {
            seams.Dispatcher.Invoke(() =>
            {
                unlocking = false;
                removing.UnlockDone(outcome);
            });
        }
    }
}
