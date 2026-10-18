using WorktreeSweep.Report;
using WorktreeSweep.Review;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Agent;

/// <summary>What removing a worktree an agent asked for would lose, which refuses the removal unless forced.</summary>
public static class WouldLose
{
    /// <summary>
    /// What removing the worktree would lose, as the review's loss sentence: uncommitted files, commits not on the default branch, a
    /// detached HEAD not on it, unpushed commits, a git lock, or a signal that could not be read. A branch with no commits of its own
    /// is not a loss, so a worktree whose only problem is an unreadable signal names that signal alone. The worktree is named relative
    /// to its repo's parent folder.
    /// </summary>
    /// <param name="candidate">The worktree.</param>
    /// <returns>The sentence; <see langword="null"/> when nothing would be lost and every signal was read.</returns>
    public static string? Text(RegisteredCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        bool lost = LosesWork(candidate);
        bool unreadable = LossText.UnreadableSignals(candidate).Count > 0;
        if (!lost && !unreadable)
        {
            return null;
        }
        string root = Path.GetDirectoryName(candidate.Repo) ?? candidate.Repo;
        string relative = ReportTable.RelativePath(candidate.Path, root);
        return lost ? LossText.Sentence(candidate, root)?.Context : $"{relative}: {LossText.UnreadableSentence(candidate)}";
    }

    /// <summary>Whether the worktree holds uncommitted files, commits not on the default branch, unpushed commits, or a git lock.</summary>
    private static bool LosesWork(RegisteredCandidate candidate)
    {
        WorktreeSignals signals = candidate.Signals;
        bool dirty = signals.Dirty is { } counts && (counts.Modified > 0 || counts.Untracked > 0);
        bool unmerged = signals.MergeState is { Kind: MergeStateKind.Unmerged } or { Kind: MergeStateKind.Detached, Contained: false };
        bool unpushed = signals.Upstream is { Kind: UpstreamKind.Tracking, Ahead: > 0 };
        return dirty || unmerged || unpushed || candidate.Record.Locked is not null;
    }
}
