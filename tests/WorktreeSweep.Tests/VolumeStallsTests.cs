using WorktreeSweep.Discovery;
using WorktreeSweep.Git;
using WorktreeSweep.Scan;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>The volumes a git timeout stalls: one set per scan, shared by discovery and every signal read.</summary>
public sealed class VolumeStallsTests
{
    /// <summary>Marking one folder stalls the whole volume it is spelled under, and no other volume.</summary>
    [Fact]
    public void MarkingOneFolderStallsItsWholeVolume()
    {
        var stalls = new VolumeStalls();
        stalls.Mark(@"D:\a");

        Assert.True(stalls.IsStalled(@"D:\b\c"));
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

    /// <summary>The key is the path's spelled root, compared folding case, and nothing is read to make it.</summary>
    [Fact]
    public void TheKeyIsTheSpelledRootComparedFoldingCase()
    {
        Assert.Equal(@"D:\", VolumeStalls.Volume(@"D:\a\b"));
        Assert.Equal(@"\\nas\dev", VolumeStalls.Volume(@"\\nas\dev\a"));
        Assert.Equal(VolumeStalls.Volume(@"D:\a"), VolumeStalls.Volume(@"d:/b"), ignoreCase: true);

        var stalls = new VolumeStalls();
        stalls.Mark(@"D:\a");

        Assert.True(stalls.IsStalled(@"d:\b"));
    }

    /// <summary>
    /// A subst drive and its target key apart, so scanning both may pay one extra timeout; a volume mounted into a folder keys with
    /// its host drive, so it pays none.
    /// </summary>
    [Fact]
    public void SubstAndMountKeyByTheirSpelling()
    {
        Assert.NotEqual(VolumeStalls.Volume(@"D:\a"), VolumeStalls.Volume(@"X:\dev\a"), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(@"D:\", VolumeStalls.Volume(@"D:\mnt\volume\a"), ignoreCase: true);
        Assert.Equal(@"D:\", VolumeStalls.Volume(@"D:\other"), ignoreCase: true);
    }

    /// <summary>Marks made from many threads at once are all recorded, on every one of the distinct keys they name.</summary>
    [Fact]
    public void ConcurrentMarksAreAllRecorded()
    {
        var stalls = new VolumeStalls();
        string[] paths = [@"D:\a", @"E:\b", @"F:\c", @"G:\d"];
        string[] keys = [.. paths.Select(VolumeStalls.Volume).Distinct(StringComparer.OrdinalIgnoreCase)];

        _ = Parallel.For(0, 512, index => stalls.Mark(paths[index % paths.Length]));

        Assert.Equal(paths.Length, keys.Length);
        Assert.All(keys, key => Assert.True(stalls.IsVolumeStalled(key)));
        Assert.All(paths, path => Assert.True(stalls.IsStalled(path)));
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

    /// <summary>
    /// A repo whose own worktree listing times out stalls its volume there, so the next repo on it is skipped without git being
    /// asked: the injected listing really throws, nothing is marked by hand, and it is never called for the second repo.
    /// </summary>
    [Fact]
    public void ARepoWhoseWorktreeListTimesOutStallsTheVolumeForTheNextRepo()
    {
        using var fx = new Fixture();
        string first = fx.Repo("a");
        string second = fx.Repo("b");
        var stalls = new VolumeStalls();
        var listed = new List<string>();

        DiscoveryResult found = Discoverer.Discover(
            fx.Root,
            PathResolver.Resolve,
            stalls,
            repo =>
            {
                listed.Add(repo);
                return Fixture.SamePath(repo, first)
                    ? throw new GitTimeoutException("`git -C a worktree list --porcelain` did not exit within 60 s")
                    : Discoverer.ListWorktrees(repo);
            }
        );

        string only = Assert.Single(listed);
        Assert.True(Fixture.SamePath(only, first), only);
        Assert.True(stalls.IsStalled(second));
        Assert.Empty(found.Repos);
        Assert.Equal(2, found.Errors.Count);
        Assert.Contains(found.Errors, error => error.Message.Contains("stalled", StringComparison.Ordinal));
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
