using CommunityToolkit.Mvvm.ComponentModel;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;

namespace WorktreeSweep.ViewModels;

/// <summary>
/// The window: which screen it shows, the scanned root, and each screen's view model once reached. Every change is a method call
/// on the UI thread; the screens' view models call back here to move between screens.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>The local time zone's offset from UTC at a Unix time.</summary>
    private readonly Func<long, TimeSpan> utcOffset;

    /// <summary>The completed scan; <see langword="null"/> before one.</summary>
    private ScanReport? report;

    /// <summary>Initializes a new instance of the <see cref="MainViewModel"/> class, on the Scanning screen.</summary>
    /// <param name="utcOffset">The local time zone's offset from UTC at a Unix time, for the detail pane's dates.</param>
    public MainViewModel(Func<long, TimeSpan> utcOffset)
    {
        ArgumentNullException.ThrowIfNull(utcOffset);
        this.utcOffset = utcOffset;
        Root = "";
        Message = "";
    }

    /// <summary>Gets the screen shown.</summary>
    [ObservableProperty]
    public partial Screen Screen { get; private set; }

    /// <summary>Gets the scanned root; empty before a scan completes.</summary>
    [ObservableProperty]
    public partial string Root { get; private set; }

    /// <summary>Gets the Empty screen's text or the Failed screen's message.</summary>
    [ObservableProperty]
    public partial string Message { get; private set; }

    /// <summary>Gets the List screen; <see langword="null"/> unless the last scan found candidates.</summary>
    [ObservableProperty]
    public partial ListViewModel? List { get; private set; }

    /// <summary>Gets the Review screen; <see langword="null"/> until Review is first entered after the last scan.</summary>
    [ObservableProperty]
    public partial ReviewViewModel? Review { get; private set; }

    /// <summary>Gets the Removing screen; <see langword="null"/> outside the Removing screen.</summary>
    [ObservableProperty]
    public partial RemovingViewModel? Removing { get; private set; }

    /// <summary>Gets the Results screen; <see langword="null"/> until a removal after the last scan finishes.</summary>
    [ObservableProperty]
    public partial ResultsViewModel? Results { get; private set; }

    /// <summary>Shows the scan's candidates on the List, or the Empty screen when it found none, dropping every earlier screen.</summary>
    /// <param name="report">What the scan found.</param>
    /// <param name="nowUnix">The time ages are measured from, in Unix seconds.</param>
    public void ScanCompleted(ScanReport report, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(report);
        this.report = report;
        Root = report.Root;
        Review = null;
        Removing?.Dispose();
        Removing = null;
        Results = null;
        if (report.Candidates.Count == 0)
        {
            List = null;
            Message = $"No worktrees or orphan folders found under {report.Root}.";
            Screen = Screen.Empty;
            return;
        }
        List = new ListViewModel(ReportTable.Rows(report, nowUnix), nowUnix, utcOffset, EnterReview);
        Screen = Screen.List;
    }

    /// <summary>Shows the Failed screen with <paramref name="message"/>.</summary>
    /// <param name="message">Why the scan failed.</param>
    public void ScanFailed(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Message = message;
        Screen = Screen.Failed;
    }

    /// <summary>Shows the Failed screen with <paramref name="message"/> after the removal threw, releasing its cancellation.</summary>
    /// <param name="message">Why the removal stopped.</param>
    public void RemovalFailed(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Removing?.Dispose();
        Removing = null;
        Message = message;
        Screen = Screen.Failed;
    }

    /// <summary>Enters Review with the ticked rows, keeping the answers of an earlier visit.</summary>
    /// <param name="tickedRows">The ticked rows' indexes, in table order.</param>
    private void EnterReview(IReadOnlyList<int> tickedRows)
    {
        ListViewModel list = List ?? throw new InvalidOperationException("Review is entered from the List");
        ScanReport scanned = report ?? throw new InvalidOperationException("Review is entered after a scan");
        Review ??= new ReviewViewModel(scanned, ShowList, StartRemoval);
        Review.Enter(tickedRows, [.. tickedRows.Select(row => list.Rows[row])]);
        Screen = Screen.Review;
    }

    /// <summary>Returns to the List.</summary>
    private void ShowList() => Screen = Screen.List;

    /// <summary>Shows the Removing screen for <paramref name="decisions"/>.</summary>
    /// <param name="decisions">The answered review's decisions.</param>
    /// <param name="paths">Each decision's path, relative to the scanned root, in decision order.</param>
    private void StartRemoval(IReadOnlyList<Decision> decisions, IReadOnlyList<string> paths)
    {
        Removing = new RemovingViewModel(decisions, paths, ShowResults);
        Screen = Screen.Removing;
    }

    /// <summary>Shows the Results of the removal, releases its cancellation and drops it.</summary>
    /// <param name="swept">What happened to each pick.</param>
    private void ShowResults(IReadOnlyList<Swept> swept)
    {
        RemovingViewModel removing = Removing ?? throw new InvalidOperationException("Results follow a removal");
        Results = new ResultsViewModel(swept, Root, removing.Banner);
        removing.Dispose();
        Removing = null;
        Screen = Screen.Results;
    }
}
