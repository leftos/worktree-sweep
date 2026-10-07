using System.Diagnostics;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Removal;

/// <summary>Builds the offer to delete a removed worktree's branch.</summary>
public static class BranchOffers
{
    /// <summary>
    /// The branch deletion offered for a registered worktree: merged branches and branches with no commits of their own with
    /// <c>-d</c>, cherry-picked and content-contained ones with <c>-D</c>; <see langword="null"/> for an unmerged or detached worktree,
    /// one with no branch, or one whose merge state is unknown.
    /// </summary>
    /// <param name="registered">The worktree.</param>
    /// <returns>The offer, or <see langword="null"/> when no branch deletion applies.</returns>
    public static BranchOffer? For(RegisteredCandidate registered)
    {
        ArgumentNullException.ThrowIfNull(registered);
        string? branch = registered.Record.Branch;
        MergeState? mergeState = registered.Signals.MergeState;
        if (branch is null || mergeState is null)
        {
            return null;
        }
        string against = registered.Signals.MergeStateAgainst ?? "the default branch";
        return mergeState.Kind switch
        {
            MergeStateKind.Ancestor => Offer(branch, force: false, $"it is merged into {against}."),
            MergeStateKind.NoCommits => Offer(branch, force: false, "it has no commits of its own."),
            MergeStateKind.PatchesApplied => Offer(
                branch,
                force: true,
                $"every commit on it is cherry-picked onto {against}; git branch -d refuses it, so this uses git branch -D."
            ),
            MergeStateKind.ContentContained => Offer(
                branch,
                force: true,
                $"its changes are already in {against} (squash-merged); git branch -d refuses it, so this uses git branch -D."
            ),
            MergeStateKind.Unmerged or MergeStateKind.Detached => null,
            _ => throw new UnreachableException($"unknown merge state kind {mergeState.Kind}"),
        };
    }

    /// <summary>The offer for <paramref name="branch"/>, which can go for <paramref name="reason"/>.</summary>
    /// <param name="branch">The branch.</param>
    /// <param name="force">Whether it needs <c>git branch -D</c>.</param>
    /// <param name="reason">Why it can go, as the clause after the branch's name.</param>
    /// <returns>The offer.</returns>
    private static BranchOffer Offer(string branch, bool force, string reason) =>
        new(branch, force, $"Branch {branch}: {reason}", "Delete the branch?");
}
