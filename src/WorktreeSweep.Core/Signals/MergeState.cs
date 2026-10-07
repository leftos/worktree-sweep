namespace WorktreeSweep.Signals;

/// <summary>How far a worktree's branch has made it into the default branch.</summary>
public sealed record MergeState
{
    private MergeState() { }

    /// <summary>Gets the branch tip is reachable from the default branch.</summary>
    public static MergeState Ancestor { get; } = new() { Kind = MergeStateKind.Ancestor };

    /// <summary>Gets the branch tip is reachable from the default branch and its reflog records no commit made on it.</summary>
    public static MergeState NoCommits { get; } = new() { Kind = MergeStateKind.NoCommits };

    /// <summary>Gets every commit on the branch has a patch-equivalent commit on the default branch.</summary>
    public static MergeState PatchesApplied { get; } = new() { Kind = MergeStateKind.PatchesApplied };

    /// <summary>Gets merging the branch into the default branch would change nothing.</summary>
    public static MergeState ContentContained { get; } = new() { Kind = MergeStateKind.ContentContained };

    /// <summary>Gets which state this is.</summary>
    public MergeStateKind Kind { get; private init; }

    /// <summary>Gets, for <see cref="MergeStateKind.Unmerged"/>, the commits on the branch that are not on the default branch; 0 otherwise.</summary>
    public int Commits { get; private init; }

    /// <summary>Gets a value indicating whether, for <see cref="MergeStateKind.Detached"/>, HEAD is reachable from a default branch.</summary>
    public bool Contained { get; private init; }

    /// <summary>
    /// Gets the rank used to keep the best result over the local and origin default: lower is better, and among unmerged results the
    /// fewer commits the better.
    /// </summary>
    internal (int Tier, int Commits) Rank =>
        Kind switch
        {
            MergeStateKind.Ancestor or MergeStateKind.NoCommits => (0, 0),
            MergeStateKind.Detached => Contained ? (0, 0) : (4, 0),
            MergeStateKind.PatchesApplied => (1, 0),
            MergeStateKind.ContentContained => (2, 0),
            MergeStateKind.Unmerged => (3, Commits),
            _ => throw new InvalidOperationException($"unknown merge state kind {Kind}"),
        };

    /// <summary>The branch has commits the default branch lacks.</summary>
    /// <param name="commits">Commits on the branch that are not on the default branch.</param>
    /// <returns>The state.</returns>
    public static MergeState Unmerged(int commits) => new() { Kind = MergeStateKind.Unmerged, Commits = commits };

    /// <summary>HEAD is detached.</summary>
    /// <param name="contained">Whether HEAD is reachable from a default branch.</param>
    /// <returns>The state.</returns>
    public static MergeState Detached(bool contained) => new() { Kind = MergeStateKind.Detached, Contained = contained };
}
