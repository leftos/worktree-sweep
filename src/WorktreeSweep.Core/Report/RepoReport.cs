using WorktreeSweep.Signals;

namespace WorktreeSweep.Report;

/// <summary>A repo and its default branches.</summary>
public sealed record RepoReport
{
    /// <summary>Gets the repo's main worktree.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the branches merge states are measured against.</summary>
    public required DefaultBranches DefaultBranches { get; init; }
}
