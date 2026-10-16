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
    /// scanned, with a trace warning: its candidates get no default branches to compare against, or no released marker; a git
    /// timeout behind any of those reads marks the repo's volume stalled. One <see cref="VolumeStalls"/> is made for the whole scan
    /// and shared by discovery and every read, so a volume one git call times out on is not waited on again by any other repo or
    /// worktree on it.
    /// </remarks>
    /// <param name="root">The folder to scan.</param>
    /// <param name="onProgress">
    /// Told how far the scan has got: <see cref="ScanProgress.Discovering"/> per child of the root, then one
    /// <see cref="ScanProgress.Discovered"/>, then <see cref="ScanProgress.RepoLookedUp"/> per repo, then, per candidate, its
    /// <see cref="ScanProgress.CandidateStep"/>s and one <see cref="ScanProgress.CandidateRead"/>. It runs on worker threads,
    /// concurrently with itself, and may still be called briefly after <paramref name="token"/> is cancelled, so a caller that
    /// starts a new scan drops a late call by its own count of scans. An exception it throws fails the scan.
    /// </param>
    /// <param name="token">Cancels the scan: no new folder is walked or git call started, and a running git's process tree is
    /// killed.</param>
    /// <returns>The report, its candidates registered worktrees first, then orphans, each in discovery order.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> is cancelled; no partial report is returned.</exception>
    /// <exception cref="IOException"><paramref name="root"/> itself cannot be listed; the message names it.</exception>
    /// <exception cref="ArgumentException"><paramref name="root"/> is empty or not a valid path.</exception>
    public static ScanReport Scan(string root, Action<ScanProgress> onProgress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(onProgress);
        token.ThrowIfCancellationRequested();
        var stalls = new VolumeStalls();
        DiscoveryResult discovery = Discoverer.Discover(
            root,
            stalls,
            (child, children) => onProgress(new ScanProgress.Discovering(child, children)),
            token
        );
        onProgress(DiscoveredOf(discovery));
        IReadOnlyList<RepoLookup> repos = LookUpAll(discovery.Repos, stalls, onProgress, token);

        var jobs = new List<ScanJob>();
        foreach ((Repo repo, RepoLookup lookup) in discovery.Repos.Zip(repos))
        {
            jobs.AddRange(repo.Worktrees.Skip(1).Select(record => new RegisteredJob(lookup, record)));
        }
        jobs.AddRange(discovery.Orphans.Select(orphan => new OrphanJob(orphan)));

        return new ScanReport
        {
            Root = discovery.Root,
            Repos = [.. repos.Select(repo => new RepoReport { Path = repo.Path, DefaultBranches = repo.Defaults })],
            DiscoveryErrors = discovery.Errors,
            Candidates = ReadAll(jobs, stalls, onProgress, token),
        };
    }

    /// <summary>What discovery found, counted for <see cref="ScanProgress.Discovered"/>.</summary>
    private static ScanProgress.Discovered DiscoveredOf(DiscoveryResult discovery)
    {
        int worktrees = discovery.Repos.Sum(repo => repo.Worktrees.Skip(1).Count());
        int links = discovery.Orphans.Count(orphan => orphan.Kind == OrphanKind.Link);
        return new ScanProgress.Discovered(discovery.Repos.Count, discovery.Containers.Count, worktrees, discovery.Orphans.Count - links, links);
    }

    /// <summary>Looks up every repo in parallel, reporting each one done under one lock, so the counts arrive in order.</summary>
    private static IReadOnlyList<RepoLookup> LookUpAll(
        IReadOnlyList<Repo> repos,
        VolumeStalls stalls,
        Action<ScanProgress> onProgress,
        CancellationToken token
    )
    {
        int done = 0;
        var counter = new Lock();
        return SignalReader.ParallelMap(
            repos,
            repo =>
            {
                RepoLookup lookup = LookUp(repo, stalls, token);
                lock (counter)
                {
                    done++;
                    onProgress(new ScanProgress.RepoLookedUp(done, repos.Count));
                }
                return lookup;
            },
            token
        );
    }

    /// <summary>Reads every candidate in parallel, reporting each one done under one lock, so the counts arrive in order.</summary>
    private static IReadOnlyList<Candidate> ReadAll(
        IReadOnlyList<ScanJob> jobs,
        VolumeStalls stalls,
        Action<ScanProgress> onProgress,
        CancellationToken token
    )
    {
        int done = 0;
        var counter = new Lock();
        return SignalReader.ParallelMap(
            jobs,
            job =>
            {
                Candidate candidate = ReadCandidate(job, stalls, onProgress, token);
                lock (counter)
                {
                    done++;
                    onProgress(new ScanProgress.CandidateRead(done, jobs.Count));
                }
                return candidate;
            },
            token
        );
    }

    /// <summary>A repo's default branches and, when it has a linked worktree, its common git dir, where released markers are found.</summary>
    /// <param name="repo">The repo.</param>
    /// <param name="stalls">The volumes an earlier git call has stalled.</param>
    /// <param name="token">Cancels the look-ups.</param>
    /// <returns>What the repo's look-ups found.</returns>
    private static RepoLookup LookUp(Repo repo, VolumeStalls stalls, CancellationToken token) =>
        new(repo.Path, DefaultBranchesOrNone(repo.Path, stalls, token), repo.Worktrees.Count > 1 ? CommonDirOrNone(repo.Path, stalls, token) : null);

    private static Candidate ReadCandidate(ScanJob job, VolumeStalls stalls, Action<ScanProgress> onProgress, CancellationToken token) =>
        job switch
        {
            RegisteredJob registered => ReadRegistered(
                registered.Lookup,
                registered.Record,
                stalls,
                (step, files) => onProgress(new ScanProgress.CandidateStep(registered.Record.Path, step, files)),
                token
            ),
            OrphanJob orphan => ReadOrphan(orphan.Orphan, onProgress, token),
            _ => throw new UnreachableException($"unknown scan job {job}"),
        };

    /// <summary>The candidate for an orphan: its size, reported as one <see cref="ScanStep.Size"/> step.</summary>
    private static OrphanCandidate ReadOrphan(Orphan orphan, Action<ScanProgress> onProgress, CancellationToken token)
    {
        onProgress(new ScanProgress.CandidateStep(orphan.Path, ScanStep.Size, 0));
        SizeInfo size = SignalReader.WalkSize(
            orphan.Path,
            files => onProgress(new ScanProgress.CandidateStep(orphan.Path, ScanStep.Size, files)),
            token
        );
        return new OrphanCandidate { Orphan = orphan, Size = size };
    }

    /// <summary>
    /// The candidate for a registered linked worktree: its record, its released marker and its signals, read against the repo's
    /// default branches. A prunable registration's signals are all unknown, as its folder is gone.
    /// </summary>
    /// <param name="repo">The repo that registers it, with what its look-ups found.</param>
    /// <param name="record">The worktree's record.</param>
    /// <param name="stalls">The volumes an earlier git call has stalled.</param>
    /// <param name="onStep">Told each step of the read, as <see cref="SignalReader.ReadWorktreeSignals"/> reports it.</param>
    /// <param name="token">Cancels the read, killing a running git.</param>
    /// <returns>The candidate.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> is cancelled.</exception>
    internal static RegisteredCandidate ReadRegistered(
        RepoLookup repo,
        WorktreeRecord record,
        VolumeStalls stalls,
        Action<ScanStep, long> onStep,
        CancellationToken token
    ) =>
        new()
        {
            Record = record,
            Repo = repo.Path,
            Released = ReleasedMarker.Find(repo.CommonDir, record.Path),
            Signals = SignalReader.ReadWorktreeSignals(repo.Defaults, record, stalls, onStep, token),
        };

    /// <summary>A repo's default branches, or none when git cannot give them, a stalled volume included.</summary>
    /// <param name="repo">The repo.</param>
    /// <param name="stalls">The volumes an earlier git call has stalled.</param>
    /// <param name="token">Cancels the look-up, killing a running git; a cancellation is never traced as a failure.</param>
    /// <returns>The defaults, or none, with the failure traced as a warning.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> is cancelled.</exception>
    internal static DefaultBranches DefaultBranchesOrNone(string repo, VolumeStalls stalls, CancellationToken token)
    {
        try
        {
            return SignalReader.ReadDefaultBranches(repo, stalls, token);
        }
        catch (GitException error)
        {
            if (error is GitTimeoutException)
            {
                stalls.Mark(repo);
            }
            Trace.TraceWarning($"cannot find the default branches of {repo}: {error.Message}");
            return new DefaultBranches();
        }
    }

    /// <summary>A repo's common git dir, or <see langword="null"/> when git cannot give it, a stalled volume included.</summary>
    /// <param name="repo">The repo.</param>
    /// <param name="stalls">The volumes an earlier git call has stalled.</param>
    /// <param name="token">Cancels the look-up, killing a running git; a cancellation is never traced as a failure.</param>
    /// <returns>The common git dir, or <see langword="null"/>, with the failure traced as a warning.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> is cancelled.</exception>
    internal static string? CommonDirOrNone(string repo, VolumeStalls stalls, CancellationToken token)
    {
        try
        {
            if (CommonDirOrNull(repo, stalls, token) is { } common)
            {
                return common;
            }
            Trace.TraceWarning($"cannot find the git dir of {repo}: git finds no repo there");
            return null;
        }
        catch (GitException error)
        {
            if (error is GitTimeoutException)
            {
                stalls.Mark(repo);
            }
            Trace.TraceWarning($"cannot find the git dir of {repo}: {error.Message}");
            return null;
        }
    }

    /// <summary>The common git dir of the repo <paramref name="dir"/> lies in, unless its volume is stalled.</summary>
    /// <param name="dir">A folder inside the repo or one of its worktrees.</param>
    /// <param name="stalls">The volumes an earlier git call has stalled.</param>
    /// <param name="token">Cancels the call, killing git.</param>
    /// <returns>The common git dir; <see langword="null"/> when git finds no repo there.</returns>
    /// <exception cref="GitTimeoutException">The volume is stalled, or git does not exit within its time limit.</exception>
    /// <exception cref="GitException">Git cannot be started, or prints a path that is not fully qualified.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> is cancelled.</exception>
    internal static string? CommonDirOrNull(string dir, VolumeStalls stalls, CancellationToken token)
    {
        stalls.ThrowIfStalled(dir);
        GitStatus status = GitRunner.RunStatus(dir, GitRunner.CommonDirArgs, token);
        return status.Success ? GitRunner.FullCommonDir(dir, status.Stdout) : null;
    }

    /// <summary>What a repo's look-ups found.</summary>
    /// <param name="Path">The repo's main worktree.</param>
    /// <param name="Defaults">Its default branches, from <see cref="DefaultBranchesOrNone"/>.</param>
    /// <param name="CommonDir">Its common git dir, where a prunable worktree's released marker is found; <see langword="null"/> when
    /// unknown.</param>
    internal sealed record RepoLookup(string Path, DefaultBranches Defaults, string? CommonDir);

    private abstract record ScanJob;

    private sealed record RegisteredJob(RepoLookup Lookup, WorktreeRecord Record) : ScanJob;

    private sealed record OrphanJob(Orphan Orphan) : ScanJob;
}
