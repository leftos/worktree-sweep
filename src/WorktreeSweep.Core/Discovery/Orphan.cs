namespace WorktreeSweep.Discovery;

/// <summary>A folder in a container dir that no repo under the root registers as a worktree.</summary>
public sealed record Orphan
{
    /// <summary>Gets the orphan's path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the container dir it was found in.</summary>
    public required string Container { get; init; }

    /// <summary>Gets whether it is a folder or a link.</summary>
    public required OrphanKind Kind { get; init; }

    /// <summary>Gets where a link points; <see langword="null"/> for a folder, or when the target cannot be read.</summary>
    public string? LinkTarget { get; init; }

    /// <summary>
    /// Gets a value indicating whether its <c>.git</c> is a file whose <c>gitdir:</c> target no longer exists; a target reached
    /// through a link is checked at the link's final target.
    /// </summary>
    public bool StaleGitdir { get; init; }

    /// <summary>
    /// Gets the git dir its <c>.git</c> file points to when that git dir still exists: a repo outside the root still registers the
    /// folder as a worktree. A git dir that cannot be checked (unreadable, or not a valid path) is treated as still existing.
    /// <see langword="null"/> for a link, a folder without a <c>.git</c> file, or a stale one.
    /// </summary>
    public string? LiveGitdir { get; init; }

    /// <summary>Gets a value indicating whether its <c>.git</c> is a directory that is not a reparse point.</summary>
    public bool HasGitDir { get; init; }
}
