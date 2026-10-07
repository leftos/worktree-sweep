using System.Diagnostics;
using WorktreeSweep.Discovery;
using WorktreeSweep.Git;

namespace WorktreeSweep.Signals;

/// <summary>The merge state half of <see cref="SignalReader"/>.</summary>
public static partial class SignalReader
{
    private static readonly string[] NoCommitPrefixes = ["reset: moving to ", "branch: Renamed ", "rebase (finish): ", "rebase -i (finish): "];

    /// <summary>
    /// The best merge state of a worktree over its repo's default branches, with the default that gave it. A branch is measured
    /// against the local default, then the origin one, and the better result (by rank) is kept.
    /// </summary>
    /// <param name="dir">The worktree.</param>
    /// <param name="defaults">The default branches of its repo.</param>
    /// <param name="record">The worktree's record.</param>
    /// <returns>The state and the default's short name; <see langword="null"/> when the repo has no default branch.</returns>
    /// <exception cref="GitException">A git command fails unexpectedly.</exception>
    public static (MergeState State, string Against)? ReadMergeState(string dir, DefaultBranches defaults, WorktreeRecord record)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(record);
        IReadOnlyList<(string Name, string Ref)> refs = defaults.Refs();
        if (record.Branch is not { } branch)
        {
            return DetachedState(dir, refs, record.Head ?? "HEAD");
        }
        string branchRef = $"refs/heads/{branch}";
        (MergeState State, string Against)? best = null;
        foreach ((string Name, string Ref) target in refs)
        {
            MergeState state = StateAgainst(dir, branchRef, target.Ref);
            if (best is null || state.Rank.CompareTo(best.Value.State.Rank) < 0)
            {
                best = (state, target.Name);
            }
            if (state == MergeState.Ancestor)
            {
                break;
            }
        }
        if (best is { State.Kind: MergeStateKind.Ancestor } found && BranchHasNoOwnCommits(dir, branchRef))
        {
            return (MergeState.NoCommits, found.Against);
        }
        return best;
    }

    /// <summary>
    /// Whether a branch's reflog subjects (<c>git reflog show --format=%gs</c>, newest first) prove no commit was made on the branch:
    /// the oldest entry is its creation, so the history is complete, and every later entry is a reset, a rename, a finished rebase or
    /// a fast-forward merge or pull. Anything else, an empty reflog included, is <see langword="false"/>.
    /// </summary>
    /// <param name="reflog">The reflog subjects, one per line.</param>
    /// <returns><see langword="true"/> when they prove no commit was made.</returns>
    public static bool HasNoOwnCommits(string reflog)
    {
        ArgumentNullException.ThrowIfNull(reflog);
        List<string> entries = [.. Lines(reflog)];
        if (entries.Count == 0)
        {
            return false;
        }
        return entries[^1].StartsWith("branch: Created from", StringComparison.Ordinal) && entries.SkipLast(1).All(IsNoCommitEntry);
    }

    private static (MergeState State, string Against)? DetachedState(string dir, IReadOnlyList<(string Name, string Ref)> refs, string head)
    {
        foreach ((string Name, string Ref) target in refs)
        {
            if (IsAncestor(dir, head, target.Ref))
            {
                return (MergeState.Detached(contained: true), target.Name);
            }
        }
        return refs.Count > 0 ? (MergeState.Detached(contained: false), refs[0].Name) : null;
    }

    private static bool BranchHasNoOwnCommits(string dir, string branchRef) =>
        HasNoOwnCommits(GitRunner.Run(dir, ["reflog", "show", "--format=%gs", branchRef]));

    private static bool IsNoCommitEntry(string entry)
    {
        bool fastForward =
            (entry.StartsWith("merge ", StringComparison.Ordinal) || entry.StartsWith("pull", StringComparison.Ordinal))
            && entry.EndsWith(": Fast-forward", StringComparison.Ordinal);
        return fastForward || NoCommitPrefixes.Any(prefix => entry.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static MergeState StateAgainst(string dir, string branch, string defaultRef)
    {
        if (IsAncestor(dir, branch, defaultRef))
        {
            return MergeState.Ancestor;
        }
        string cherry = GitRunner.Run(dir, ["cherry", defaultRef, branch]);
        if (cherry.Length > 0 && !Lines(cherry).Any(line => line.StartsWith('+')))
        {
            return MergeState.PatchesApplied;
        }
        if (ContentContained(dir, branch, defaultRef))
        {
            return MergeState.ContentContained;
        }
        return MergeState.Unmerged(Count(dir, $"{defaultRef}..{branch}"));
    }

    private static bool IsAncestor(string dir, string commit, string defaultRef) =>
        ExitIsAnswer(dir, ["merge-base", "--is-ancestor", commit, defaultRef]);

    /// <summary>
    /// Whether <c>git merge-tree --write-tree {default} {branch}</c> gives the default branch's own tree. The objects the merge writes
    /// go to a scratch object directory, so the repo is left untouched.
    /// </summary>
    /// <exception cref="GitException">The default branch's tree cannot be read, or git cannot be started, does not exit within
    /// <see cref="GitRunner.CallTimeout"/> or leaves its output open; a merge-tree exit code other than 0 or 1 is traced and read
    /// as not contained.</exception>
    private static bool ContentContained(string dir, string branch, string defaultRef)
    {
        string defaultTree = GitRunner.Run(dir, ["rev-parse", $"{defaultRef}^{{tree}}"]);
        using var scratch = ScratchObjects.Create(dir);
        string[] args = ["merge-tree", "--write-tree", defaultRef, branch];
        var env = new Dictionary<string, string> { ["GIT_OBJECT_DIRECTORY"] = scratch.Dir, ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = scratch.Alternate };
        GitStatus status = GitRunner.RunStatusWithEnv(dir, args, env);
        switch (status.Code)
        {
            case 0:
                return Lines(status.Stdout).FirstOrDefault() == defaultTree;
            case 1:
                return false;
            default:
                Trace.WriteLine($"{GitRunner.Failure(dir, args, status).Message}; treating the branch as not content-contained");
                return false;
        }
    }
}
