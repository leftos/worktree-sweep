using WorktreeSweep.Report;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Agent;

/// <summary>What an agent removal does with a worktree's branch once the worktree is gone.</summary>
public abstract record AgentBranch
{
    private protected AgentBranch() { }

    /// <summary>
    /// The branch follow-up: a branch merged into the default branch, or with no commits of its own, is deleted with
    /// <c>git branch -d</c>; a cherry-picked or squash-merged one is kept with a note, since <c>git branch -d</c> refuses it; any
    /// other branch, an unknown merge state included, is kept silently, as is a detached HEAD.
    /// </summary>
    /// <param name="candidate">The worktree.</param>
    /// <returns>The follow-up.</returns>
    public static AgentBranch For(RegisteredCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Record.Branch is not { } branch)
        {
            return new Keep(null);
        }
        return candidate.Signals.MergeState?.Kind switch
        {
            MergeStateKind.Ancestor or MergeStateKind.NoCommits => new Delete(branch),
            MergeStateKind.PatchesApplied => new Keep($"branch {branch} kept: cherry-picked, delete it with git branch -D"),
            MergeStateKind.ContentContained => new Keep($"branch {branch} kept: squash-merged, delete it with git branch -D"),
            _ => new Keep(null),
        };
    }

    /// <summary>The branch is deleted with <c>git branch -d</c>.</summary>
    /// <param name="Branch">The branch, without <c>refs/heads/</c>.</param>
    public sealed record Delete(string Branch) : AgentBranch;

    /// <summary>The branch is kept.</summary>
    /// <param name="Note">The note telling the caller why and how to delete it; <see langword="null"/> when kept silently.</param>
    public sealed record Keep(string? Note) : AgentBranch;
}
