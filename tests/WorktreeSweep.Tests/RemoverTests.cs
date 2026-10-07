using WorktreeSweep.Discovery;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>Removing decided picks end to end, on real folders, junctions and git repos the tests create.</summary>
public sealed class RemoverTests
{
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Removing a junction orphan deletes the link and leaves its target's contents alone.</summary>
    [Fact]
    public void RemovingAJunctionOrphanKeepsTheTarget()
    {
        using var fx = new Fixture();
        string target = fx.PathTo("live-repo");
        Directory.CreateDirectory(target);
        string kept = Path.Combine(target, "file.txt");
        File.WriteAllText(kept, "keep");
        Directory.CreateDirectory(fx.PathTo("x.wt"));
        string link = fx.PathTo(@"x.wt\yaat");
        Assert.SkipUnless(Fixture.MakeJunction(link, target), "mklink /J is unavailable");

        Candidate candidate = CandidateAt(fx, link);
        Assert.True(candidate is OrphanCandidate { Orphan.Kind: OrphanKind.Link }, $"not a link orphan: {candidate}");
        var run = new Recorder();
        IReadOnlyList<Swept> swept = run.Remove(
            [new Decision(candidate, new Plan.Run(new RemoveAction.RemoveLink()), new BranchChoice.NotOffered())],
            TestContext.Current.CancellationToken
        );

        Assert.Equal(new Outcome.LinkRemoved(), swept[0].Outcome);
        Assert.False(Path.Exists(link), $"{link} still exists");
        Assert.True(File.Exists(kept), $"the link's target lost {kept}");
        Assert.Equal([new Progress.Started(0), new Progress.Done(0)], run.Progress);
    }

    /// <summary>Removing a registered worktree prunes its registration and deletes its branch when the choice is delete.</summary>
    [Fact]
    public void RemovingARegisteredWorktreePrunesAndDeletesTheBranch()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo(@"repo.wt\feat");
        Fixture.AddWorktree(repo, wt, "feat");
        RegisteredCandidate registered = Assert.IsType<RegisteredCandidate>(CandidateAt(fx, wt));
        BranchOffer offer = BranchOffers.For(registered) ?? throw new InvalidOperationException("no branch offer for a branch with no commits");
        Assert.False(offer.Force, $"a branch with no commits needs only -d: {offer}");

        IReadOnlyList<Swept> swept = new Recorder().Remove(
            [PermanentDecision(registered, new BranchChoice.Delete(offer))],
            TestContext.Current.CancellationToken
        );

        Assert.IsType<Outcome.Permanent>(swept[0].Outcome);
        Assert.False(Directory.Exists(wt), $"{wt} still exists");
        string list = Fixture.Git(repo, ["worktree", "list", "--porcelain"]);
        Assert.DoesNotContain("repo.wt/feat", list, StringComparison.Ordinal);
        Assert.Equal("", Fixture.Git(repo, ["branch", "--list", "feat"]));
        Assert.Equal(["branch feat deleted"], swept[0].Notes);
    }

    /// <summary>A declined branch is kept, and the note says so.</summary>
    [Fact]
    public void DeclinedBranchIsKeptWithANote()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo(@"repo.wt\feat");
        Fixture.AddWorktree(repo, wt, "feat");
        RegisteredCandidate registered = Assert.IsType<RegisteredCandidate>(CandidateAt(fx, wt));
        BranchOffer offer = BranchOffers.For(registered) ?? throw new InvalidOperationException("no branch offer for a branch with no commits");

        IReadOnlyList<Swept> swept = new Recorder().Remove(
            [PermanentDecision(registered, new BranchChoice.Keep(offer))],
            TestContext.Current.CancellationToken
        );

        Assert.IsType<Outcome.Permanent>(swept[0].Outcome);
        Assert.Equal(["branch feat kept"], swept[0].Notes);
        Assert.Contains("feat", Fixture.Git(repo, ["branch", "--list", "feat"]), StringComparison.Ordinal);
    }

    /// <summary>A skipped decision never touches the disk, reports no progress and makes no unlock offer.</summary>
    [Fact]
    public void SkippedDecisionNeverTouchesTheDisk()
    {
        using var fx = new Fixture();
        string folder = fx.PathTo(@"x.wt\stray");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "file.txt");
        File.WriteAllText(file, "keep");
        Candidate candidate = CandidateAt(fx, folder);

        var run = new Recorder();
        IReadOnlyList<Swept> swept = run.Remove(
            [new Decision(candidate, new Plan.Skip("not confirmed"), new BranchChoice.NotOffered())],
            TestContext.Current.CancellationToken
        );

        Assert.True(File.Exists(file), $"{folder} was touched");
        Swept only = Assert.Single(swept);
        Assert.Equal(new Outcome.Skipped("not confirmed"), only.Outcome);
        Assert.Equal(0, run.Offers);
        Assert.Empty(run.Progress);
    }

    /// <summary>
    /// A folder replaced by a junction after the scan is left in place rather than recycled through the link. The removal runs on
    /// another thread so that reaching the Shell fails the test on the timeout instead of hanging it.
    /// </summary>
    /// <returns>The test's task.</returns>
    [Fact]
    public async Task FolderThatBecameALinkIsLeftInPlace()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using var fx = new Fixture();
        string folder = fx.PathTo(@"x.wt\stray");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "file.txt"), "scanned");
        Candidate candidate = CandidateAt(fx, folder);
        string target = fx.PathTo("outside");
        Directory.CreateDirectory(target);
        string kept = Path.Combine(target, "keep.txt");
        File.WriteAllText(kept, "keep");
        Directory.Delete(folder, recursive: true);
        Assert.SkipUnless(Fixture.MakeJunction(folder, target), "mklink /J is unavailable");

        var run = new Recorder();
        Decision decision = new(candidate, new Plan.Run(new RemoveAction.Delete(DeleteMethod.Recycle)), new BranchChoice.NotOffered());
        Task<IReadOnlyList<Swept>> removing = Task.Run(() => run.Remove([decision], cancel), cancel);
        Task finished = await Task.WhenAny(removing, Task.Delay(DialogTimeout, cancel));

        Assert.True(finished == removing, $"RemovePicks did not return within {DialogTimeout}: the Shell may be showing a dialog");
        IReadOnlyList<Swept> swept = await removing;
        Outcome.Failed failed = Assert.IsType<Outcome.Failed>(swept[0].Outcome);
        Assert.Contains("became a link since the scan; left in place", failed.Reason, StringComparison.Ordinal);
        Assert.True(new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint), $"{folder} is no longer the junction");
        Assert.True(File.Exists(kept), $"the junction's target lost {kept}");
    }

    /// <summary>A token cancelled before the first item leaves every pick in place as skipped, with no progress and no offer.</summary>
    [Fact]
    public void CancelledBeforeTheFirstItemSkipsEveryPick()
    {
        using var fx = new Fixture();
        string first = fx.PathTo(@"x.wt\first");
        string second = fx.PathTo(@"x.wt\second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Decision[] decisions =
        [
            PermanentDecision(CandidateAt(fx, first), new BranchChoice.NotOffered()),
            PermanentDecision(CandidateAt(fx, second), new BranchChoice.NotOffered()),
        ];
        using var source = new CancellationTokenSource();
        source.Cancel();

        var run = new Recorder();
        IReadOnlyList<Swept> swept = run.Remove(decisions, source.Token);

        Assert.All(swept, entry => Assert.Equal(new Outcome.Skipped("cancelled"), entry.Outcome));
        Assert.Equal(2, swept.Count);
        Assert.True(Directory.Exists(first) && Directory.Exists(second), "a cancelled pick was removed");
        Assert.Equal(0, run.Offers);
        Assert.Empty(run.Progress);
    }

    /// <summary>
    /// A git-locked worktree with a file held open fails pass 1 as locked; an offer that releases the file and answers unlocked lets
    /// the retry remove it. The git lock is lifted once only: a second <c>git worktree unlock</c> would fail the retry.
    /// </summary>
    [Fact]
    public void LockedPickIsRemovedOnRetryAfterAnOfferThatUnlocks()
    {
        using var fx = new Fixture();
        (string repo, string wt, string held) = GitLockedWorktree(fx);
        RegisteredCandidate registered = Assert.IsType<RegisteredCandidate>(CandidateAt(fx, wt));
        var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
        var run = new Recorder
        {
            Answer = () =>
            {
                handle.Dispose();
                return UnlockOutcome.Unlocked;
            },
        };

        IReadOnlyList<Swept> swept;
        using (handle)
        {
            swept = run.Remove([PermanentDecision(registered, new BranchChoice.NotOffered())], TestContext.Current.CancellationToken);
        }

        Assert.IsType<Outcome.Permanent>(swept[0].Outcome);
        Assert.False(Directory.Exists(wt), $"{wt} still exists");
        Assert.Equal(1, run.Offers);
        Assert.True(Fixture.SamePath(Assert.Single(run.OfferedPaths), wt), $"offered: {string.Join(", ", run.OfferedPaths)}");
        Assert.Equal([new Progress.Started(0), new Progress.Done(0), new Progress.Started(0), new Progress.Done(0)], run.Progress);
        Assert.DoesNotContain("repo.wt/feat", Fixture.Git(repo, ["worktree", "list", "--porcelain"]), StringComparison.Ordinal);
    }

    /// <summary>
    /// A git-locked worktree still locked after a declined offer fails as <c>locked (...)</c>, reports its progress done, and gets its
    /// git lock back with its reason.
    /// </summary>
    [Fact]
    public void DeclinedOfferFailsTheLockedPickAndRestoresItsGitLock()
    {
        using var fx = new Fixture();
        (string repo, string wt, string held) = GitLockedWorktree(fx);
        RegisteredCandidate registered = Assert.IsType<RegisteredCandidate>(CandidateAt(fx, wt));
        Assert.Equal("keep me", registered.Record.Locked);
        var run = new Recorder();

        IReadOnlyList<Swept> swept;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            swept = run.Remove([PermanentDecision(registered, new BranchChoice.NotOffered())], TestContext.Current.CancellationToken);
        }

        Outcome.Failed failed = Assert.IsType<Outcome.Failed>(swept[0].Outcome);
        Assert.StartsWith("locked (", failed.Reason, StringComparison.Ordinal);
        Assert.Contains("held.txt", failed.Reason, StringComparison.Ordinal);
        Assert.Empty(swept[0].Notes);
        Assert.Equal(1, run.Offers);
        Assert.Equal([new Progress.Started(0), new Progress.Done(0)], run.Progress);
        Assert.Contains("locked keep me", Fixture.Git(repo, ["worktree", "list", "--porcelain"]), StringComparison.Ordinal);
    }

    /// <summary>A folder replaced by a junction after the scan is not deleted for good either: the junction and its target stay.</summary>
    [Fact]
    public void PermanentDeleteOfAFolderThatBecameALinkLeavesItInPlace()
    {
        using var fx = new Fixture();
        string folder = fx.PathTo(@"x.wt\stray");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "file.txt"), "scanned");
        Candidate candidate = CandidateAt(fx, folder);
        string target = fx.PathTo("outside");
        Directory.CreateDirectory(target);
        string kept = Path.Combine(target, "keep.txt");
        File.WriteAllText(kept, "keep");
        Directory.Delete(folder, recursive: true);
        Assert.SkipUnless(Fixture.MakeJunction(folder, target), "mklink /J is unavailable");

        var run = new Recorder();
        IReadOnlyList<Swept> swept = run.Remove([PermanentDecision(candidate, new BranchChoice.NotOffered())], TestContext.Current.CancellationToken);

        Outcome.Failed failed = Assert.IsType<Outcome.Failed>(swept[0].Outcome);
        Assert.Contains("became a link since the scan; left in place", failed.Reason, StringComparison.Ordinal);
        Assert.True(new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint), $"{folder} is no longer the junction");
        Assert.True(File.Exists(kept), $"the junction's target lost {kept}");
        Assert.Equal([new Progress.Started(0), new Progress.Done(0)], run.Progress);
    }

    /// <summary>A worktree on <c>feat</c> at <c>repo.wt\feat</c>, git-locked with the reason <c>keep me</c>, holding <c>held.txt</c>.</summary>
    /// <param name="fx">The fixture.</param>
    /// <returns>The repo, the worktree and the file to hold open.</returns>
    private static (string Repo, string Wt, string Held) GitLockedWorktree(Fixture fx)
    {
        string repo = fx.Repo("repo");
        string wt = fx.PathTo(@"repo.wt\feat");
        Fixture.AddWorktree(repo, wt, "feat");
        string held = Path.Combine(wt, "held.txt");
        File.WriteAllText(held, "held");
        _ = Fixture.Git(repo, ["worktree", "lock", "--reason", "keep me", wt]);
        return (repo, wt, held);
    }

    /// <summary>The scanned candidate at <paramref name="path"/>.</summary>
    /// <param name="fx">The fixture to scan.</param>
    /// <param name="path">The candidate's path.</param>
    /// <returns>The candidate.</returns>
    private static Candidate CandidateAt(Fixture fx, string path) =>
        Assert.Single(fx.Scan().Candidates, candidate => Fixture.SamePath(candidate.Path, path));

    /// <summary>A decision to delete <paramref name="candidate"/> for good.</summary>
    /// <param name="candidate">The pick.</param>
    /// <param name="branch">What to do with its branch.</param>
    /// <returns>The decision.</returns>
    private static Decision PermanentDecision(Candidate candidate, BranchChoice branch) =>
        new(candidate, new Plan.Run(new RemoveAction.Delete(DeleteMethod.Permanent)), branch);

    /// <summary>Runs <see cref="Remover.RemovePicks"/> and records its progress and unlock offers.</summary>
    private sealed class Recorder
    {
        /// <summary>Gets the progress reported, in order.</summary>
        public List<Progress> Progress { get; } = [];

        /// <summary>Gets how many times the unlock offer was made.</summary>
        public int Offers { get; private set; }

        /// <summary>Gets the paths of the last unlock offer; empty when none was made.</summary>
        public IReadOnlyList<string> OfferedPaths { get; private set; } = [];

        /// <summary>Gets what the unlock offer does and answers; by default it declines.</summary>
        public Func<UnlockOutcome> Answer { get; init; } = () => UnlockOutcome.Skipped;

        /// <summary>Removes <paramref name="decisions"/>.</summary>
        /// <param name="decisions">The decided picks.</param>
        /// <param name="cancel">The token that stops the removal.</param>
        /// <returns>What happened to each.</returns>
        public IReadOnlyList<Swept> Remove(IReadOnlyList<Decision> decisions, CancellationToken cancel) =>
            Remover.RemovePicks(
                decisions,
                Progress.Add,
                paths =>
                {
                    Offers++;
                    OfferedPaths = paths;
                    return Answer();
                },
                cancel
            );
    }
}
