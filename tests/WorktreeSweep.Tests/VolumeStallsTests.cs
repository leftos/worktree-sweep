using WorktreeSweep.Discovery;
using WorktreeSweep.Git;
using WorktreeSweep.Scan;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>The volumes a git timeout stalls: one set per scan, shared by discovery and every signal read.</summary>
public sealed class VolumeStallsTests
{
    /// <summary>Marking one folder stalls its whole volume, and no other volume.</summary>
    [Fact]
    public void MarkingOneFolderStallsItsWholeVolume()
    {
        var stalls = new VolumeStalls();
        stalls.Mark(@"D:\a");

        Assert.True(stalls.IsStalled(@"D:\b\c"));
        Assert.SkipWhen(VolumeStalls.Key(@"E:\x") == VolumeStalls.Key(@"D:\a"), "this machine's E: resolves to D:'s volume");
        Assert.False(stalls.IsStalled(@"E:\x"));
    }

    /// <summary>A UNC path stalls its share alone, not another share of the same server.</summary>
    [Fact]
    public void UncPathStallsItsShareAlone()
    {
        var stalls = new VolumeStalls();
        stalls.Mark(@"\\nas\dev\a");

        Assert.True(stalls.IsStalled(@"\\nas\dev\other"));
        Assert.False(stalls.IsStalled(@"\\nas\other\a"));
    }

    /// <summary>Volume comparison folds case: the same volume spelled either way is one volume.</summary>
    [Fact]
    public void VolumeComparisonFoldsCase()
    {
        var stalls = new VolumeStalls();
        stalls.Mark(@"D:\a");

        Assert.Equal(VolumeStalls.Key(@"D:\a"), VolumeStalls.Key(@"d:\b"));
        Assert.True(stalls.IsStalled(@"d:\b"));
    }

    /// <summary>Marks made from many threads at once are all recorded, on every volume they name.</summary>
    [Fact]
    public void ConcurrentMarksAreAllRecorded()
    {
        var stalls = new VolumeStalls();
        string[] volumes = [@"D:\a", @"E:\b", @"F:\c", @"G:\d"];

        _ = Parallel.For(0, 512, index => stalls.Mark(volumes[index % volumes.Length]));

        Assert.All(volumes, volume => Assert.True(stalls.IsStalled(volume)));
    }

    /// <summary>
    /// A repo on a stalled volume is skipped without git being asked to list its worktrees: the fixture's own repo is on a volume
    /// that answers, so only the stalled set can have stopped the call.
    /// </summary>
    [Fact]
    public void RepoOnAStalledVolumeIsSkippedWithoutListingItsWorktrees()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        var stalls = new VolumeStalls();
        stalls.Mark(repo);

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Assert.Empty(found.Repos);
        DiscoveryError error = Assert.Single(found.Errors);
        Assert.Contains("stalled", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A stalled volume's default branches are not read: the repo answers main on a volume that is live.</summary>
    [Fact]
    public void DefaultBranchesOfARepoOnAStalledVolumeAreNotRead()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        var stalls = new VolumeStalls();
        stalls.Mark(repo);

        DefaultBranches defaults = Scanner.DefaultBranchesOrNone(repo, stalls);

        Assert.Null(defaults.Local);
        Assert.Null(defaults.Origin);
    }

    /// <summary>A stalled volume's common git dir is not read; the same repo without a stall answers one.</summary>
    [Fact]
    public void CommonDirOfARepoOnAStalledVolumeIsNotRead()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        var stalls = new VolumeStalls();
        stalls.Mark(repo);

        Assert.Null(Scanner.CommonDirOrNone(repo, stalls));
        Assert.NotNull(Scanner.CommonDirOrNone(repo, new VolumeStalls()));
    }
}
