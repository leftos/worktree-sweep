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
    /// is not a loss. The worktree is named relative to its repo's parent folder.
    /// </summary>
    /// <param name="candidate">The worktree.</param>
    /// <returns>The sentence, then one naming the signals that could not be read; <see langword="null"/> when nothing would be lost.</returns>
    public static string? Text(RegisteredCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        List<string> unreadable = Unreadable(candidate);
        bool lost = LosesWork(candidate);
        if (!lost && unreadable.Count == 0)
        {
            return null;
        }
        string root = Path.GetDirectoryName(candidate.Repo) ?? candidate.Repo;
        var sentences = new List<string>();
        if (!lost)
        {
            sentences.Add($"{ReportTable.RelativePath(candidate.Path, root)}:");
        }
        else if (LossText.Sentence(candidate, root) is { } sentence)
        {
            sentences.Add(sentence.Context);
        }
        if (unreadable.Count > 0)
        {
            sentences.Add($"Its {string.Join(", ", unreadable)} could not be read.");
        }
        return string.Join(' ', sentences);
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

    /// <summary>The signals that could not be read, in sentence order; the upstream counts only for a worktree on a branch.</summary>
    private static List<string> Unreadable(RegisteredCandidate candidate)
    {
        WorktreeSignals signals = candidate.Signals;
        var unreadable = new List<string>();
        if (signals.Dirty is null)
        {
            unreadable.Add("uncommitted changes");
        }
        if (signals.MergeState is null)
        {
            unreadable.Add("merge state");
        }
        if (candidate.Record.Branch is not null && signals.Upstream is null)
        {
            unreadable.Add("upstream");
        }
        return unreadable;
    }
}
