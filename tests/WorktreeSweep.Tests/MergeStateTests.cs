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
}
