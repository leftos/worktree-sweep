namespace WorktreeSweep.Discovery;

/// <summary>A problem discovery met while listing a repo's worktrees; a worktree it hides would otherwise look like an orphan.</summary>
/// <param name="Repo">The repo folder the problem is in.</param>
/// <param name="Path">
/// What the problem is about: the repo itself for a failed list, else the <c>gitdir</c> file or the <c>worktrees</c> folder.
/// </param>
/// <param name="Message">What went wrong.</param>
public sealed record DiscoveryError(string Repo, string Path, string Message);
