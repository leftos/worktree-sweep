using WorktreeSweep.Git;

namespace WorktreeSweep.Discovery;

/// <summary>What <see cref="Discoverer.Discover(string, DiscoverySeams, Action{int, int}, CancellationToken)"/> resolves and lists with.</summary>
/// <param name="Resolve">Resolves the root and each registered worktree's parent folder, as <see cref="PathResolver.Resolve"/> does.</param>
/// <param name="Stalls">The volumes an earlier git call has stalled, shared with the scan's signal reads.</param>
/// <param name="ListWorktrees">Lists a repo's worktrees: the git call whose timeout stalls the repo's volume.</param>
internal sealed record DiscoverySeams(Func<string, string> Resolve, VolumeStalls Stalls, Func<string, IReadOnlyList<WorktreeRecord>> ListWorktrees);
