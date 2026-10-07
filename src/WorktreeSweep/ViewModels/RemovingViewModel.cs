using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorktreeSweep.Removal;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.ViewModels;

/// <summary>
/// The Removing screen: a row per runnable decision moving from pending through removing… to done, a heading counting them, Cancel,
/// and the unlock hand-off's notice and banner. It owns the cancellation the removal watches.
/// </summary>
public sealed partial class RemovingViewModel : ObservableObject, IDisposable
{
    /// <summary>What the heading gains once Cancel is pressed.</summary>
    private const string StoppingSuffix = "  stopping after the current item…";

    /// <summary>Cancelled by <see cref="CancelCommand"/>; its token stops the removal after the current item.</summary>
    private readonly CancellationTokenSource cancellation = new();

    /// <summary>Shows the Results.</summary>
    private readonly Action<IReadOnlyList<Swept>> finished;

    /// <summary>Whether Cancel was pressed.</summary>
    private bool stopping;

    /// <summary>Whether the unlock hand-off is under way.</summary>
    private bool unlocking;

    /// <summary>Whether the removal finished.</summary>
    private bool done;

    /// <summary>Initializes a new instance of the <see cref="RemovingViewModel"/> class with every runnable decision pending.</summary>
    /// <param name="decisions">The decisions, in the order the removal goes through them.</param>
    /// <param name="paths">Each decision's path as the List shows it, relative to the scanned root, in decision order.</param>
    /// <param name="finished">Shows the Results.</param>
    /// <exception cref="ArgumentException">There is not one path per decision.</exception>
    public RemovingViewModel(IReadOnlyList<Decision> decisions, IReadOnlyList<string> paths, Action<IReadOnlyList<Swept>> finished)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(finished);
        if (paths.Count != decisions.Count)
        {
            throw new ArgumentException($"expected one path per decision: {decisions.Count} decisions, {paths.Count} paths", nameof(paths));
        }
        this.finished = finished;
        Decisions = decisions;
        Rows =
        [
            .. decisions
                .Select((decision, index) => (Decision: decision, Index: index))
                .Where(entry => entry.Decision.Plan is Plan.Run)
                .Select(entry => new RemovalRowViewModel(entry.Index, paths[entry.Index])),
        ];
        Heading = HeadingText();
    }

    /// <summary>Gets the decisions to pass to <see cref="Remover.RemovePicks"/>.</summary>
    public IReadOnlyList<Decision> Decisions { get; }

    /// <summary>Gets one row per runnable decision, in decision order.</summary>
    public IReadOnlyList<RemovalRowViewModel> Rows { get; }

    /// <summary>Gets the token to pass to <see cref="Remover.RemovePicks"/>; <see cref="CancelCommand"/> cancels it.</summary>
    public CancellationToken Token => cancellation.Token;

    /// <summary>Gets the heading: <c>Removing {done}/{runnable}</c>, and after Cancel that the removal stops after the current item.</summary>
    [ObservableProperty]
    public partial string Heading { get; private set; }

    /// <summary>Gets the notice shown while the unlock hand-off runs in the terminal; <see langword="null"/> otherwise.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>Gets the unlock hand-off's outcome, <c>Unlock: {outcome}</c>; <see langword="null"/> when there was none.</summary>
    [ObservableProperty]
    public partial string? Banner { get; private set; }

    /// <summary>Moves the row of the decision <paramref name="progress"/> names; progress for a decision with no row is ignored.</summary>
    /// <param name="progress">Which decision started or finished.</param>
    public void OnProgress(Progress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        (int Index, string Status) step = progress switch
        {
            Progress.Started started => (started.Index, RemovalRowViewModel.Removing),
            Progress.Done finishedOne => (finishedOne.Index, RemovalRowViewModel.Done),
            _ => throw new UnreachableException($"unknown progress {progress}"),
        };
        if (Rows.FirstOrDefault(row => row.Index == step.Index) is not { } moved)
        {
            return;
        }
        moved.Status = step.Status;
        Heading = HeadingText();
    }

    /// <summary>Shows that <paramref name="pickCount"/> picks are locked and the unlock step runs in the terminal; disables Cancel.</summary>
    /// <param name="pickCount">How many picks are locked.</param>
    public void UnlockStarted(int pickCount)
    {
        Notice =
            pickCount == 1
                ? "1 pick locked; finish the unlock step in the terminal."
                : string.Create(CultureInfo.InvariantCulture, $"{pickCount} picks locked; finish the unlock step in the terminal.");
        unlocking = true;
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Shows the unlock step's outcome as the banner, clears the notice and enables Cancel again.</summary>
    /// <param name="outcome">What the unlock step did.</param>
    public void UnlockDone(UnlockOutcome outcome)
    {
        Banner = "Unlock: " + UnlockLabel(outcome);
        Notice = null;
        unlocking = false;
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Ends the removal and shows the Results.</summary>
    /// <param name="swept">What happened to each pick.</param>
    public void Finished(IReadOnlyList<Swept> swept)
    {
        ArgumentNullException.ThrowIfNull(swept);
        done = true;
        CancelCommand.NotifyCanExecuteChanged();
        finished(swept);
    }

    /// <inheritdoc/>
    public void Dispose() => cancellation.Dispose();

    /// <summary>The banner's word for an unlock outcome.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The word.</returns>
    private static string UnlockLabel(UnlockOutcome outcome) =>
        outcome switch
        {
            UnlockOutcome.Unlocked => "Unlocked",
            UnlockOutcome.PartlyUnlocked => "Partly unlocked",
            UnlockOutcome.StillLocked => "Still locked",
            UnlockOutcome.Skipped => "Skipped",
            _ => throw new UnreachableException($"unknown unlock outcome {outcome}"),
        };

    /// <summary>Stops the removal after the current item; once only.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (!CanCancel())
        {
            return;
        }
        stopping = true;
        cancellation.Cancel();
        Heading = HeadingText();
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Whether Cancel can be pressed: not yet pressed, no unlock hand-off under way, and the removal not finished.</summary>
    /// <returns><see langword="true"/> when it can.</returns>
    private bool CanCancel() => !stopping && !unlocking && !done;

    /// <summary>The heading for the rows as they are now.</summary>
    /// <returns>The heading.</returns>
    private string HeadingText()
    {
        int finishedRows = Rows.Count(row => row.Status == RemovalRowViewModel.Done);
        string heading = string.Create(CultureInfo.InvariantCulture, $"Removing {finishedRows}/{Rows.Count}");
        return stopping ? heading + StoppingSuffix : heading;
    }
}
