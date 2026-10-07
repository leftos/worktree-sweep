namespace WorktreeSweep.Signals;

/// <summary>
/// The signals of one registered worktree. Every value is <see langword="null"/> for a prunable registration, whose folder is gone.
/// </summary>
public sealed record WorktreeSignals
{
    /// <summary>Gets the best merge state over the local and origin default.</summary>
    public MergeState? MergeState { get; init; }

    /// <summary>Gets the default branch that gave <see cref="MergeState"/>.</summary>
    public string? MergeStateAgainst { get; init; }

    /// <summary>Gets the uncommitted work.</summary>
    public Dirty? Dirty { get; init; }

    /// <summary>Gets the upstream state; <see langword="null"/> when detached.</summary>
    public Upstream? Upstream { get; init; }

    /// <summary>Gets the later of the HEAD commit time and the worktree index's modification time, in Unix seconds.</summary>
    public long? LastActivityUnix { get; init; }

    /// <summary>Gets the disk usage of the worktree folder.</summary>
    public SizeInfo? Size { get; init; }

    /// <summary>Gets the signals that could not be read, one message each; each one's value is left <see langword="null"/>.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}
