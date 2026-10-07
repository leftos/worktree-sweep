using WorktreeSweep.Report;

namespace WorktreeSweep.Agent;

/// <summary>What a path given for removal resolves to: a registered linked worktree, or the reason it is not one.</summary>
public abstract record Resolution
{
    private protected Resolution() { }

    /// <summary>The path is the root of a linked worktree some repo registers, or of a prunable registration of one.</summary>
    /// <param name="Candidate">The worktree, its signals read.</param>
    /// <param name="MainWorktree">The main worktree of the repo that registers it (the bare folder for a bare repo).</param>
    public sealed record Resolved(RegisteredCandidate Candidate, string MainWorktree) : Resolution;

    /// <summary>The path is not a removable worktree.</summary>
    /// <param name="Reason">Why it is not.</param>
    /// <param name="Repo">The main worktree of the repo the path was found in; <see langword="null"/> when none was.</param>
    public sealed record Refusal(RefusalReason Reason, string? Repo) : Resolution;
}
