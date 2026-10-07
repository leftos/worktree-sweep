using WorktreeSweep.Discovery;
using WorktreeSweep.Report;
using WorktreeSweep.Review;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>What removing a pick would lose, and the question confirming it.</summary>
public sealed class LossTextTests
{
    /// <summary>A long orphan path the loss text must never name.</summary>
    private const string LongPath = @"a-rather-long-repository-name.wt\a-feature-branch-with-a-long-descriptive-name\nested";

    /// <summary>A modified file is named in the sentence after the pick's relative path.</summary>
    [Fact]
    public void LossSentenceDirty()
    {
        WorktreeSignals signals = Signals(MergeState.Ancestor) with { Dirty = new Dirty { Modified = 1 } };

        Assert.Equal(RemoveAnyway(@"yaat.wt\eram-co\yaat: 1 modified file will be lost."), Sentence(Worktree(signals)));
    }

    /// <summary>Dirty files and unmerged commits are joined into one sentence.</summary>
    [Fact]
    public void LossSentenceUnmerged()
    {
        WorktreeSignals signals = Signals(MergeState.Unmerged(4)) with
        {
            Dirty = new Dirty { Modified = 3, Untracked = 2 },
        };

        Assert.Equal(
            RemoveAnyway(@"yaat.wt\eram-co\yaat: 3 modified, 2 untracked files and 4 commits not on main will be lost."),
            Sentence(Worktree(signals))
        );
    }

    /// <summary>A detached HEAD not on the default branch is named by its short hash; one contained in it loses nothing.</summary>
    [Fact]
    public void LossSentenceDetachedUncontained()
    {
        RegisteredCandidate detached = Detached(Worktree(Signals(MergeState.Detached(contained: false)) with { Upstream = null }));

        Assert.Equal(RemoveAnyway(@"yaat.wt\eram-co\yaat: detached HEAD 0123456 and its commits not on main will be lost."), Sentence(detached));
        RegisteredCandidate contained = Detached(Worktree(Signals(MergeState.Detached(contained: true))));
        Assert.Null(Sentence(contained));
    }

    /// <summary>A branch with no commits of its own may be new work, so its loss is confirmed.</summary>
    [Fact]
    public void LossSentenceNoCommits()
    {
        Assert.Equal(
            RemoveAnyway(@"yaat.wt\eram-co\yaat: a branch with no commits of its own (it may be new work in progress) will be lost."),
            Sentence(Worktree(Signals(MergeState.NoCommits)))
        );
    }

    /// <summary>An unpushed commit is named in singular.</summary>
    [Fact]
    public void LossSentenceUnpushed()
    {
        WorktreeSignals signals = Signals(MergeState.Ancestor) with { Upstream = Upstream.Tracking(1) };

        Assert.Equal(RemoveAnyway(@"yaat.wt\eram-co\yaat: 1 commit not pushed will be lost."), Sentence(Worktree(signals)));
    }

    /// <summary>A git lock's reason is quoted as its own sentence.</summary>
    [Fact]
    public void LossSentenceGitLocked()
    {
        RegisteredCandidate registered = Worktree(Signals(MergeState.Ancestor));
        RegisteredCandidate locked = registered with { Record = registered.Record with { Locked = "on a USB drive" } };

        Assert.Equal(RemoveAnyway(@"yaat.wt\eram-co\yaat: It is git-locked: on a USB drive."), Sentence(locked));
    }

    /// <summary>Every kind of loss asks a question of at most 40 characters naming no path, and its text never names the pick.</summary>
    [Fact]
    public void LossQuestionIsShortAndPathless()
    {
        RegisteredCandidate locked = Worktree(Signals(MergeState.Unmerged(9)));
        locked = locked with { Record = locked.Record with { Locked = @"on a USB drive mounted at E:\backups\worktrees" } };
        Candidate[] candidates =
        [
            Long(
                Worktree(
                    Signals(MergeState.Ancestor) with
                    {
                        Dirty = new Dirty { Modified = 3, Untracked = 2 },
                    }
                )
            ),
            Long(Worktree(Signals(MergeState.Unmerged(4)))),
            Long(Worktree(Signals(MergeState.NoCommits))),
            Long(Worktree(Signals(MergeState.Ancestor) with { Upstream = Upstream.Tracking(5) })),
            Long(locked),
            Long(Detached(Worktree(Signals(MergeState.Detached(contained: false))))),
            Orphan(OrphanKind.Link, liveGitdir: null),
            Orphan(OrphanKind.Folder, ReportSamples.Under(@"other\repo\.git\worktrees\nested")),
        ];

        foreach (Candidate candidate in candidates)
        {
            Loss? loss = LossText.For(candidate);
            Assert.NotNull(loss);
            Assert.DoesNotContain("\\", loss.Question, StringComparison.Ordinal);
            Assert.DoesNotContain("/", loss.Question, StringComparison.Ordinal);
            Assert.True(loss.Question.Length <= 40, $"question is {loss.Question.Length} chars: {loss.Question}");
            Assert.DoesNotContain(LongPath, loss.Text, StringComparison.Ordinal);
        }
    }

    /// <summary>A link's loss names its target, not the link, with a capital first letter and its own question.</summary>
    [Fact]
    public void LossTextLinkIsCapitalisedAndPathless()
    {
        Loss? link = LossText.For(Orphan(OrphanKind.Link, liveGitdir: null));

        Assert.NotNull(link);
        string target = ReportSamples.Under(@"elsewhere\a-rather-long-target-folder");
        Assert.Equal($"Remove the link only; {target} is not touched.", link.Text);
        Assert.True(link.IsLink);
        Assert.Equal("Remove the link?", link.Question);
        Assert.DoesNotContain(LongPath, link.Text, StringComparison.Ordinal);

        Loss? noCommits = LossText.For(Worktree(Signals(MergeState.NoCommits)));
        Assert.NotNull(noCommits);
        Assert.Equal("A branch with no commits of its own (it may be new work in progress) will be lost.", noCommits.Text);
        Assert.False(noCommits.IsLink);
    }

    /// <summary>A merged worktree and a folder no repo registers lose nothing.</summary>
    [Fact]
    public void LossTextNoneWhenNothingLost()
    {
        Assert.Null(LossText.For(Worktree(Signals(MergeState.Ancestor))));
        Assert.Null(LossText.For(Orphan(OrphanKind.Folder, liveGitdir: null)));
    }

    /// <summary>Clean signals with <paramref name="mergeState"/> against <c>main</c>, tracking an upstream with nothing ahead.</summary>
    /// <param name="mergeState">The merge state.</param>
    /// <returns>The signals.</returns>
    private static WorktreeSignals Signals(MergeState mergeState) =>
        new()
        {
            MergeState = mergeState,
            MergeStateAgainst = "main",
            Dirty = new Dirty(),
            Upstream = Upstream.Tracking(0),
            Size = new SizeInfo(),
        };

    /// <summary>The worktree <c>D:\yaat.wt\eram-co\yaat</c> on <c>eram-co</c>.</summary>
    /// <param name="signals">Its signals.</param>
    /// <returns>The candidate.</returns>
    private static RegisteredCandidate Worktree(WorktreeSignals signals) => ReportSamples.Registered(@"yaat.wt\eram-co\yaat", "eram-co", signals);

    /// <summary><paramref name="registered"/> with a detached HEAD.</summary>
    /// <param name="registered">The worktree.</param>
    /// <returns>The candidate.</returns>
    private static RegisteredCandidate Detached(RegisteredCandidate registered) =>
        registered with
        {
            Record = registered.Record with { Branch = null, Detached = true },
        };

    /// <summary><paramref name="registered"/> moved to <see cref="LongPath"/>.</summary>
    /// <param name="registered">The worktree.</param>
    /// <returns>The candidate.</returns>
    private static RegisteredCandidate Long(RegisteredCandidate registered) =>
        registered with
        {
            Record = registered.Record with { Path = ReportSamples.Under(LongPath) },
        };

    /// <summary>An orphan at <see cref="LongPath"/>; a link points into <c>D:\elsewhere</c>.</summary>
    /// <param name="kind">Folder or link.</param>
    /// <param name="liveGitdir">The live git dir its <c>.git</c> file names, if any.</param>
    /// <returns>The candidate.</returns>
    private static OrphanCandidate Orphan(OrphanKind kind, string? liveGitdir) =>
        new()
        {
            Orphan = new Orphan
            {
                Path = ReportSamples.Under(LongPath),
                Container = ReportSamples.Under("a-rather-long-repository-name.wt"),
                Kind = kind,
                LinkTarget = kind == OrphanKind.Link ? ReportSamples.Under(@"elsewhere\a-rather-long-target-folder") : null,
                LiveGitdir = liveGitdir,
            },
            Size = new SizeInfo(),
        };

    /// <summary>The confirmation for a registered worktree, its path relative to <see cref="ReportSamples.Root"/>.</summary>
    /// <param name="registered">The worktree.</param>
    /// <returns>The context and question, or <see langword="null"/>.</returns>
    private static (string Context, string Question)? Sentence(RegisteredCandidate registered) => LossText.Sentence(registered, ReportSamples.Root);

    /// <summary>The confirmation <paramref name="context"/> with the question every loss but a link's asks.</summary>
    /// <param name="context">The expected context.</param>
    /// <returns>The context and question.</returns>
    private static (string Context, string Question)? RemoveAnyway(string context) => (context, "Remove anyway?");
}
