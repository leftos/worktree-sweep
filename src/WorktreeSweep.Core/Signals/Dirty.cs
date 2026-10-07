namespace WorktreeSweep.Signals;

/// <summary>Uncommitted work in a worktree; ignored files are not counted.</summary>
public sealed record Dirty
{
    /// <summary>Gets the tracked entries with staged or unstaged changes.</summary>
    public int Modified { get; init; }

    /// <summary>Gets the untracked entries; an untracked folder counts once.</summary>
    public int Untracked { get; init; }
}
