using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;

namespace WorktreeSweep.ViewModels;

/// <summary>
/// The window: which screen it shows, the folder box, the scanned root, and each screen's view model once reached. Every change is a
/// method call on the UI thread; the screens' view models call back here to move between screens.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>The window's name, which every title starts with.</summary>
    public const string AppName = "worktree-sweep";

    /// <summary>The local time zone's offset from UTC at a Unix time.</summary>
    private readonly Func<long, TimeSpan> utcOffset;

    /// <summary>The completed scan; <see langword="null"/> before one.</summary>
    private ScanReport? report;

    /// <summary>Initializes a new instance of the <see cref="MainViewModel"/> class, on the Choose screen with an empty folder box.</summary>
    /// <param name="utcOffset">The local time zone's offset from UTC at a Unix time, for the detail pane's dates.</param>
    public MainViewModel(Func<long, TimeSpan> utcOffset)
    {
        ArgumentNullException.ThrowIfNull(utcOffset);
        this.utcOffset = utcOffset;
        Screen = Screen.Choose;
        Folder = "";
        FolderError = "";
        Root = "";
        Message = "";
        ScanSummary = "";
        EmptyDiscoveryErrors = [];
        Recent = [];
    }

    /// <summary>Gets the screen shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ShowsRootBar), nameof(IsScanning), nameof(CanEditFolder))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(BrowseCommand), nameof(ScanRecentCommand), nameof(StopScanCommand))]
    public partial Screen Screen { get; private set; }

    /// <summary>Gets or sets the folder box: what <see cref="ScanCommand"/> scans.</summary>
    [ObservableProperty]
    public partial string Folder { get; set; }

    /// <summary>Gets the line under the folder box, on Choose and in the root bar; empty unless the last Scan was refused.</summary>
    [ObservableProperty]
    public partial string FolderError { get; private set; }

    /// <summary>Gets the full path of the folder the last scan began on, or the root the last completed scan reports; empty before one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Root { get; private set; }

    /// <summary>Gets the Empty screen's text or the Failed screen's message.</summary>
    [ObservableProperty]
    public partial string Message { get; private set; }

    /// <summary>Gets the root bar's account of the last completed scan: <c>Scanned just now · 14 repos · 9.8 s</c>; empty otherwise.</summary>
    [ObservableProperty]
    public partial string ScanSummary { get; private set; }

    /// <summary>Gets the Empty screen's discovery errors as <c>{path}: {message}</c>, each on one line; empty when the scan had none.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> EmptyDiscoveryErrors { get; private set; }

    /// <summary>Gets the Empty screen's notice title, <c>{n} discovery error(s).</c>; <see langword="null"/> when the scan had none.</summary>
    [ObservableProperty]
    public partial string? EmptyDiscoveryErrorsTitle { get; private set; }

    /// <summary>Gets the recent folders, most recent first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecent))]
    public partial IReadOnlyList<RecentRow> Recent { get; private set; }

    /// <summary>Gets the Scanning screen; <see langword="null"/> before the first scan begins.</summary>
    [ObservableProperty]
    public partial ScanningViewModel? Scanning { get; private set; }

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

    /// <summary>
    /// Gets or sets the folder picker the window supplies: given the folder box's text, it returns the folder picked, or
    /// <see langword="null"/> when none is.
    /// </summary>
    public Func<string, string?>? PickFolder { get; set; }

    /// <summary>
    /// Gets the window's title: <c>worktree-sweep</c> on Choose, <c>worktree-sweep — {root} — scanning 71%</c> while scanning, and
    /// <c>worktree-sweep — {root}</c> on the other screens.
    /// </summary>
    public string Title =>
        Screen == Screen.Choose || Root.Length == 0 ? AppName
        : Screen == Screen.Scanning ? string.Create(CultureInfo.InvariantCulture, $"{AppName} — {Root} — scanning {Scanning?.Percent ?? 0}%")
        : $"{AppName} — {Root}";

    /// <summary>Gets a value indicating whether the root bar shows: on the List, Empty, Failed, Results and Scanning screens.</summary>
    public bool ShowsRootBar => Screen is Screen.List or Screen.Empty or Screen.Failed or Screen.Results or Screen.Scanning;

    /// <summary>Gets a value indicating whether a scan is running, when the root bar offers Stop scan instead of Rescan.</summary>
    public bool IsScanning => Screen == Screen.Scanning;

    /// <summary>Gets a value indicating whether the folder box and Browse… take input: not while scanning.</summary>
    public bool CanEditFolder => !IsScanning;

    /// <summary>Gets a value indicating whether the Choose screen lists recent folders.</summary>
    public bool HasRecent => Recent.Count > 0;

    /// <summary>Shows <paramref name="entries"/> as the recent folders.</summary>
    /// <param name="entries">The recent folders, most recent first.</param>
    /// <param name="nowUnix">The time their ages are measured from, in Unix seconds.</param>
    public void ShowRecent(IReadOnlyList<RecentFolder> entries, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Recent = [.. entries.Select(entry => new RecentRow(entry.Folder, RecentFolders.Summary(entry, nowUnix)))];
    }

    /// <summary>Shows how far the running scan has got.</summary>
    /// <param name="snapshot">The scan's tally.</param>
    public void ScanProgressed(ScanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ScanningViewModel scanning = Scanning ?? throw new InvalidOperationException("progress follows the start of a scan");
        scanning.Show(snapshot);
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>
    /// Shows the scan's candidates on the List, or the Empty screen when it found none, dropping every earlier screen, and sums the
    /// scan up for the root bar with the Scanning screen's elapsed time.
    /// </summary>
    /// <param name="report">What the scan found.</param>
    /// <param name="nowUnix">The time ages are measured from, in Unix seconds.</param>
    public void ScanCompleted(ScanReport report, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(report);
        this.report = report;
        Root = report.Root;
        DropScreens();
        ScanSummary = Summary(report.Repos.Count, Scanning?.Elapsed ?? TimeSpan.Zero);
        if (report.Candidates.Count == 0)
        {
            Message = $"No worktrees or orphan folders found under {report.Root}.";
            EmptyDiscoveryErrors = ReportTable.DiscoveryErrorLines(report);
            EmptyDiscoveryErrorsTitle = DiscoveryErrorsText.Title(EmptyDiscoveryErrors.Count);
            Screen = Screen.Empty;
            return;
        }
        List = new ListViewModel(report, nowUnix, utcOffset, EnterReview);
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

    /// <summary>
    /// Returns to the Choose screen after the running scan was cancelled, keeping the folder box; the stopped scan's root is cleared,
    /// since no report stands behind it.
    /// </summary>
    public void ScanStopped()
    {
        report = null;
        Root = "";
        Screen = Screen.Choose;
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

    /// <summary>
    /// Scans the folder box's folder: refused on Review, Removing and Scanning; on an empty folder box, or one that names no existing
    /// folder, it stays on the screen with <see cref="FolderError"/> set.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private void Scan()
    {
        if (!CanScan())
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(Folder))
        {
            FolderError = "Choose a folder to scan.";
            return;
        }
        string? root = FullFolder(Folder);
        if (root is null)
        {
            FolderError = $"No folder at {Folder}.";
            return;
        }
        BeginScan(root);
    }

    /// <summary>Puts <paramref name="folder"/> in the folder box and scans it.</summary>
    /// <param name="folder">A recent folder.</param>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private void ScanRecent(string? folder)
    {
        if (folder is null || !CanScan())
        {
            return;
        }
        Folder = folder;
        Scan();
    }

    /// <summary>Asks the window's folder picker for a folder and puts the one picked in the folder box.</summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private void Browse()
    {
        if (CanScan() && PickFolder?.Invoke(Folder) is { } picked)
        {
            Folder = picked;
        }
    }

    /// <summary>Stops the running scan: back to the Choose screen, with the folder box and the recent folders kept.</summary>
    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void StopScan()
    {
        if (IsScanning)
        {
            ScanStopped();
        }
    }

    /// <summary>Whether a scan may start: not on Review, Removing or Scanning.</summary>
    private bool CanScan() => Screen is not (Screen.Review or Screen.Removing or Screen.Scanning);

    /// <summary>Clears the folder error once the folder box changes.</summary>
    partial void OnFolderChanged(string value) => FolderError = "";

    /// <summary>Starts a scan of <paramref name="root"/>: drops every screen from the last scan and shows the Scanning screen.</summary>
    /// <param name="root">The full path of an existing folder.</param>
    private void BeginScan(string root)
    {
        DropScreens();
        report = null;
        ScanSummary = "";
        FolderError = "";
        EmptyDiscoveryErrors = [];
        EmptyDiscoveryErrorsTitle = null;
        Scanning = new ScanningViewModel(root);
        Root = root;
        Screen = Screen.Scanning;
    }

    /// <summary>Drops the List, Review, Removing and Results screens, releasing the removal's cancellation.</summary>
    private void DropScreens()
    {
        List = null;
        Review = null;
        Removing?.Dispose();
        Removing = null;
        Results = null;
    }

    /// <summary>The full path of an existing folder <paramref name="folder"/> names; <see langword="null"/> when it names none.</summary>
    private static string? FullFolder(string folder)
    {
        try
        {
            string full = Path.GetFullPath(folder);
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    /// <summary>The root bar's account of a completed scan.</summary>
    private static string Summary(int repos, TimeSpan took)
    {
        string repoText = repos == 1 ? "1 repo" : string.Create(CultureInfo.InvariantCulture, $"{repos} repos");
        return string.Create(CultureInfo.InvariantCulture, $"Scanned just now · {repoText} · {took.TotalSeconds:0.0} s");
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
