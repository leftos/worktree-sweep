using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.ViewModels;

/// <summary>
/// The Removing screen: a row per runnable decision moving from pending through removing… to done, a progress bar and its byte line,
/// a heading counting them, Stop, and the unlock hand-off's notice and banner. It owns the cancellation the removal watches.
/// </summary>
public sealed partial class RemovingViewModel : ObservableObject, IDisposable
{
    /// <summary>What the heading gains once Stop is pressed.</summary>
    private const string StoppingSuffix = "  stopping after the current item…";

    /// <summary>Cancelled by <see cref="CancelCommand"/>; its token stops the removal after the current item.</summary>
    private readonly CancellationTokenSource cancellation = new();

    /// <summary>Shows the Results.</summary>
    private readonly Action<IReadOnlyList<Swept>> finished;

    /// <summary>Whether every runnable pick is a recycle, which is what puts its bytes in the Recycle Bin.</summary>
    private readonly bool everyPickRecycles;

    /// <summary>Whether Stop was pressed.</summary>
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
                .Select(entry => new RemovalRowViewModel(entry.Index, paths[entry.Index], entry.Decision)),
        ];
        Total = Rows.Count;
        BytesTotal = Rows.Sum(row => row.Size ?? 0);
        everyPickRecycles = Rows.All(row => row.Action is RemoveAction.Delete { Method: DeleteMethod.Recycle });
        Heading = HeadingText();
    }

    /// <summary>Gets the decisions to pass to <see cref="Remover.RemovePicks"/>.</summary>
    public IReadOnlyList<Decision> Decisions { get; }

    /// <summary>Gets one row per runnable decision, in decision order.</summary>
    public IReadOnlyList<RemovalRowViewModel> Rows { get; }

    /// <summary>Gets the token to pass to <see cref="Remover.RemovePicks"/>; <see cref="CancelCommand"/> cancels it.</summary>
    public CancellationToken Token => cancellation.Token;

    /// <summary>Gets how many picks the progress bar counts: the runnable decisions.</summary>
    public int Total { get; }

    /// <summary>Gets how many of them are finished, removed or failed.</summary>
    public int Done => Rows.Count(row => row.Status == RemovalRowViewModel.Done);

    /// <summary>Gets the size of every runnable pick; a size the scan did not read counts as no bytes at all.</summary>
    public long BytesTotal { get; }

    /// <summary>Gets the size of the runnable picks removed so far; a failed pick and an unknown size add nothing.</summary>
    public long BytesDone => Rows.Where(row => row.Succeeded).Sum(row => row.Size ?? 0);

    /// <summary>
    /// Gets the line under the bar: the bytes done of the bytes total, each <c>at least …</c> when a size it counts was read only in
    /// part, and where they went; empty when no size is known.
    /// </summary>
    public string BytesLine
    {
        get
        {
            if (BytesTotal == 0)
            {
                return "";
            }
            string where = everyPickRecycles ? "moved to the Recycle Bin" : "removed";
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{ReportTable.SizeText(BytesDone, DoneIsPartial)} of {ReportTable.SizeText(BytesTotal, TotalIsPartial)} {where}"
            );
        }
    }

    /// <summary>Gets the heading: <c>Removing {done} of {total}</c>, and after Stop that the removal stops after the current item.</summary>
    [ObservableProperty]
    public partial string Heading { get; private set; }

    /// <summary>Gets the notice shown while the unlock hand-off runs in the terminal; <see langword="null"/> otherwise.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>Gets the unlock hand-off's outcome, <c>Unlock: {outcome}</c>; <see langword="null"/> when there was none.</summary>
    [ObservableProperty]
    public partial string? Banner { get; private set; }

    /// <summary>Whether a size the total counts was read only in part, which makes that total a lower bound.</summary>
    private bool TotalIsPartial => Rows.Any(row => row.SizeIsPartial);

    /// <summary>Whether a size the removed picks count was read only in part, which makes their bytes a lower bound.</summary>
    private bool DoneIsPartial => Rows.Any(row => row.Succeeded && row.SizeIsPartial);

    /// <summary>Moves the row of the decision <paramref name="progress"/> names; progress for a decision with no row is ignored.</summary>
    /// <param name="progress">Which decision started or finished, with what became of it.</param>
    public void OnProgress(Progress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        (int Index, Outcome? Result) step = progress switch
        {
            Progress.Started started => (started.Index, null),
            Progress.Done finishedOne => (finishedOne.Index, finishedOne.Outcome),
            _ => throw new UnreachableException($"unknown progress {progress}"),
        };
        if (Rows.FirstOrDefault(row => row.Index == step.Index) is not { } moved)
        {
            return;
        }
        if (step.Result is { } result)
        {
            moved.Removed(result);
        }
        else
        {
            moved.Started();
        }
        Heading = HeadingText();
        OnPropertyChanged(nameof(Done));
        OnPropertyChanged(nameof(BytesDone));
        OnPropertyChanged(nameof(BytesLine));
    }

    /// <summary>Shows that <paramref name="pickCount"/> picks are locked and the unlock step runs in the terminal; disables Stop.</summary>
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

    /// <summary>Shows the unlock step's outcome as the banner, clears the notice and enables Stop again.</summary>
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

    /// <summary>Whether Stop can be pressed: not yet pressed, no unlock hand-off under way, and the removal not finished.</summary>
    /// <returns><see langword="true"/> when it can.</returns>
    private bool CanCancel() => !stopping && !unlocking && !done;

    /// <summary>The heading for the rows as they are now.</summary>
    /// <returns>The heading.</returns>
    private string HeadingText()
    {
        string heading = string.Create(CultureInfo.InvariantCulture, $"Removing {Done} of {Total}");
        return stopping ? heading + StoppingSuffix : heading;
    }
}
