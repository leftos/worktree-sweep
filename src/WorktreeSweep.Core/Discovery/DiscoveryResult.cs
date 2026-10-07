namespace WorktreeSweep.Discovery;

/// <summary>Everything <see cref="Discoverer.Discover"/> found under a root.</summary>
/// <param name="Root">The root, made absolute; an 8.3 root comes back in its long form, as <see cref="Path.GetFullPath(string)"/> gives it.</param>
/// <param name="Repos">Repos at depth 1 of the root.</param>
/// <param name="Containers">Container dirs that were walked for orphans.</param>
/// <param name="Orphans">Orphans found in the container dirs.</param>
/// <param name="Errors">
/// The problems discovery met while listing the repos' worktrees, one per failed list, unreadable folder or left-out worktree; see
/// <see cref="DiscoveryError"/>.
/// </param>
public sealed record DiscoveryResult(
    string Root,
    IReadOnlyList<Repo> Repos,
    IReadOnlyList<string> Containers,
    IReadOnlyList<Orphan> Orphans,
    IReadOnlyList<DiscoveryError> Errors
)
{
    /// <summary>Gets every registered worktree that is a candidate, with its repo; a repo's main worktree never is.</summary>
    public IEnumerable<(Repo Repo, WorktreeRecord Record)> Registered =>
        Repos.SelectMany(repo => repo.Worktrees.Skip(1).Select(record => (repo, record)));
}
