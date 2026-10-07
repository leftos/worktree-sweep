using System.Diagnostics;
using WorktreeSweep.Git;
using WorktreeSweep.Report;

namespace WorktreeSweep.Removal;

/// <summary>What runs once a pick is gone: the prune, the branch, and the reminder about an orphan still registered elsewhere.</summary>
public static class FollowUps
{
    /// <summary>
    /// The follow-ups once <paramref name="candidate"/> is gone, returned as notes for the summary: a registered worktree's registration
    /// is pruned (unless <paramref name="action"/> was the prune itself) and <paramref name="branch"/> carried out (deleted, or noted as
    /// kept); an orphan still registered elsewhere gets a reminder. A git failure is a note, never an exception.
    /// </summary>
    /// <param name="candidate">The removed pick.</param>
    /// <param name="action">How it was removed.</param>
    /// <param name="branch">What to do with its branch.</param>
    /// <returns>The notes, in the order they happened; empty when there is nothing to say.</returns>
    public static IReadOnlyList<string> AfterRemoved(Candidate candidate, RemoveAction action, BranchChoice branch)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(branch);
        return candidate switch
        {
            RegisteredCandidate registered => AfterRegistered(registered, pruned: action is RemoveAction.PruneRegistration, branch),
            OrphanCandidate { Orphan.LiveGitdir: string gitdir } => [$"still registered at {gitdir}; `git worktree prune` in that repo clears it"],
            OrphanCandidate => [],
            _ => throw new UnreachableException($"unknown candidate {candidate}"),
        };
    }

    /// <summary>
    /// Deletes <paramref name="branch"/> in <paramref name="repo"/> with <c>git branch -d</c>, or <c>-D</c> when <paramref name="force"/>.
    /// </summary>
    /// <param name="repo">The repo's main worktree.</param>
    /// <param name="branch">The branch.</param>
    /// <param name="force">Whether to use <c>-D</c>.</param>
    /// <exception cref="GitException">
    /// Git refused, for example because the branch is checked out in another worktree; the message carries git's.
    /// </exception>
    public static void DeleteBranch(string repo, string branch, bool force)
    {
        ArgumentException.ThrowIfNullOrEmpty(repo);
        ArgumentException.ThrowIfNullOrEmpty(branch);
        _ = GitRunner.Run(repo, ["branch", force ? "-D" : "-d", branch]);
    }

    /// <summary>Prunes the worktree's repo unless the removal already did, then deletes or keeps its branch.</summary>
    /// <param name="registered">The removed worktree.</param>
    /// <param name="pruned">Whether the removal was the prune itself.</param>
    /// <param name="branch">What to do with its branch.</param>
    /// <returns>The notes.</returns>
    private static List<string> AfterRegistered(RegisteredCandidate registered, bool pruned, BranchChoice branch)
    {
        List<string> notes = [];
        if (!pruned)
        {
            notes.AddRange(Prune(registered.Repo));
        }
        switch (branch)
        {
            case BranchChoice.Delete delete:
                notes.Add(Delete(registered.Repo, delete.Offer));
                break;
            case BranchChoice.Keep keep:
                notes.Add($"branch {keep.Offer.Branch} kept");
                break;
            case BranchChoice.NotOffered:
                break;
            default:
                throw new UnreachableException($"unknown branch choice {branch}");
        }
        return notes;
    }

    /// <summary>Runs <c>git worktree prune</c> in <paramref name="repo"/>.</summary>
    /// <param name="repo">The repo's main worktree.</param>
    /// <returns>No note when it worked; <c>prune failed: &lt;why&gt;</c> when git failed.</returns>
    private static IEnumerable<string> Prune(string repo)
    {
        try
        {
            CandidateRemover.PruneRepo(repo);
            return [];
        }
        catch (GitException error)
        {
            return [$"prune failed: {error.Message}"];
        }
    }

    /// <summary>Deletes the offered branch and says how that went.</summary>
    /// <param name="repo">The repo's main worktree.</param>
    /// <param name="offer">The offer the user accepted.</param>
    /// <returns><c>branch &lt;name&gt; deleted</c>, or <c>branch &lt;name&gt; kept: &lt;why&gt;</c> when git refused.</returns>
    private static string Delete(string repo, BranchOffer offer)
    {
        try
        {
            DeleteBranch(repo, offer.Branch, offer.Force);
            return $"branch {offer.Branch} deleted";
        }
        catch (GitException error)
        {
            return $"branch {offer.Branch} kept: {error.Message}";
        }
    }
}
