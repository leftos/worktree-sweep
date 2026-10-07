using System.Diagnostics;
using System.Runtime.ExceptionServices;
using WorktreeSweep.Discovery;
using WorktreeSweep.Git;

namespace WorktreeSweep.Signals;

/// <summary>The merge state half of <see cref="SignalReader"/>.</summary>
public static partial class SignalReader
{
    private static readonly string[] NoCommitPrefixes = ["reset: moving to ", "branch: Renamed ", "rebase (finish): ", "rebase -i (finish): "];

    /// <summary>
    /// The best merge state of a worktree over its repo's default branches, with the default that gave it. A branch is measured
    /// against the local default, then the origin one, and the better result (by rank) is kept; a default that git fails to
    /// measure is skipped with a traced warning when another one gives a state.
    /// </summary>
    /// <param name="dir">The worktree.</param>
    /// <param name="defaults">The default branches of its repo.</param>
    /// <param name="record">The worktree's record.</param>
    /// <returns>The state and the default's short name; <see langword="null"/> when the repo has no default branch.</returns>
    /// <exception cref="GitTimeoutException">A git read times out; it is not retried against another default.</exception>
    /// <exception cref="GitException">A git command fails unexpectedly; for a branch, against every default.</exception>
    /// <exception cref="IOException">The scratch object directory merge-tree writes to cannot be created.</exception>
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
        (MergeState State, string Against)? best = BestState(dir, branchRef, refs);
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

    /// <summary>
    /// The best merge state of a branch over the default refs, stopping at the first that has it as an ancestor. A git failure
    /// against one default is traced as a warning when another default gives a state, and rethrown when none does; a git timeout is
    /// neither retried nor traced, because it means the volume is stalled. Every merge-tree of the call writes to one scratch object
    /// directory, created on first use and deleted before this returns.
    /// </summary>
    /// <exception cref="GitTimeoutException">A read times out; the remaining defaults are not tried.</exception>
    /// <exception cref="GitException">Measuring fails against every default; the first failure is rethrown.</exception>
    /// <exception cref="IOException">The scratch object directory cannot be created.</exception>
    private static (MergeState State, string Against)? BestState(string dir, string branchRef, IReadOnlyList<(string Name, string Ref)> refs)
    {
        using var scratch = new LazyScratch(dir);
        return BestState(refs, target => StateAgainst(dir, branchRef, target, scratch));
    }

    /// <summary>The best merge state of a branch over the default refs, reading each ref with <paramref name="stateAgainst"/>.</summary>
    /// <param name="refs">The default refs, local first.</param>
    /// <param name="stateAgainst">The branch's state against one default ref.</param>
    /// <returns>The state and the default's short name; <see langword="null"/> when no read gave a state.</returns>
    /// <exception cref="GitTimeoutException">A read times out; the remaining defaults are not tried.</exception>
    /// <exception cref="GitException">Every read failed; the first failure is rethrown.</exception>
    internal static (MergeState State, string Against)? BestState(
        IReadOnlyList<(string Name, string Ref)> refs,
        Func<string, MergeState> stateAgainst
    )
    {
        (MergeState State, string Against)? best = null;
        GitException? firstFailure = null;
        foreach ((string Name, string Ref) target in refs)
        {
            MergeState state;
            try
            {
                state = stateAgainst(target.Ref);
            }
            catch (GitException error) when (error is not GitTimeoutException)
            {
                firstFailure ??= error;
                continue;
            }
            if (best is null || state.Rank.CompareTo(best.Value.State.Rank) < 0)
            {
                best = (state, target.Name);
            }
            if (state == MergeState.Ancestor)
            {
                break;
            }
        }
        return BestDespiteFailure(best, firstFailure);
    }

    /// <summary>The best state when there is one, tracing the failure beside it; the failure itself when there is no state. A git
    /// timeout never reaches it: it propagates from the read that raised it.</summary>
    private static (MergeState State, string Against)? BestDespiteFailure((MergeState State, string Against)? best, GitException? failure)
    {
        if (failure is null)
        {
            return best;
        }
        if (best is null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
        Trace.TraceWarning($"{failure.Message}; the merge state comes from {best.Value.Against} instead");
        return best;
    }

    /// <summary>
    /// The merge state of a detached HEAD: contained against the first default that reaches it, else not contained against the
    /// first default that answered. A failure against one default is handled as in <see cref="BestState"/>, a timeout included: it
    /// propagates at once instead of trying the next default.
    /// </summary>
    /// <exception cref="GitTimeoutException">A read times out; the remaining defaults are not tried.</exception>
    /// <exception cref="GitException">Measuring fails against every default; the first failure is rethrown.</exception>
    private static (MergeState State, string Against)? DetachedState(string dir, IReadOnlyList<(string Name, string Ref)> refs, string head)
    {
        string? firstAnswered = null;
        GitException? firstFailure = null;
        foreach ((string Name, string Ref) target in refs)
        {
            bool contained;
            try
            {
                contained = IsAncestor(dir, head, target.Ref);
            }
            catch (GitException error) when (error is not GitTimeoutException)
            {
                firstFailure ??= error;
                continue;
            }
            if (contained)
            {
                return BestDespiteFailure((MergeState.Detached(contained: true), target.Name), firstFailure);
            }
            firstAnswered ??= target.Name;
        }
        (MergeState State, string Against)? notContained = firstAnswered is null ? null : (MergeState.Detached(contained: false), firstAnswered);
        return BestDespiteFailure(notContained, firstFailure);
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

    private static MergeState StateAgainst(string dir, string branch, string defaultRef, LazyScratch scratch)
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
        if (ContentContained(dir, branch, defaultRef, scratch))
        {
            return MergeState.ContentContained;
        }
        return MergeState.Unmerged(Count(dir, $"{defaultRef}..{branch}"));
    }

    private static bool IsAncestor(string dir, string commit, string defaultRef) =>
        ExitIsAnswer(dir, ["merge-base", "--is-ancestor", commit, defaultRef]);

    private static bool ShareHistory(string dir, string defaultRef, string branch) => ExitIsAnswer(dir, ["merge-base", defaultRef, branch]);

    /// <summary>
    /// Whether <c>git merge-tree --write-tree {default} {branch}</c> gives the default branch's own tree; a branch that shares no
    /// history with the default is not contained, and merge-tree is not run. The objects the merge writes go to the call's scratch
    /// object directory, so the repo is left untouched.
    /// </summary>
    /// <exception cref="GitException">merge-base exits with a code other than 0 (a base) or 1 (none), the default branch's tree
    /// cannot be read, merge-tree exits with a code other than 0 (merged) or 1 (conflicts), or git cannot be started, does not exit
    /// within <see cref="GitRunner.CallTimeout"/> or leaves its output open.</exception>
    /// <exception cref="IOException">The scratch object directory cannot be created.</exception>
    private static bool ContentContained(string dir, string branch, string defaultRef, LazyScratch scratch)
    {
        if (!ShareHistory(dir, defaultRef, branch))
        {
            return false;
        }
        string defaultTree = GitRunner.Run(dir, ["rev-parse", $"{defaultRef}^{{tree}}"]);
        ScratchObjects objects = scratch.Get();
        string[] args = ["merge-tree", "--write-tree", defaultRef, branch];
        var env = new Dictionary<string, string> { ["GIT_OBJECT_DIRECTORY"] = objects.Dir, ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = objects.Alternate };
        GitStatus status = GitRunner.RunStatusWithEnv(dir, args, env);
        return status.Code switch
        {
            0 => Lines(status.Stdout).FirstOrDefault() == defaultTree,
            1 => false,
            _ => throw GitRunner.Failure(dir, args, status),
        };
    }

    /// <summary>A worktree's scratch object directory, created on first use and deleted on dispose if it was created.</summary>
    /// <param name="worktree">The worktree whose repo the directory borrows objects from.</param>
    private sealed class LazyScratch(string worktree) : IDisposable
    {
        private ScratchObjects? scratch;

        /// <summary>The scratch object directory, created on the first call.</summary>
        /// <returns>The directory.</returns>
        /// <exception cref="GitException">Git cannot find the repo's common dir.</exception>
        /// <exception cref="IOException">The directory cannot be created.</exception>
        public ScratchObjects Get() => scratch ??= ScratchObjects.Create(worktree);

        /// <inheritdoc/>
        public void Dispose() => scratch?.Dispose();
    }
}
