using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Scan;
using WorktreeSweep.Signals;
using WorktreeSweep.ViewModels;

namespace WorktreeSweep.Tests.ViewModels;

/// <summary>The window's screens: choosing a folder, scanning it, and the screens after a scan.</summary>
public sealed class MainViewModelTests
{
    /// <summary>A folder that exists on every machine, as its full path.</summary>
    private static readonly string Existing = Path.GetFullPath(Path.GetTempPath());

    /// <summary>Scan on the Choose screen shows the Scanning screen for the folder box's folder, and the completed scan shows the List.</summary>
    [Fact]
    public void ChooseThenScanningThenList()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero) { Folder = Existing };

        main.ScanCommand.Execute(null);

        Assert.Equal(Screen.Scanning, main.Screen);
        Assert.Equal(Existing, main.Scanning?.Root);
        Assert.True(main.ShowsRootBar);
        Assert.False(main.CanEditFolder);

        main.ScanCompleted(ReportSamples.ReportOf(ViewModelSamples.Worktree("a", MergeState.Ancestor, 10)), ReportSamples.Now);

        Assert.Equal(Screen.List, main.Screen);
        Assert.NotNull(main.List);
        Assert.Equal("Scanned just now · 0 repos · 0.0 s", main.ScanSummary);
    }

    /// <summary>The root bar's Rescan scans the folder box again from List, Empty, Failed and Results, dropping the old screens.</summary>
    /// <param name="screen">The screen Rescan is pressed on.</param>
    [Theory]
    [InlineData(Screen.List)]
    [InlineData(Screen.Empty)]
    [InlineData(Screen.Failed)]
    [InlineData(Screen.Results)]
    public void RescanBeginsScanningFrom(Screen screen)
    {
        MainViewModel main = On(screen);
        Assert.Equal(screen, main.Screen);
        Assert.True(main.ShowsRootBar);
        main.Folder = Existing;

        Assert.True(main.ScanCommand.CanExecute(null));
        main.ScanCommand.Execute(null);

        Assert.Equal(Screen.Scanning, main.Screen);
        Assert.Null(main.List);
        Assert.Null(main.Results);
        Assert.Equal("", main.ScanSummary);
    }

    /// <summary>Scan is refused on Review, Removing and Scanning: it cannot execute and pressing it changes nothing.</summary>
    /// <param name="screen">The screen Scan is pressed on.</param>
    [Theory]
    [InlineData(Screen.Review)]
    [InlineData(Screen.Removing)]
    [InlineData(Screen.Scanning)]
    public void ScanIsRefusedOn(Screen screen)
    {
        MainViewModel main = On(screen);
        Assert.Equal(screen, main.Screen);
        ScanningViewModel? scanning = main.Scanning;
        main.Folder = Existing;

        Assert.False(main.ScanCommand.CanExecute(null));
        main.ScanCommand.Execute(null);
        main.ScanRecentCommand.Execute(Existing);

        Assert.Equal(screen, main.Screen);
        Assert.Same(scanning, main.Scanning);
    }

    /// <summary>Stop scan returns to the Choose screen with the folder box kept, and a stopped Rescan drops the list it began from.</summary>
    [Fact]
    public void StopReturnsToChooseWithTheFolderKept()
    {
        MainViewModel main = On(Screen.List);
        main.Folder = Existing;
        main.ScanCommand.Execute(null);
        Assert.True(main.StopScanCommand.CanExecute(null));

        main.StopScanCommand.Execute(null);

        Assert.Equal(Screen.Choose, main.Screen);
        Assert.Equal(Existing, main.Folder);
        Assert.Null(main.List);
        Assert.False(main.ShowsRootBar);
        Assert.False(main.StopScanCommand.CanExecute(null));
    }

    /// <summary>A scan that fails shows the Failed screen with the root bar, so Scan is offered again.</summary>
    [Fact]
    public void FailedScanShowsFailedWithTheRootBar()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero) { Folder = Existing };
        main.ScanCommand.Execute(null);

        main.ScanFailed("Scan failed: boom");

        Assert.Equal(Screen.Failed, main.Screen);
        Assert.Equal("Scan failed: boom", main.Message);
        Assert.True(main.ShowsRootBar);
        Assert.True(main.ScanCommand.CanExecute(null));
    }

    /// <summary>An empty or blank folder box keeps the Choose screen and asks for a folder.</summary>
    /// <param name="folder">The folder box's text.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyFolderAsksForOne(string folder)
    {
        var main = new MainViewModel(_ => TimeSpan.Zero) { Folder = folder };

        main.ScanCommand.Execute(null);

        Assert.Equal(Screen.Choose, main.Screen);
        Assert.Equal("Choose a folder to scan.", main.FolderError);
        Assert.Null(main.Scanning);
    }

    /// <summary>
    /// Rescan with a folder box that names no folder stays on the List with the error line the root bar shows, and the next scan
    /// clears it.
    /// </summary>
    [Fact]
    public void BadFolderOnRescanStaysOnTheListWithTheError()
    {
        MainViewModel main = On(Screen.List);
        ListViewModel list = ViewModelSamples.ListOf(main);
        main.Folder = @"C:\no\such\folder\wts-57";

        main.ScanCommand.Execute(null);

        Assert.Equal(Screen.List, main.Screen);
        Assert.Same(list, main.List);
        Assert.True(main.ShowsRootBar);
        Assert.Equal(@"No folder at C:\no\such\folder\wts-57.", main.FolderError);
    }

    /// <summary>
    /// Stopping a Rescan of another folder returns to Choose with that folder in the box, but no root or report from the stopped scan,
    /// so the title names no folder.
    /// </summary>
    [Fact]
    public void StopClearsTheRootAndKeepsTheFolderBox()
    {
        MainViewModel main = On(Screen.List);
        Assert.Equal(ReportSamples.Root, main.Root);
        main.Folder = Existing;
        main.ScanCommand.Execute(null);

        main.StopScanCommand.Execute(null);

        Assert.Equal(Screen.Choose, main.Screen);
        Assert.Equal(Existing, main.Folder);
        Assert.Equal("", main.Root);
        Assert.Equal("worktree-sweep", main.Title);
    }

    /// <summary>A post that changes nothing replaces none of the Scanning screen's step rows or its working list.</summary>
    [Fact]
    public void UnchangedProgressReplacesNoRow()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero) { Folder = Existing };
        main.ScanCommand.Execute(null);
        ScanningViewModel scanning = main.Scanning ?? throw new InvalidOperationException("the scan did not begin");
        var snapshot = new ScanSnapshot
        {
            Discovered = new ScanProgress.Discovered(1, 1, 1, 0, 0),
            ReposDone = 1,
            CandidatesTotal = 1,
            Working = [new ScanProgress.CandidateStep(Path.Join(Existing, "a"), ScanStep.Status, 0)],
        };
        main.ScanProgressed(snapshot);
        var changed = new List<string?>();
        scanning.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        main.ScanProgressed(snapshot with { Working = [.. snapshot.Working] });

        Assert.DoesNotContain(nameof(ScanningViewModel.Working), changed);
        Assert.DoesNotContain(nameof(ScanningViewModel.Find), changed);
        Assert.DoesNotContain(nameof(ScanningViewModel.LookUp), changed);
        Assert.DoesNotContain(nameof(ScanningViewModel.Read), changed);
    }

    /// <summary>A folder box that names no folder keeps the Choose screen with one error line; editing the box clears it.</summary>
    /// <param name="folder">The folder box's text.</param>
    [Theory]
    [InlineData(@"C:\no\such\folder\wts-57")]
    [InlineData("bad\0path")]
    public void BadFolderStaysOnChooseWithTheErrorLine(string folder)
    {
        var main = new MainViewModel(_ => TimeSpan.Zero) { Folder = folder };

        main.ScanCommand.Execute(null);

        Assert.Equal(Screen.Choose, main.Screen);
        Assert.Equal($"No folder at {folder}.", main.FolderError);
        Assert.Null(main.Scanning);
        main.Folder = Existing;
        Assert.Equal("", main.FolderError);
    }

    /// <summary>
    /// The percent is the candidates read over the candidates to read, 0 before the first is read, rounded down: 10 of 14 is 71%, as
    /// the title shows.
    /// </summary>
    [Fact]
    public void PercentIsCandidatesReadOverTotal()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero) { Folder = Existing };
        main.ScanCommand.Execute(null);
        ScanningViewModel scanning = main.Scanning ?? throw new InvalidOperationException("the scan did not begin");
        var discovered = new ScanSnapshot
        {
            Discovered = new ScanProgress.Discovered(14, 6, 9, 4, 1),
            ReposDone = 14,
            CandidatesTotal = 14,
        };

        main.ScanProgressed(discovered);
        Assert.Equal(0, scanning.Percent);

        main.ScanProgressed(discovered with { CandidatesDone = 10 });

        Assert.Equal(71, scanning.Percent);
        Assert.Equal(3, scanning.Step);
        Assert.Equal("10 of 14", scanning.CandidatesText);
        Assert.Equal($"worktree-sweep — {Existing} — scanning 71%", main.Title);
    }

    /// <summary>The title names the app on Choose, the root and the percent while scanning, and the root on the other screens.</summary>
    [Fact]
    public void TitleFollowsTheScreen()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero) { Folder = Existing };
        Assert.Equal("worktree-sweep", main.Title);

        main.ScanCommand.Execute(null);
        Assert.Equal($"worktree-sweep — {Existing} — scanning 0%", main.Title);

        main.ScanCompleted(ReportSamples.ReportOf(), ReportSamples.Now);
        Assert.Equal(@"worktree-sweep — D:\", main.Title);

        main.ScanCommand.Execute(null);
        main.StopScanCommand.Execute(null);
        Assert.Equal("worktree-sweep", main.Title);
    }

    /// <summary>A scan that found nothing shows the Empty screen and says where it looked.</summary>
    [Fact]
    public void ScanCompletedWithNoCandidatesIsEmpty()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        Assert.Equal(Screen.Choose, main.Screen);

        main.ScanCompleted(ReportSamples.ReportOf(), ReportSamples.Now);

        Assert.Equal(Screen.Empty, main.Screen);
        Assert.Equal(@"No worktrees or orphan folders found under D:\.", main.Message);
        Assert.Null(main.List);
    }

    /// <summary>A failed scan shows the Failed screen with the failure's message.</summary>
    [Fact]
    public void ScanFailedShowsTheMessage()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);

        main.ScanFailed(@"D:\nowhere does not exist");

        Assert.Equal(Screen.Failed, main.Screen);
        Assert.Equal(@"D:\nowhere does not exist", main.Message);
    }

    /// <summary>A removal that threw shows the Failed screen with its message and drops the removal, releasing its cancellation.</summary>
    [Fact]
    public void RemovalFailedShowsTheMessageAndDropsTheRemoval()
    {
        MainViewModel main = ViewModelSamples.Listed(ViewModelSamples.Worktree("a", MergeState.Ancestor, 10));
        ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy).Answer(true);
        main.Review?.ConfirmCommand.Execute(null);
        RemovingViewModel? removing = main.Removing;
        Assert.NotNull(removing);

        main.RemovalFailed("the Recycle Bin refused D:\\repo.wt\\a");

        Assert.Equal(Screen.Failed, main.Screen);
        Assert.Equal("the Recycle Bin refused D:\\repo.wt\\a", main.Message);
        Assert.Null(main.Removing);
        Assert.Throws<ObjectDisposedException>(() => removing.Token);
    }

    /// <summary>A scan that completes while a removal is shown releases that removal's cancellation.</summary>
    [Fact]
    public void ScanCompletedDisposesARemoval()
    {
        MainViewModel main = ViewModelSamples.Listed(ViewModelSamples.Worktree("a", MergeState.Ancestor, 10));
        ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy).Answer(true);
        main.Review?.ConfirmCommand.Execute(null);
        RemovingViewModel? removing = main.Removing;
        Assert.NotNull(removing);

        main.ScanCompleted(ReportSamples.ReportOf(), ReportSamples.Now);

        Assert.Null(main.Removing);
        Assert.Throws<ObjectDisposedException>(() => removing.Token);
    }

    /// <summary>A scan that finds nothing after a removal leaves no screen's view model from before it.</summary>
    [Fact]
    public void ScanCompletedOnEmptyLeavesNoList()
    {
        RegisteredCandidate a = ViewModelSamples.Worktree("a", MergeState.Ancestor, 10);
        MainViewModel main = ViewModelSamples.Listed(a);
        ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy).Answer(true);
        main.Review?.ConfirmCommand.Execute(null);
        main.Removing?.Finished([new(a, new Outcome.Recycled(a.KnownSize), [])]);
        Assert.NotNull(main.Results);

        main.ScanCompleted(ReportSamples.ReportOf(), ReportSamples.Now);

        Assert.Equal(Screen.Empty, main.Screen);
        Assert.Null(main.List);
        Assert.Null(main.Review);
        Assert.Null(main.Removing);
        Assert.Null(main.Results);
    }

    /// <summary>A window on <paramref name="screen"/>, reached the way the user reaches it, with one worktree scanned.</summary>
    /// <param name="screen">The screen.</param>
    /// <returns>The window's view model.</returns>
    private static MainViewModel On(Screen screen)
    {
        RegisteredCandidate a = ViewModelSamples.Worktree("a", MergeState.Ancestor, 10);
        var main = new MainViewModel(_ => TimeSpan.Zero) { Folder = Existing };
        switch (screen)
        {
            case Screen.Scanning:
                main.ScanCommand.Execute(null);
                return main;
            case Screen.Empty:
                main.ScanCompleted(ReportSamples.ReportOf(), ReportSamples.Now);
                return main;
            case Screen.Failed:
                main.ScanFailed("boom");
                return main;
            case Screen.List:
                return ViewModelSamples.Listed(a);
            case Screen.Review:
                main = ViewModelSamples.Listed(a);
                ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy);
                return main;
            default:
                return Removed(screen, a);
        }
    }

    /// <summary>A window on Removing or Results after removing <paramref name="a"/>.</summary>
    /// <param name="screen">Removing or Results.</param>
    /// <param name="a">The worktree removed.</param>
    /// <returns>The window's view model.</returns>
    private static MainViewModel Removed(Screen screen, RegisteredCandidate a)
    {
        MainViewModel main = ViewModelSamples.Listed(a);
        ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy).Answer(true);
        main.Review?.ConfirmCommand.Execute(null);
        if (screen == Screen.Results)
        {
            main.Removing?.Finished([new(a, new Outcome.Recycled(a.KnownSize), [])]);
        }
        return main;
    }
}
