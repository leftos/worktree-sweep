namespace WorktreeSweep.Discovery;

/// <summary>A repo found at depth 1 of the root.</summary>
/// <param name="Path">The repo's main worktree folder.</param>
/// <param name="Worktrees">Every worktree git lists for it; the first is the main worktree.</param>
public sealed record Repo(string Path, IReadOnlyList<WorktreeRecord> Worktrees);
