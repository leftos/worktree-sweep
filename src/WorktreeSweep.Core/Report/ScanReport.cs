using WorktreeSweep.Discovery;

namespace WorktreeSweep.Report;

/// <summary>Everything a scan found, as <c>--list</c> and <c>--json</c> print it.</summary>
public sealed record ScanReport
{
    /// <summary>Gets the folder scanned.</summary>
    public required string Root { get; init; }

    /// <summary>Gets the repos at depth 1 of the root.</summary>
    public required IReadOnlyList<RepoReport> Repos { get; init; }

    /// <summary>
    /// Gets the problems discovery met while listing the repos' worktrees, one per failed list, unreadable folder or left-out
    /// worktree; see <see cref="DiscoveryError"/>.
    /// </summary>
    public required IReadOnlyList<DiscoveryError> DiscoveryErrors { get; init; }

    /// <summary>Gets the folders the tool offers to remove, in the order the scan found them.</summary>
    public required IReadOnlyList<Candidate> Candidates { get; init; }

    /// <summary>
    /// The candidates in table order: released worktrees, then the other registered worktrees, each group by repo and path, then
    /// orphans by path. Paths compare by <see cref="Discoverer.PathKey"/> under <see cref="StringComparer.Ordinal"/>.
    /// </summary>
    /// <returns>The candidates, ordered.</returns>
    public IReadOnlyList<Candidate> Ordered() =>
        [
            .. Candidates
                .OrderBy(Group)
                .ThenBy(RepoKey, StringComparer.Ordinal)
                .ThenBy(candidate => Discoverer.PathKey(candidate.Path), StringComparer.Ordinal),
        ];

    private static int Group(Candidate candidate) =>
        candidate switch
        {
            RegisteredCandidate { Released: not null } => 0,
            RegisteredCandidate => 1,
            _ => 2,
        };

    private static string RepoKey(Candidate candidate) => candidate is RegisteredCandidate registered ? Discoverer.PathKey(registered.Repo) : "";
}
