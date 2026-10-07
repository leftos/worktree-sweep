using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>The branch deletion offered for a worktree that is about to go.</summary>
public sealed class BranchOffersTests
{
    /// <summary>The branch the offers name; long, so a question that quoted it would show.</summary>
    private const string Branch = "feature/JIRA-1234-rework-the-candidate-table-rendering-for-narrow-terminals";

    /// <summary>Every offering merge state asks a short question naming no branch, over a context that names it.</summary>
    [Fact]
    public void BranchQuestionIsShortAndNameless()
    {
        MergeState[] states = [MergeState.Ancestor, MergeState.NoCommits, MergeState.PatchesApplied, MergeState.ContentContained];

        foreach (MergeState state in states)
        {
            BranchOffer offer = OfferOf(state);

            Assert.DoesNotContain("JIRA", offer.Question, StringComparison.Ordinal);
            Assert.DoesNotContain("/", offer.Question, StringComparison.Ordinal);
            Assert.True(offer.Question.Length <= 40, $"the question is {offer.Question.Length} characters: {offer.Question}");
            Assert.Contains(Branch, offer.Context, StringComparison.Ordinal);
        }
    }

    /// <summary>A branch with commits the default branch lacks is not offered.</summary>
    [Fact]
    public void UnmergedIsNotOffered() => Assert.Null(BranchOffers.For(RegisteredWith(MergeState.Unmerged(3))));

    /// <summary>A detached worktree, and one whose merge state was never read, are not offered.</summary>
    [Fact]
    public void DetachedIsNotOffered()
    {
        Assert.Null(BranchOffers.For(RegisteredWith(MergeState.Detached(contained: true))));
        Assert.Null(BranchOffers.For(RegisteredWith(MergeState.Detached(contained: false))));
        Assert.Null(BranchOffers.For(RegisteredWith(state: null)));
    }

    /// <summary>A merged branch, or one with no commits of its own, goes with <c>-d</c>; the other two need <c>-D</c>.</summary>
    [Fact]
    public void ForceFollowsTheMergeState()
    {
        Assert.False(OfferOf(MergeState.Ancestor).Force);
        Assert.False(OfferOf(MergeState.NoCommits).Force);
        Assert.True(OfferOf(MergeState.PatchesApplied).Force);
        Assert.True(OfferOf(MergeState.ContentContained).Force);
    }

    /// <summary>A registered worktree on <see cref="Branch"/> whose merge state is <paramref name="state"/>, over <c>main</c>.</summary>
    /// <param name="state">Its merge state; <see langword="null"/> when it was never read.</param>
    /// <returns>The candidate.</returns>
    private static RegisteredCandidate RegisteredWith(MergeState? state) =>
        ReportSamples.Registered("yaat.wt/narrow", Branch, new WorktreeSignals { MergeState = state, MergeStateAgainst = "main" });

    /// <summary>The offer for a worktree whose merge state is <paramref name="state"/>.</summary>
    /// <param name="state">Its merge state.</param>
    /// <returns>The offer.</returns>
    private static BranchOffer OfferOf(MergeState state) => Assert.IsType<BranchOffer>(BranchOffers.For(RegisteredWith(state)));
}
