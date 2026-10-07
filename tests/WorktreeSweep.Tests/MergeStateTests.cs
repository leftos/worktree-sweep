using System.Collections.Concurrent;
using WorktreeSweep.Discovery;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>How far a worktree's branch has made it into the default branch.</summary>
public sealed class MergeStateTests
{
    /// <summary>A branch fast-forwarded into main is an ancestor.</summary>
    [Fact]
    public void MergedBranchIsAncestor()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        _ = Fixture.CommitFile(wt, "b.txt", "b\n", "b");
        _ = Fixture.Git(repo, ["merge", "-q", "--ff-only", "feat"]);

        Assert.Equal(MergeState.Ancestor, fx.Registered(wt).Signals.MergeState);
    }

    /// <summary>A branch with no commit of its own is new work, not merged work.</summary>
    [Fact]
    public void FreshBranchIsNoCommits()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");

        Assert.Equal(MergeState.NoCommits, fx.Registered(wt).Signals.MergeState);
    }

    /// <summary>A merge commit made on the branch is a commit of its own, so the merged branch is an ancestor.</summary>
    [Fact]
    public void BranchWithMergeCommitIsNotNoCommits()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        _ = Fixture.Git(wt, ["checkout", "-q", "-b", "side"]);
        _ = Fixture.CommitFile(wt, "b.txt", "b\n", "b");
        _ = Fixture.Git(wt, ["checkout", "-q", "feat"]);
        _ = Fixture.Git(wt, ["merge", "-q", "--no-ff", "-m", "merge side", "side"]);
        _ = Fixture.Git(repo, ["merge", "-q", "--ff-only", "feat"]);

        Assert.Equal(MergeState.Ancestor, fx.Registered(wt).Signals.MergeState);
    }

    /// <summary>A branch whose every commit was cherry-picked onto main has its patches applied.</summary>
    [Fact]
    public void CherryPickedBranchIsPatchesApplied()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        string first = Fixture.CommitFile(wt, "b.txt", "b\n", "b");
        string second = Fixture.CommitFile(wt, "c.txt", "c\n", "c");
        _ = Fixture.CommitFile(repo, "x.txt", "x\n", "unrelated");
        _ = Fixture.Git(repo, ["cherry-pick", first, second]);

        Assert.Equal(MergeState.PatchesApplied, fx.Registered(wt).Signals.MergeState);
    }

    /// <summary>A branch squash-merged into main is content-contained.</summary>
    [Fact]
    public void SquashMergedBranchIsContentContained()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        _ = Fixture.CommitFile(wt, "b.txt", "b\n", "b");
        _ = Fixture.CommitFile(wt, "c.txt", "c\n", "c");
        _ = Fixture.Git(repo, ["merge", "-q", "--squash", "feat"]);
        _ = Fixture.Git(repo, ["commit", "-q", "-m", "squash feat"]);
        _ = Fixture.CommitFile(repo, "x.txt", "x\n", "unrelated");

        Assert.Equal(MergeState.ContentContained, fx.Registered(wt).Signals.MergeState);
    }

    /// <summary>A branch with commits main lacks is unmerged, with the count of those commits.</summary>
    [Fact]
    public void DivergentBranchIsUnmergedWithCount()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        _ = Fixture.CommitFile(wt, "b.txt", "b\n", "b");
        _ = Fixture.CommitFile(wt, "c.txt", "c\n", "c");
        _ = Fixture.CommitFile(repo, "x.txt", "x\n", "unrelated");

        Assert.Equal(MergeState.Unmerged(2), fx.Registered(wt).Signals.MergeState);
    }

    /// <summary>A branch whose merge into main would conflict is unmerged, not content-contained.</summary>
    [Fact]
    public void ConflictingBranchIsNotContentContained()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        _ = Fixture.CommitFile(wt, "README.md", "from feat\n", "feat edit");
        _ = Fixture.CommitFile(repo, "README.md", "from main\n", "main edit");

        Assert.Equal(MergeState.Unmerged(1), fx.Registered(wt).Signals.MergeState);
    }

    /// <summary>A detached HEAD is contained when main reaches it, and not once it has moved on.</summary>
    [Fact]
    public void DetachedHeadIsDetached()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string atMain = fx.PathTo("repo.wt/at-main");
        string movedOn = fx.PathTo("repo.wt/moved-on");
        _ = Fixture.Git(repo, ["worktree", "add", "-q", "--detach", atMain, "HEAD"]);
        _ = Fixture.Git(repo, ["worktree", "add", "-q", "--detach", movedOn, "HEAD"]);
        _ = Fixture.CommitFile(movedOn, "b.txt", "b\n", "b");

        (WorktreeRecord Record, WorktreeSignals Signals) contained = fx.Registered(atMain);
        Assert.Null(contained.Record.Branch);
        Assert.Equal(MergeState.Detached(contained: true), contained.Signals.MergeState);
        Assert.Equal(MergeState.Detached(contained: false), fx.Registered(movedOn).Signals.MergeState);
    }

    /// <summary>When the local main is behind <c>origin/main</c>, the branch is measured against <c>origin/main</c>.</summary>
    [Fact]
    public void OriginDefaultIsUsedWhenLocalIsBehind()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        _ = fx.Origin(repo);
        _ = Fixture.Git(repo, ["push", "-q", "origin", "main"]);
        _ = Fixture.Git(repo, ["remote", "set-head", "origin", "main"]);
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        _ = Fixture.CommitFile(wt, "b.txt", "b\n", "b");
        _ = Fixture.Git(repo, ["push", "-q", "origin", "feat:main"]);
        _ = Fixture.Git(repo, ["fetch", "-q", "origin"]);

        WorktreeSignals signals = fx.Registered(wt).Signals;
        Assert.Equal(MergeState.Ancestor, signals.MergeState);
        Assert.Equal("origin/main", signals.MergeStateAgainst);
    }

    /// <summary>A branch that shares no history with main is unmerged, with no signal error.</summary>
    [Fact]
    public void UnrelatedHistoriesAreNotContained()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        _ = Fixture.Git(wt, ["checkout", "-q", "--orphan", "unrelated"]);
        _ = Fixture.CommitFile(wt, "b.txt", "b\n", "b");

        WorktreeSignals signals = fx.Registered(wt).Signals;
        Assert.Equal(MergeState.Unmerged(1), signals.MergeState);
        Assert.Empty(signals.Errors);
    }

    /// <summary>
    /// A merge-tree that fails, here on a bogus <c>merge.conflictStyle</c> only merge-tree reads, is a signal error, not "not
    /// contained".
    /// </summary>
    [Fact]
    public void MergeTreeFailureIsRecordedAsAnError()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        _ = Fixture.CommitFile(wt, "b.txt", "b\n", "b");
        _ = Fixture.Git(repo, ["config", "merge.conflictStyle", "bogus"]);

        WorktreeSignals signals = fx.Registered(wt).Signals;
        Assert.Null(signals.MergeState);
        Assert.Contains("merge-tree", Assert.Single(signals.Errors), StringComparison.Ordinal);
    }

    /// <summary>A merge-tree that fails against the stale local main still lets origin/main, which has the branch, answer.</summary>
    [Fact]
    public void FailureOnLocalDefaultStillMeasuresOrigin()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        _ = fx.Origin(repo);
        _ = Fixture.Git(repo, ["push", "-q", "origin", "main"]);
        _ = Fixture.Git(repo, ["remote", "set-head", "origin", "main"]);
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        _ = Fixture.CommitFile(wt, "b.txt", "b\n", "b");
        _ = Fixture.Git(repo, ["push", "-q", "origin", "feat:main"]);
        _ = Fixture.Git(repo, ["fetch", "-q", "origin"]);
        _ = Fixture.Git(repo, ["config", "merge.conflictStyle", "bogus"]);

        WorktreeSignals signals = fx.Registered(wt).Signals;
        Assert.Equal(MergeState.Ancestor, signals.MergeState);
        Assert.Equal("origin/main", signals.MergeStateAgainst);
        Assert.Empty(signals.Errors);
    }

    /// <summary>A detached HEAD is still measured against the local main when the origin default names a branch that does not exist.</summary>
    [Fact]
    public void DetachedFailureOnOriginStillMeasuresLocal()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/moved-on");
        _ = Fixture.Git(repo, ["worktree", "add", "-q", "--detach", wt, "HEAD"]);
        _ = Fixture.CommitFile(wt, "b.txt", "b\n", "b");
        DiscoveryResult found = Discoverer.Discover(fx.Root);
        WorktreeRecord record = Assert.Single(found.Registered, candidate => Fixture.SamePath(candidate.Record.Path, wt)).Record;
        var defaults = new DefaultBranches { Local = "main", Origin = "origin/gone" };

        WorktreeSignals signals = SignalReader.ReadWorktreeSignals(defaults, record);
        Assert.Equal(MergeState.Detached(contained: false), signals.MergeState);
        Assert.Equal("main", signals.MergeStateAgainst);
        Assert.Empty(signals.Errors);
    }

    /// <summary>An origin/HEAD that points to a branch that does not exist is dropped, and the local default falls back to main.</summary>
    [Fact]
    public void DanglingOriginHeadIsDropped()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        _ = fx.Origin(repo);
        _ = Fixture.Git(repo, ["symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/gone"]);

        DefaultBranches defaults = SignalReader.ReadDefaultBranches(repo);
        Assert.Null(defaults.Origin);
        Assert.Equal("main", defaults.Local);
    }
}

/// <summary>
/// The scratch object folders a merge-tree writes to. The test points this process's temp folder at a private one, so it runs in
/// the collection no other test runs beside.
/// </summary>
[Collection(ProcessEnvironment.Name)]
public sealed class MergeScratchTests
{
    private const string ScratchPrefix = "worktree-sweep-objects-";

    /// <summary>A branch measured against both main and origin/main uses one scratch folder for both, removed afterwards.</summary>
    [Fact]
    public void ScratchFolderIsSharedAcrossDefaultRefs()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        _ = fx.Origin(repo);
        _ = Fixture.Git(repo, ["push", "-q", "origin", "main"]);
        _ = Fixture.Git(repo, ["remote", "set-head", "origin", "main"]);
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        _ = Fixture.CommitFile(wt, "b.txt", "b\n", "b");
        string temp = Directory.CreateDirectory(fx.PathTo("temp")).FullName;

        var created = new ConcurrentQueue<string>();
        using var watcher = new FileSystemWatcher(temp) { NotifyFilter = NotifyFilters.DirectoryName };
        watcher.Created += (_, args) => created.Enqueue(Path.GetFileName(args.FullPath));
        watcher.EnableRaisingEvents = true;
        using var env = new InheritedEnv(new Dictionary<string, string> { ["TMP"] = temp, ["TEMP"] = temp });

        WorktreeSignals signals = fx.Registered(wt).Signals;
        _ = Directory.CreateDirectory(Path.Join(temp, "sentinel"));
        bool sentinelSeen = SpinWait.SpinUntil(() => created.Contains("sentinel"), TimeSpan.FromSeconds(10));

        Assert.True(sentinelSeen, "the watcher never reported the sentinel folder, so its count of scratch folders is incomplete");
        Assert.Equal(MergeState.Unmerged(1), signals.MergeState);
        Assert.Single(created, name => name.StartsWith(ScratchPrefix, StringComparison.Ordinal));
        Assert.Empty(Directory.GetDirectories(temp, $"{ScratchPrefix}*"));
    }
}
