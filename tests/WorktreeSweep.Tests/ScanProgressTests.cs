using System.Collections.Concurrent;
using WorktreeSweep.Report;
using WorktreeSweep.Scan;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>What <see cref="Scanner.Scan"/> reports while it runs, and how a cancelled scan ends.</summary>
public sealed class ScanProgressTests
{
    /// <summary>
    /// Discovery's events come before every repo look-up, every look-up before the last candidate is read, and the counts match
    /// the report.
    /// </summary>
    [Fact]
    public void ScanReportsDiscoveryThenLookupsThenCandidates()
    {
        using var fx = new Fixture();
        TwoWorktreesAndAnOrphan(fx);
        var events = new ConcurrentQueue<ScanProgress>();

        ScanReport report = Scanner.Scan(fx.Root, events.Enqueue, TestContext.Current.CancellationToken);

        List<ScanProgress> order = [.. events];
        int lastDiscovery = order.FindLastIndex(progress => progress is ScanProgress.Discovering or ScanProgress.Discovered);
        int firstLookup = order.FindIndex(progress => progress is ScanProgress.RepoLookedUp);
        int lastLookup = order.FindLastIndex(progress => progress is ScanProgress.RepoLookedUp);
        int lastRead = order.FindLastIndex(progress => progress is ScanProgress.CandidateRead);
        Assert.True(order.FindIndex(progress => progress is ScanProgress.Discovering) >= 0, "no Discovering event");
        Assert.True(lastDiscovery < firstLookup, $"a discovery event came after the first look-up: {string.Join(", ", order)}");
        Assert.True(lastLookup < lastRead, $"a look-up came after the last candidate read: {string.Join(", ", order)}");

        int registered = report.Candidates.OfType<RegisteredCandidate>().Count();
        int orphans = report.Candidates.OfType<OrphanCandidate>().Count();
        Assert.Equal(
            new ScanProgress.Discovered(report.Repos.Count, 1, registered, orphans, 0),
            Assert.Single(order.OfType<ScanProgress.Discovered>())
        );
        Assert.Equal((1, 2, 1), (report.Repos.Count, registered, orphans));
        Assert.Equal(new ScanProgress.RepoLookedUp(report.Repos.Count, report.Repos.Count), order[lastLookup]);
        Assert.Equal(new ScanProgress.CandidateRead(report.Candidates.Count, report.Candidates.Count), order[lastRead]);
    }

    /// <summary>Each registered worktree's read reports every step it reads, and the orphan's its size.</summary>
    [Fact]
    public void ScanReportsEachCandidatesSteps()
    {
        using var fx = new Fixture();
        TwoWorktreesAndAnOrphan(fx);
        var events = new ConcurrentQueue<ScanProgress>();

        ScanReport report = Scanner.Scan(fx.Root, events.Enqueue, TestContext.Current.CancellationToken);

        List<ScanProgress.CandidateStep> steps = [.. events.OfType<ScanProgress.CandidateStep>()];
        ScanStep[] all = [ScanStep.Merge, ScanStep.Status, ScanStep.Upstream, ScanStep.Activity, ScanStep.Size];
        foreach (RegisteredCandidate candidate in report.Candidates.OfType<RegisteredCandidate>())
        {
            HashSet<ScanStep> seen = [.. steps.Where(step => Fixture.SamePath(step.Path, candidate.Path)).Select(step => step.Step)];
            Assert.Equal([.. all], seen);
        }
        OrphanCandidate orphan = Assert.Single(report.Candidates.OfType<OrphanCandidate>());
        Assert.Contains(steps, step => Fixture.SamePath(step.Path, orphan.Path) && step.Step == ScanStep.Size);
    }

    /// <summary>A scan given a token that is already cancelled throws instead of returning a report.</summary>
    [Fact]
    public void ScanCancelledBeforeStartThrows()
    {
        using var fx = new Fixture();
        TwoWorktreesAndAnOrphan(fx);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        _ = Assert.ThrowsAny<OperationCanceledException>(() => Scanner.Scan(fx.Root, _ => { }, cancel.Token));
    }

    /// <summary>
    /// A scan cancelled while it reads a worktree throws instead of returning a partial report, and that worktree's read stops at
    /// the step it was in: no later step of it is started, so no git call of it runs after the cancel.
    /// </summary>
    [Fact]
    public void ScanCancelledMidScanThrowsAndLeavesNoGitRunning()
    {
        using var fx = new Fixture();
        TwoWorktreesAndAnOrphan(fx);
        using var cancel = new CancellationTokenSource();
        var events = new ConcurrentQueue<ScanProgress>();
        ScanProgress.CandidateStep? cancelledAt = null;
        var gate = new Lock();

        void CancelOnFirstMerge(ScanProgress progress)
        {
            events.Enqueue(progress);
            if (progress is ScanProgress.CandidateStep { Step: ScanStep.Merge } step)
            {
                lock (gate)
                {
                    cancelledAt ??= step;
                }
                cancel.Cancel();
            }
        }

        _ = Assert.ThrowsAny<OperationCanceledException>(() => Scanner.Scan(fx.Root, CancelOnFirstMerge, cancel.Token));

        Assert.NotNull(cancelledAt);
        List<ScanStep> stepsOfCancelled =
        [
            .. events.OfType<ScanProgress.CandidateStep>().Where(step => step.Path == cancelledAt.Path).Select(step => step.Step),
        ];
        Assert.Equal([ScanStep.Merge], stepsOfCancelled);
    }

    /// <summary>
    /// A scan cancelled while a worktree's git call hangs throws, and the hung git's process tree is gone. The repo's clean filter
    /// opens a file and then loops on one-second sleeps; an index stat that no longer matches makes <c>git status</c> run it on
    /// <c>README.md</c>. The test cancels once the file exists, and the file is released only when the filter's shell is killed.
    /// </summary>
    /// <returns>A task that completes when the test ends.</returns>
    [Fact]
    public async Task ScanCancelledWhileGitHangsKillsItsProcessTree()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string worktree = fx.PathTo("repo.wt/one");
        Fixture.AddWorktree(repo, worktree, "one");
        string held = fx.PathTo("held");
        _ = Fixture.Git(repo, ["config", "filter.hang.clean", $"exec 3> '{held.Replace('\\', '/')}'; while :; do sleep 1; done"]);
        File.WriteAllText(Path.Join(repo, ".git", "info", "attributes"), "README.md filter=hang\n");
        File.SetLastWriteTimeUtc(Path.Join(worktree, "README.md"), DateTime.UtcNow.AddHours(-1));
        using var cancel = new CancellationTokenSource();

        Task<ScanReport> scan = Task.Run(() => Scanner.Scan(fx.Root, _ => { }, cancel.Token), TestContext.Current.CancellationToken);
        Fixture.WaitUntil(() => File.Exists(held) || scan.IsCompleted, "the status read's clean filter to start");
        Assert.False(scan.IsCompleted, "the scan ended before its git call hung");
        await cancel.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Fixture.WaitUntil(() => Fixture.IsReleased(held), "the killed clean filter to release its output file");
    }

    /// <summary>Builds a repo with two linked worktrees and an orphan folder beside them in its container.</summary>
    private static void TwoWorktreesAndAnOrphan(Fixture fx)
    {
        string repo = fx.Repo("repo");
        Fixture.AddWorktree(repo, fx.PathTo("repo.wt/one"), "one");
        Fixture.AddWorktree(repo, fx.PathTo("repo.wt/two"), "two");
        Directory.CreateDirectory(fx.PathTo("repo.wt/stray"));
    }
}
