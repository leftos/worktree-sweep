using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;
using WorktreeSweep.ViewModels;

namespace WorktreeSweep.Tests.ViewModels;

/// <summary>The window's screens after a scan.</summary>
public sealed class MainViewModelTests
{
    /// <summary>A scan that found nothing shows the Empty screen and says where it looked.</summary>
    [Fact]
    public void ScanCompletedWithNoCandidatesIsEmpty()
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        Assert.Equal(Screen.Scanning, main.Screen);

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
}
