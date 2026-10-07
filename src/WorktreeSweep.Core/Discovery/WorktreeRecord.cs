namespace WorktreeSweep.Discovery;

/// <summary>One record of <c>git worktree list --porcelain</c>.</summary>
public sealed record WorktreeRecord
{
    /// <summary>Gets the worktree folder, with <c>\</c> separators.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the commit checked out, when git reports one.</summary>
    public string? Head { get; init; }

    /// <summary>Gets the branch checked out, without <c>refs/heads/</c>; <see langword="null"/> when detached.</summary>
    public string? Branch { get; init; }

    /// <summary>Gets a value indicating whether HEAD is detached.</summary>
    public bool Detached { get; init; }

    /// <summary>Gets a value indicating whether the record is a bare repository.</summary>
    public bool Bare { get; init; }

    /// <summary>Gets the <c>git worktree lock</c> reason; <c>""</c> when locked without one, <see langword="null"/> when not locked.</summary>
    public string? Locked { get; init; }

    /// <summary>Gets why git considers the registration prunable (its folder is gone); <see langword="null"/> when it is not.</summary>
    public string? Prunable { get; init; }
}
