using System.Diagnostics;
using WorktreeSweep.Git;
using WorktreeSweep.Recycle;
using WorktreeSweep.Report;

namespace WorktreeSweep.Removal;

/// <summary>Removes one pick as its <see cref="RemoveAction"/> says: a link as a link, a folder recycled or deleted, a registration pruned.</summary>
public static class CandidateRemover
{
    /// <summary>
    /// Carries out <paramref name="action"/> on <paramref name="candidate"/>'s folder or link. A registered worktree's git lock must
    /// already be lifted (<see cref="GitUnlock"/>). A folder to recycle or delete that has become a link since the scan is left in
    /// place: the Shell is never handed a link, and a link is never reported as a deleted folder.
    /// </summary>
    /// <param name="candidate">The pick.</param>
    /// <param name="action">How to remove it.</param>
    /// <exception cref="LockedException">A file or folder in the tree is in use.</exception>
    /// <exception cref="IOException">The removal failed for any other reason, or the pick was left in place; the message says which.</exception>
    /// <exception cref="GitException">A prune failed.</exception>
    public static void Remove(Candidate candidate, RemoveAction action)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(action);
        string path = candidate.Path;
        switch (action)
        {
            case RemoveAction.RemoveLink:
                PermanentDelete.RemoveLink(path);
                break;
            case RemoveAction.PruneRegistration:
                Prune(candidate);
                break;
            case RemoveAction.Delete { Method: DeleteMethod.Recycle }:
                PermanentDelete.RejectLink(path);
                ShellRecycler.Recycle(path);
                break;
            case RemoveAction.Delete { Method: DeleteMethod.Permanent }:
                PermanentDelete.RejectLink(path);
                PermanentDelete.Delete(path);
                break;
            default:
                throw new UnreachableException($"unknown remove action {action}");
        }
    }

    /// <summary>Lifts a registered worktree's git lock with <c>git worktree unlock</c>; does nothing when it is not git-locked.</summary>
    /// <param name="registered">The worktree.</param>
    /// <exception cref="GitException">Git failed.</exception>
    public static void GitUnlock(RegisteredCandidate registered)
    {
        ArgumentNullException.ThrowIfNull(registered);
        if (registered.Record.Locked is not null)
        {
            _ = GitRunner.Run(registered.Repo, ["worktree", "unlock", registered.Record.Path]);
        }
    }

    /// <summary>
    /// Puts back a registered worktree's git lock, with its reason when it had one, after <see cref="GitUnlock"/> lifted it and the
    /// removal did not go through; does nothing when it was not git-locked.
    /// </summary>
    /// <param name="registered">The worktree, as the scan saw it.</param>
    /// <exception cref="GitException">Git failed.</exception>
    public static void GitRelock(RegisteredCandidate registered)
    {
        ArgumentNullException.ThrowIfNull(registered);
        string? reason = registered.Record.Locked;
        if (reason is null)
        {
            return;
        }
        string worktree = registered.Record.Path;
        string[] args = reason.Length == 0 ? ["worktree", "lock", worktree] : ["worktree", "lock", "--reason", reason, worktree];
        _ = GitRunner.Run(registered.Repo, args);
    }

    /// <summary>Runs <c>git worktree prune</c> in <paramref name="repo"/>.</summary>
    /// <param name="repo">The repo's main worktree.</param>
    /// <exception cref="GitException">Git failed.</exception>
    public static void PruneRepo(string repo)
    {
        ArgumentException.ThrowIfNullOrEmpty(repo);
        _ = GitRunner.Run(repo, ["worktree", "prune"]);
    }

    /// <summary>Prunes a registered worktree's repo; an orphan has no registration to prune.</summary>
    /// <param name="candidate">The pick.</param>
    private static void Prune(Candidate candidate)
    {
        if (candidate is not RegisteredCandidate registered)
        {
            throw new IOException($"{candidate.Path} is not a registered worktree; there is nothing to prune");
        }
        PruneRepo(registered.Repo);
    }
}
