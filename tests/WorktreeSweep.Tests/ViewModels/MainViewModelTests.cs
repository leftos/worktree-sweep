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
}
