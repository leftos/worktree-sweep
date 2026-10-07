using System.ComponentModel;
using System.Diagnostics;
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

    /// <summary>
    /// Gets or sets the window that owns the Shell's prompts during removal; <see cref="ShellRecycler.NoOwner"/> until the window has
    /// a handle.
    /// </summary>
    public nint OwnerWindow { get; set; } = ShellRecycler.NoOwner;

    /// <summary>Scans <paramref name="root"/> in the background, then shows what it found or why it failed.</summary>
    /// <param name="root">The folder to scan.</param>
    public void Start(string root) => seams.RunInBackground(() => Scan(root));

    /// <summary>
    /// Decides whether the window may close now: not while removing, where it presses Cancel instead, which stops the removal after
    /// the current item and does nothing while the unlock step runs; on every other screen it may.
    /// </summary>
    /// <returns><see langword="true"/> when the window may close.</returns>
    public bool RequestClose()
    {
        if (main.Screen != Screen.Removing)
        {
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
            string message = error.Message;
            show = () => ScanFailure(message);
        }
        seams.Dispatcher.Post(Guarded(show, ScanFailure));
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
}
