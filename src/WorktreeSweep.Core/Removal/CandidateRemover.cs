using System.Diagnostics;
using WorktreeSweep.Discovery;
using WorktreeSweep.Git;
using WorktreeSweep.Recycle;
using WorktreeSweep.Report;

namespace WorktreeSweep.Removal;

/// <summary>Removes one pick as its <see cref="RemoveAction"/> says: a link as a link, a folder recycled or deleted, a registration pruned.</summary>
public static class CandidateRemover
{
    /// <summary>What <see cref="FileSystemInfo.Attributes"/> returns when the path is not there.</summary>
    private const FileAttributes Missing = unchecked((FileAttributes)(-1));

    /// <summary>
    /// Carries out <paramref name="action"/> on <paramref name="candidate"/>'s folder or link. A registered worktree's git lock must
    /// already be lifted (<see cref="GitUnlock"/>). A folder bound for the Recycle Bin that has become a link since the scan is left in
    /// place: the Shell is never handed a link.
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
                RejectLink(path);
                ShellRecycler.Recycle(path);
                break;
            case RemoveAction.Delete { Method: DeleteMethod.Permanent }:
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
            _ = GitRunner.Run(registered.Repo, ["worktree", "unlock", registered.Path]);
        }
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

    /// <summary>Fails when <paramref name="path"/> is now a junction or symbolic link, which a recycle would follow.</summary>
    /// <param name="path">The folder about to be recycled.</param>
    /// <exception cref="IOException">It is a link now, or it cannot be read.</exception>
    private static void RejectLink(string path)
    {
        FileSystemInfo entry;
        try
        {
            var file = new FileInfo(path);
            FileAttributes attributes = file.Attributes;
            if (attributes == Missing)
            {
                throw new FileNotFoundException($"could not find '{path}'", path);
            }
            entry = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(path) : file;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"cannot read {path}: {error.Message}", error);
        }
        if (Discoverer.IsLink(entry))
        {
            throw new IOException($"{path} became a link since the scan; left in place");
        }
    }
}
