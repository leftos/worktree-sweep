using System.Diagnostics;
using WorktreeSweep.Discovery;
using WorktreeSweep.Git;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Scan;

/// <summary>Scans a root folder for stale worktrees and orphan folders.</summary>
public static class Scanner
{
    /// <summary>
    /// Scans <paramref name="root"/>: discovers repos, registered worktrees and orphans, then reads every repo's and every
    /// candidate's signals in parallel. Read-only: nothing under <paramref name="root"/> is written.
    /// </summary>
    /// <remarks>
    /// A repo's main worktree is never a candidate. A repo whose default branches or common git dir git cannot find is still
    /// scanned, with a trace warning: its candidates get no default branches to compare against, or no released marker.
    /// </remarks>
    /// <param name="root">The folder to scan.</param>
    /// <returns>The report, its candidates registered worktrees first, then orphans, each in discovery order.</returns>
    /// <exception cref="IOException"><paramref name="root"/> itself cannot be listed; the message names it.</exception>
    /// <exception cref="ArgumentException"><paramref name="root"/> is empty or not a valid path.</exception>
    public static ScanReport Scan(string root)
    {
        DiscoveryResult discovery = Discoverer.Discover(root);
        IReadOnlyList<RepoLookup> repos = SignalReader.ParallelMap(discovery.Repos, LookUp);

        var jobs = new List<ScanJob>();
        foreach (RepoLookup repo in repos)
        {
            jobs.AddRange(repo.Repo.Worktrees.Skip(1).Select(record => new RegisteredJob(repo, record)));
        }
        jobs.AddRange(discovery.Orphans.Select(orphan => new OrphanJob(orphan)));

        return new ScanReport
        {
            Root = discovery.Root,
            Repos = [.. repos.Select(repo => new RepoReport { Path = repo.Repo.Path, DefaultBranches = repo.Defaults })],
            DiscoveryErrors = discovery.Errors,
            Candidates = SignalReader.ParallelMap(jobs, ReadCandidate),
        };
    }

    /// <summary>A repo's default branches and, when it has a linked worktree, its common git dir, where released markers are found.</summary>
    private static RepoLookup LookUp(Repo repo) =>
        new(repo, DefaultBranchesOrNone(repo.Path), repo.Worktrees.Count > 1 ? CommonDirOrNone(repo.Path) : null);

    private static Candidate ReadCandidate(ScanJob job) =>
        job switch
        {
            RegisteredJob registered => new RegisteredCandidate
            {
                Record = registered.Record,
                Repo = registered.Repo.Repo.Path,
                Released = ReleasedMarker.Find(registered.Repo.CommonDir, registered.Record.Path),
                Signals = SignalReader.ReadWorktreeSignals(registered.Repo.Defaults, registered.Record),
            },
            OrphanJob orphan => new OrphanCandidate { Orphan = orphan.Orphan, Size = SignalReader.WalkSize(orphan.Orphan.Path) },
            _ => throw new UnreachableException($"unknown scan job {job}"),
        };

    private static DefaultBranches DefaultBranchesOrNone(string repo)
    {
        try
        {
            return SignalReader.ReadDefaultBranches(repo);
        }
        catch (GitException error)
        {
            Trace.TraceWarning($"cannot find the default branches of {repo}: {error.Message}");
            return new DefaultBranches();
        }
    }

    private static string? CommonDirOrNone(string repo)
    {
        try
        {
            return GitRunner.CommonDir(repo);
        }
        catch (GitException error)
        {
            Trace.TraceWarning($"cannot find the git dir of {repo}: {error.Message}");
            return null;
        }
    }

    private sealed record RepoLookup(Repo Repo, DefaultBranches Defaults, string? CommonDir);

    private abstract record ScanJob;

    private sealed record RegisteredJob(RepoLookup Repo, WorktreeRecord Record) : ScanJob;

    private sealed record OrphanJob(Orphan Orphan) : ScanJob;
}
