namespace WorktreeSweep.Agent;

/// <summary>What an agent's removal did.</summary>
public enum RemoveStatus
{
    /// <summary>The worktree is gone and its registration pruned.</summary>
    Removed,

    /// <summary>The worktree is still there and marked released for the next interactive sweep.</summary>
    Released,

    /// <summary>Nothing was touched.</summary>
    Refused,
}
