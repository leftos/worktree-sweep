using WorktreeSweep.Agent;
using WorktreeSweep.Discovery;
using WorktreeSweep.Holders;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>The agent removal's pure rules: what would be lost, what happens to the branch, and why a locked folder is released.</summary>
public sealed class AgentRulesTests
{
    /// <summary>A clean, merged worktree, and a detached HEAD the default branch contains, lose nothing.</summary>
    [Fact]
    public void CleanMergedWorktreeLosesNothing()
    {
        Assert.Null(WouldLose.Text(Clean(MergeState.Ancestor)));
        Assert.Null(WouldLose.Text(Clean(MergeState.Detached(contained: true))));
    }

    /// <summary>A branch with no commits of its own is not a loss.</summary>
    [Fact]
    public void NoCommitsIsNotALoss() => Assert.Null(WouldLose.Text(Clean(MergeState.NoCommits)));

    /// <summary>Modified files are a loss.</summary>
    [Fact]
    public void ModifiedFilesAreALoss()
    {
        RegisteredCandidate candidate = WithSignals(Clean(MergeState.Ancestor), signals => signals with { Dirty = new Dirty { Modified = 2 } });

        Assert.Equal(@"x.wt\feat: 2 modified files will be lost.", WouldLose.Text(candidate));
    }

    /// <summary>Untracked files are a loss.</summary>
    [Fact]
    public void UntrackedFilesAreALoss()
    {
        RegisteredCandidate candidate = WithSignals(Clean(MergeState.Ancestor), signals => signals with { Dirty = new Dirty { Untracked = 1 } });

        Assert.Equal(@"x.wt\feat: 1 untracked file will be lost.", WouldLose.Text(candidate));
    }

    /// <summary>Commits not on the default branch are a loss.</summary>
    [Fact]
    public void UnmergedCommitsAreALoss() =>
        Assert.Equal(@"x.wt\feat: 3 commits not on main will be lost.", WouldLose.Text(Clean(MergeState.Unmerged(3))));

    /// <summary>A detached HEAD the default branch does not contain is a loss.</summary>
    [Fact]
    public void UncontainedDetachedHeadIsALoss()
    {
        RegisteredCandidate candidate = WithBranch(Clean(MergeState.Detached(contained: false)), null);

        Assert.Equal(@"x.wt\feat: detached HEAD 0123456 and its commits not on main will be lost.", WouldLose.Text(candidate));
    }

    /// <summary>Commits the upstream lacks are a loss.</summary>
    [Fact]
    public void UnpushedCommitsAreALoss()
    {
        RegisteredCandidate candidate = WithSignals(Clean(MergeState.Ancestor), signals => signals with { Upstream = Upstream.Tracking(1) });

        Assert.Equal(@"x.wt\feat: 1 commit not pushed will be lost.", WouldLose.Text(candidate));
    }

    /// <summary>A git lock is a loss.</summary>
    [Fact]
    public void GitLockIsALoss()
    {
        RegisteredCandidate clean = Clean(MergeState.Ancestor);
        RegisteredCandidate candidate = clean with { Record = clean.Record with { Locked = "in use" } };

        Assert.Equal(@"x.wt\feat: It is git-locked: in use.", WouldLose.Text(candidate));
    }

    /// <summary>A signal that could not be read is a loss, named after the worktree's path.</summary>
    [Fact]
    public void UnreadableSignalIsALoss()
    {
        RegisteredCandidate noDirty = WithSignals(Clean(MergeState.Ancestor), signals => signals with { Dirty = null });
        Assert.Equal(@"x.wt\feat: Its uncommitted changes could not be read.", WouldLose.Text(noDirty));

        RegisteredCandidate noMergeOrUpstream = WithSignals(
            Clean(MergeState.Ancestor),
            signals => signals with { MergeState = null, Upstream = null }
        );
        Assert.Equal(@"x.wt\feat: Its merge state, upstream could not be read.", WouldLose.Text(noMergeOrUpstream));
    }

    /// <summary>An unreadable signal is added after the loss sentence.</summary>
    [Fact]
    public void UnreadableSignalAddsToTheLossSentence()
    {
        RegisteredCandidate candidate = WithSignals(Clean(MergeState.Unmerged(1)), signals => signals with { Dirty = null });

        Assert.Equal(@"x.wt\feat: 1 commit not on main will be lost. Its uncommitted changes could not be read.", WouldLose.Text(candidate));
    }

    /// <summary>A branch merged into the default branch, or with no commits of its own, is deleted.</summary>
    [Fact]
    public void MergedAndEmptyBranchesAreDeleted()
    {
        Assert.Equal(new AgentBranch.Delete("feat"), AgentBranch.For(Clean(MergeState.Ancestor)));
        Assert.Equal(new AgentBranch.Delete("feat"), AgentBranch.For(Clean(MergeState.NoCommits)));
    }

    /// <summary>A squash-merged or cherry-picked branch is kept, with a note saying how to delete it.</summary>
    [Fact]
    public void SquashMergedBranchesAreKeptWithANote()
    {
        Assert.Equal(
            new AgentBranch.Keep("branch feat kept: squash-merged, delete it with git branch -D"),
            AgentBranch.For(Clean(MergeState.ContentContained))
        );
        Assert.Equal(
            new AgentBranch.Keep("branch feat kept: cherry-picked, delete it with git branch -D"),
            AgentBranch.For(Clean(MergeState.PatchesApplied))
        );
    }

    /// <summary>An unmerged branch, a detached HEAD and an unknown merge state keep the branch without a note.</summary>
    [Fact]
    public void UnmergedAndDetachedBranchesAreKeptSilently()
    {
        Assert.Equal(new AgentBranch.Keep(null), AgentBranch.For(Clean(MergeState.Unmerged(1))));
        Assert.Equal(new AgentBranch.Keep(null), AgentBranch.For(WithBranch(Clean(MergeState.Detached(contained: true)), null)));
        Assert.Equal(
            new AgentBranch.Keep(null),
            AgentBranch.For(WithSignals(Clean(MergeState.Ancestor), signals => signals with { MergeState = null }))
        );
    }

    /// <summary>The caller holds the folder only through a process in its own chain that holds something there.</summary>
    [Fact]
    public void CallerHoldsOnlyThroughItsOwnChain()
    {
        var report = new HolderReport([Holder(10, CwdHold()), Holder(20, [])], [May(30)]);

        Assert.True(CallerHolds.Check(new HashSet<int> { 1, 10, 100 }, report));
        Assert.False(CallerHolds.Check(new HashSet<int> { 1, 20, 30 }, report));
        Assert.False(CallerHolds.Check(new HashSet<int> { 1, 2 }, report));
    }

    /// <summary>A definite holder makes the reason <c>locked</c>; only unnamed ones make it <c>may_hold</c>; none at all is <c>locked</c>.</summary>
    [Fact]
    public void LockedReasonPrefersDefiniteHolders()
    {
        Assert.Equal(Reason.Locked, LockedReason.For(new HolderReport([Holder(10, CwdHold())], [May(30)])));
        Assert.Equal(Reason.MayHold, LockedReason.For(new HolderReport([], [May(30)])));
        Assert.Equal(Reason.Locked, LockedReason.For(new HolderReport([], [])));
    }

    /// <summary>A clean worktree <c>D:\x.wt\feat</c> of <c>D:\x</c> on branch <c>feat</c>, with no upstream.</summary>
    private static RegisteredCandidate Clean(MergeState mergeState) =>
        new()
        {
            Record = new WorktreeRecord
            {
                Path = @"D:\x.wt\feat",
                Branch = "feat",
                Head = "0123456789abcdef",
            },
            Repo = @"D:\x",
            Signals = new WorktreeSignals
            {
                MergeState = mergeState,
                MergeStateAgainst = "main",
                Dirty = new Dirty(),
                Upstream = Upstream.None,
            },
        };

    private static RegisteredCandidate WithSignals(RegisteredCandidate candidate, Func<WorktreeSignals, WorktreeSignals> change) =>
        candidate with
        {
            Signals = change(candidate.Signals),
        };

    private static RegisteredCandidate WithBranch(RegisteredCandidate candidate, string? branch) =>
        candidate with
        {
            Record = candidate.Record with { Branch = branch },
        };

    private static Holder Holder(int pid, IReadOnlyList<Hold> holds) => new(pid, "pwsh.exe", Image: null, Started: 1, CommandLine: null, holds);

    private static Hold[] CwdHold() => [new Hold.CurrentFolder(@"D:\x.wt\feat")];

    private static MayHold May(int pid) => new(pid, "svchost.exe", MayHoldWhy.UnnamedHandle);
}
