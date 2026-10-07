using WorktreeSweep.Discovery;
using WorktreeSweep.Recycle;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Review;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>The questions asked about the picks before removal, the decisions they make, and the final sentence.</summary>
public sealed class ReviewSessionTests
{
    /// <summary>A mebibyte (1024 × 1024 bytes).</summary>
    private const long Mb = 1024 * 1024;

    /// <summary>The git dir a guarded orphan's <c>.git</c> file names, in the repo <c>D:\yaat</c>.</summary>
    private const string StrayGitdir = @"D:\yaat\.git\worktrees\stray";

    /// <summary>A bin holding 1 GB.</summary>
    private static readonly BinCapacity Roomy = new(1024, NukeOnDelete: false);

    /// <summary>A bin holding 1 MB.</summary>
    private static readonly BinCapacity Small = new(1, NukeOnDelete: false);

    /// <summary>A dirty, unmerged worktree asks about its loss first, defaulting to no; a yes recycles it.</summary>
    [Fact]
    public void ReviewDirtyUnmergedAsksLossFirstDefaultNo()
    {
        RegisteredCandidate dirty = Worktree(MergeState.Unmerged(4), bytes: 10);
        dirty = dirty with
        {
            Signals = dirty.Signals with
            {
                Dirty = new Dirty { Modified = 3, Untracked = 2 },
            },
        };
        ReviewSession review = Session([dirty], _ => Roomy);

        Assert.Equal(
            new ReviewStep(
                0,
                StepKind.Loss,
                "3 modified, 2 untracked files and 4 commits not on main will be lost.",
                "Remove anyway?",
                DefaultAnswer: false
            ),
            review.Current
        );
        review.Answer(true);
        Assert.Null(review.Current);
        Decision decision = OnlyDecision(review);
        Assert.Equal(new Plan.Run(new RemoveAction.Delete(DeleteMethod.Recycle)), decision.Plan);
        Assert.IsType<BranchChoice.NotOffered>(decision.Branch);
    }

    /// <summary>Declining the loss skips the pick's later questions and leaves it in place.</summary>
    [Fact]
    public void ReviewDeclinedLossSkipsPermanentAndBranch()
    {
        RegisteredCandidate unpushed = Worktree(MergeState.Ancestor, bytes: 10);
        unpushed = unpushed with { Signals = unpushed.Signals with { Upstream = Upstream.Tracking(1) } };
        ReviewSession review = Session([unpushed], _ => null);

        Assert.Equal(StepKind.Loss, review.Current?.Kind);
        review.Answer(false);
        Assert.Null(review.Current);
        Decision decision = OnlyDecision(review);
        Assert.Equal(new Plan.Skip("not confirmed"), decision.Plan);
        Assert.IsType<BranchChoice.NotOffered>(decision.Branch);
    }

    /// <summary>A pick bigger than the bin asks for a permanent delete, defaulting to no; a no skips it and its branch.</summary>
    [Fact]
    public void ReviewOverCapacityAsksPermanentDefaultNo()
    {
        ReviewSession review = Session([Worktree(MergeState.Ancestor, bytes: 2 * Mb)], _ => Small);

        ReviewStep? step = review.Current;
        Assert.NotNull(step);
        Assert.Equal(StepKind.Permanent, step.Kind);
        Assert.False(step.DefaultAnswer);
        Assert.Equal("Delete it permanently?", step.Question);
        Assert.StartsWith(@"repo.wt\feat cannot go to the Recycle Bin: ", step.Body, StringComparison.Ordinal);
        Assert.EndsWith("; deleting it permanently cannot be undone.", step.Body, StringComparison.Ordinal);
        review.Answer(false);
        Assert.Null(review.Current);
        Decision decision = OnlyDecision(review);
        Plan.Skip skip = Assert.IsType<Plan.Skip>(decision.Plan);
        Assert.StartsWith("permanent delete declined; ", skip.Reason, StringComparison.Ordinal);
        Assert.IsType<BranchChoice.NotOffered>(decision.Branch);
    }

    /// <summary>A squash-merged branch is offered for a forced delete, defaulting to yes.</summary>
    [Fact]
    public void ReviewSquashedBranchOffersForceDeleteDefaultYes()
    {
        ReviewSession review = Session([Worktree(MergeState.ContentContained, bytes: 10)], _ => Roomy);

        ReviewStep? step = review.Current;
        Assert.NotNull(step);
        Assert.Equal(StepKind.Branch, step.Kind);
        Assert.True(step.DefaultAnswer);
        Assert.Equal("Delete the branch?", step.Question);
        Assert.StartsWith("Branch feat: ", step.Body, StringComparison.Ordinal);
        Assert.Contains("git branch -D", step.Body, StringComparison.Ordinal);
        review.Answer(true);
        Decision decision = OnlyDecision(review);
        BranchChoice.Delete delete = Assert.IsType<BranchChoice.Delete>(decision.Branch);
        Assert.True(delete.Offer.Force);
        Assert.Equal("feat", delete.Offer.Branch);
        Assert.Equal(new Plan.Run(new RemoveAction.Delete(DeleteMethod.Recycle)), decision.Plan);
    }

    /// <summary>Declining the branch question keeps the branch.</summary>
    [Fact]
    public void ReviewDeclinedBranchKeepsIt()
    {
        ReviewSession review = Session([Worktree(MergeState.Ancestor, bytes: 10)], _ => Roomy);

        Assert.Equal(StepKind.Branch, review.Current?.Kind);
        review.Answer(false);
        BranchChoice.Keep keep = Assert.IsType<BranchChoice.Keep>(OnlyDecision(review).Branch);
        Assert.Equal("feat", keep.Offer.Branch);
        Assert.False(keep.Offer.Force);
    }

    /// <summary>A link asks the link question, defaulting to no; a yes removes only the link.</summary>
    [Fact]
    public void ReviewLinkAsksLinkQuestion()
    {
        ReviewSession review = Session([Orphan(OrphanKind.Link)], _ => Roomy);

        ReviewStep? step = review.Current;
        Assert.NotNull(step);
        Assert.Equal(StepKind.Link, step.Kind);
        Assert.False(step.DefaultAnswer);
        Assert.Equal("Remove the link?", step.Question);
        Assert.StartsWith("Remove the link only; ", step.Body, StringComparison.Ordinal);
        review.Answer(true);
        Assert.Equal(new Plan.Run(new RemoveAction.RemoveLink()), OnlyDecision(review).Plan);
    }

    /// <summary>The bin's capacity is read for each folder, and never for a link or a prunable registration.</summary>
    [Fact]
    public void ReviewReadsCapacityOnlyForFolders()
    {
        OrphanCandidate link = Orphan(OrphanKind.Link);
        OrphanCandidate folder = Orphan(OrphanKind.Folder);
        RegisteredCandidate prunable = Worktree(MergeState.Ancestor, bytes: 10);
        prunable = prunable with { Record = prunable.Record with { Prunable = "gitdir file points to non-existent location" } };
        RegisteredCandidate live = Worktree(MergeState.Ancestor, bytes: 10);
        List<string> calls = [];

        _ = Session(
            [link, folder, prunable, live],
            path =>
            {
                calls.Add(path);
                return Roomy;
            }
        );

        Assert.Equal([folder.Path, live.Path], calls);
    }

    /// <summary>Totals and decisions are withheld until every question is answered.</summary>
    [Fact]
    public void ReviewDecisionsNoneUntilFinished()
    {
        RegisteredCandidate candidate = Worktree(MergeState.Ancestor, bytes: 10);
        ReviewSession unfinished = Session([candidate], _ => Roomy);
        Assert.NotNull(unfinished.Current);
        Assert.Null(unfinished.Totals());
        Assert.Null(unfinished.Decisions());

        ReviewSession finished = Session([candidate], _ => Roomy);
        finished.Answer(true);
        Assert.Equal(
            new ReviewTotals
            {
                Recycle = 1,
                RecycleBytes = 10,
                Branches = 1,
            },
            finished.Totals()
        );
        Assert.NotNull(finished.Decisions());
    }

    /// <summary>The final sentence names only the non-zero parts.</summary>
    [Fact]
    public void FinalSentenceOmitsZeroParts()
    {
        ReviewTotals totals = new()
        {
            Recycle = 2,
            RecycleBytes = 3 * Mb,
            Links = 3,
        };
        Assert.Equal($"Remove 5 items: 2 to the Recycle Bin ({ReportTable.HumanBytes(3 * Mb)}), 3 links.", totals.FinalSentence());

        ReviewTotals all = new()
        {
            Recycle = 2,
            RecycleBytes = 3 * Mb,
            Permanent = 2,
            PermanentBytes = 5 * Mb,
            Links = 2,
            Prunes = 2,
            Branches = 3,
            Skipped = 4,
        };
        Assert.Equal(
            $"Remove 8 items: 2 to the Recycle Bin ({ReportTable.HumanBytes(3 * Mb)}), 2 permanently ({ReportTable.HumanBytes(5 * Mb)}), "
                + "2 links, 2 registrations pruned; 3 branches deleted. 4 skipped.",
            all.FinalSentence()
        );
    }

    /// <summary>A count of one takes the singular noun.</summary>
    [Fact]
    public void FinalSentenceSingularForms()
    {
        ReviewTotals totals = new()
        {
            Prunes = 1,
            Branches = 1,
            Skipped = 1,
        };
        Assert.Equal("Remove 1 item: 1 registration pruned; 1 branch deleted. 1 skipped.", totals.FinalSentence());
        Assert.Equal("Remove 1 item: 1 link.", new ReviewTotals { Links = 1 }.FinalSentence());
    }

    /// <summary>A sized part with one unknown size reads "at least" and counts it; a known size keeps the plain wording.</summary>
    [Fact]
    public void FinalSentenceNamesUnknownSizes()
    {
        ReviewTotals recycled = new()
        {
            Recycle = 2,
            RecycleBytes = Mb,
            RecycleUnknown = 1,
        };
        Assert.Equal($"Remove 2 items: 2 to the Recycle Bin (at least {ReportTable.HumanBytes(Mb)}; 1 of unknown size).", recycled.FinalSentence());

        ReviewTotals permanent = new() { Permanent = 1, PermanentUnknown = 1 };
        Assert.Equal($"Remove 1 item: 1 permanently (at least {ReportTable.HumanBytes(0)}; 1 of unknown size).", permanent.FinalSentence());
    }

    /// <summary>A pick whose size the scan could not read counts in its part's unknown total and reads "at least" in the sentence.</summary>
    [Fact]
    public void ReviewUnknownSizeIsCounted()
    {
        ReviewSession review = Session([Worktree(MergeState.Ancestor, bytes: null)], _ => Roomy);

        Assert.Equal(StepKind.Permanent, review.Current?.Kind);
        review.Answer(true);
        Assert.Equal(StepKind.Branch, review.Current?.Kind);
        review.Answer(false);
        ReviewTotals totals = review.Totals() ?? throw new InvalidOperationException("every question is answered");
        Assert.Equal(new ReviewTotals { Permanent = 1, PermanentUnknown = 1 }, totals);
        Assert.Equal("Remove 1 item: 1 permanently (at least 0 B; 1 of unknown size).", totals.FinalSentence());
    }

    /// <summary>A failed worktree list in the orphan's repo plans it as skipped and asks nothing about it.</summary>
    [Fact]
    public void DiscoveryErrorListFailedSkipsOrphan() => AssertGuarded(new DiscoveryError(@"D:\yaat", @"D:\yaat", "git worktree list failed: boom"));

    /// <summary>A record git's list left out for this very worktree plans the orphan as skipped.</summary>
    [Fact]
    public void DiscoveryErrorLeftOutGitdirSkipsOrphan() =>
        AssertGuarded(new DiscoveryError(@"D:\yaat", StrayGitdir + @"\gitdir", "git's worktree list leaves out this one"));

    /// <summary>An unreadable <c>.git\worktrees</c> folder in the orphan's repo plans it as skipped.</summary>
    [Fact]
    public void DiscoveryErrorUnreadableWorktreesSkipsOrphan() =>
        AssertGuarded(new DiscoveryError(@"D:\yaat", @"D:\yaat\.git\worktrees", "cannot read it"));

    /// <summary>A left-out record of another worktree in the same repo guards nothing: the orphan is asked about and removable.</summary>
    [Fact]
    public void DiscoveryErrorForAnotherWorktreeLeavesOrphanPlannable()
    {
        DiscoveryError error = new(@"D:\yaat", @"D:\yaat\.git\worktrees\other\gitdir", "git's worktree list leaves out another");
        ReviewSession review = Session([Orphan(OrphanKind.Folder, StrayGitdir)], _ => Roomy, error);

        ReviewStep? step = review.Current;
        Assert.NotNull(step);
        Assert.Equal(StepKind.Loss, step.Kind);
        Assert.Equal(@"Still registered in D:\yaat; removing leaves a prunable registration there.", step.Body);
        review.Answer(true);
        Assert.Equal(new Plan.Run(new RemoveAction.Delete(DeleteMethod.Recycle)), OnlyDecision(review).Plan);
    }

    /// <summary>An orphan with no live git dir is unaffected by any discovery error.</summary>
    [Fact]
    public void DiscoveryErrorIgnoresOrphanWithoutLiveGitdir()
    {
        ReviewSession review = Session(
            [Orphan(OrphanKind.Folder)],
            _ => Roomy,
            new DiscoveryError(@"D:\yaat", @"D:\yaat", "git worktree list failed: boom"),
            new DiscoveryError(@"D:\yaat", @"D:\yaat\.git\worktrees", "cannot read it"),
            new DiscoveryError(@"D:\yaat", StrayGitdir + @"\gitdir", "left out")
        );

        Assert.Null(review.Current);
        Assert.Equal(new Plan.Run(new RemoveAction.Delete(DeleteMethod.Recycle)), OnlyDecision(review).Plan);
    }

    /// <summary>Paths compare by <see cref="Discoverer.PathKey"/>: case and separators do not matter.</summary>
    /// <param name="repo">The error's repo.</param>
    /// <param name="path">The error's path.</param>
    [Theory]
    [InlineData(@"d:\YAAT", @"D:/Yaat/")]
    [InlineData(@"D:\yaat", @"d:\YAAT\.GIT\Worktrees\STRAY\GitDir")]
    [InlineData(@"D:\yaat", @"D:/yaat/.git/WORKTREES")]
    public void DiscoveryErrorPathsCompareCaseInsensitively(string repo, string path) => AssertGuarded(new DiscoveryError(repo, path, "failed"));

    /// <summary>
    /// A git dir written relative to its worktree (<c>worktree.useRelativePaths</c>), so with <c>..</c> segments, is guarded by each
    /// clause of the rule.
    /// </summary>
    /// <param name="path">The error's path, in the repo <c>D:\yaat</c>.</param>
    [Theory]
    [InlineData(@"D:\yaat")]
    [InlineData(StrayGitdir + @"\gitdir")]
    [InlineData(@"D:\yaat\.git\worktrees")]
    public void DiscoveryErrorGuardsRelativeGitdir(string path) =>
        AssertGuarded(@"D:\yaat.wt\stray\..\..\yaat\.git\worktrees\stray", @"D:\yaat", new DiscoveryError(@"D:\yaat", path, "failed"));

    /// <summary>
    /// An error named through a junction to the scanned folders guards the orphan whose git dir names the real path: discovery walks
    /// the junction's spelling, git prints the resolved one.
    /// </summary>
    [Fact]
    public void DiscoveryErrorThroughJunctionGuardsOrphan()
    {
        using Fixture fx = new();
        string real = fx.PathTo("real");
        string gitdir = Path.Join(real, "yaat", ".git", "worktrees", "stray");
        Directory.CreateDirectory(gitdir);
        string linked = fx.PathTo("linked");
        Assert.SkipUnless(Fixture.MakeJunction(linked, real), "mklink /J is unavailable");
        string walkedRepo = Path.Join(linked, "yaat");

        AssertGuarded(gitdir, Path.Join(real, "yaat"), new DiscoveryError(walkedRepo, walkedRepo, "git worktree list failed: boom"));
    }

    /// <summary>
    /// A bare-shaped git dir (<c>{common}\worktrees\{id}</c>) is guarded by an error on the <c>worktrees</c> folder holding it.
    /// </summary>
    [Fact]
    public void DiscoveryErrorUnreadableWorktreesGuardsBareShapedGitdir() =>
        AssertGuarded(@"D:\bare.git\worktrees\stray", @"D:\bare.git", new DiscoveryError(@"D:\bare.git", @"D:\bare.git\worktrees", "cannot read it"));

    /// <summary>Asserts that <paramref name="error"/> guards the orphan at <see cref="StrayGitdir"/> in <c>D:\yaat</c>.</summary>
    /// <param name="error">The discovery error.</param>
    private static void AssertGuarded(DiscoveryError error) => AssertGuarded(StrayGitdir, @"D:\yaat", error);

    /// <summary>
    /// Asserts that <paramref name="error"/> makes the orphan whose live git dir is <paramref name="gitdir"/> a skip naming
    /// <paramref name="repo"/> (resolved) and the error, with no question.
    /// </summary>
    /// <param name="gitdir">The orphan's live git dir.</param>
    /// <param name="repo">The repo it belongs to.</param>
    /// <param name="error">The discovery error.</param>
    private static void AssertGuarded(string gitdir, string repo, DiscoveryError error)
    {
        ReviewSession review = Session([Orphan(OrphanKind.Folder, gitdir)], _ => Roomy, error);

        Assert.Null(review.Current);
        Plan.Skip skip = Assert.IsType<Plan.Skip>(OnlyDecision(review).Plan);
        Assert.Equal($"{PathResolver.Resolve(repo)} may still use it: {error.Message}", skip.Reason);
        Assert.Equal(new ReviewTotals { Skipped = 1 }, review.Totals());
    }

    /// <summary>A review of <paramref name="candidates"/> under <see cref="ReportSamples.Root"/>.</summary>
    /// <param name="candidates">The picks.</param>
    /// <param name="capacity">Reads a volume's bin settings.</param>
    /// <param name="errors">The discovery errors.</param>
    /// <returns>The session.</returns>
    private static ReviewSession Session(Candidate[] candidates, Func<string, BinCapacity?> capacity, params DiscoveryError[] errors) =>
        new(candidates, ReportSamples.Root, capacity, errors);

    /// <summary>The worktree <c>D:\repo.wt\feat</c> on <c>feat</c>, clean against <c>main</c>.</summary>
    /// <param name="mergeState">Its merge state.</param>
    /// <param name="bytes">Its size; <see langword="null"/> when the scan could not read it.</param>
    /// <returns>The candidate.</returns>
    private static RegisteredCandidate Worktree(MergeState mergeState, long? bytes) =>
        ReportSamples.Registered(
            @"repo.wt\feat",
            "feat",
            new WorktreeSignals
            {
                MergeState = mergeState,
                MergeStateAgainst = "main",
                Dirty = new Dirty(),
                Upstream = Upstream.Tracking(0),
                Size = bytes is { } size ? new SizeInfo { Bytes = size } : null,
            }
        ) with
        {
            Repo = ReportSamples.Under("repo"),
        };

    /// <summary>The empty orphan <c>D:\repo.wt\stray</c>; a link points to <c>D:\target</c>.</summary>
    /// <param name="kind">Folder or link.</param>
    /// <param name="liveGitdir">The live git dir its <c>.git</c> file names, if any.</param>
    /// <returns>The candidate.</returns>
    private static OrphanCandidate Orphan(OrphanKind kind, string? liveGitdir = null)
    {
        OrphanCandidate orphan = ReportSamples.Orphan(@"repo.wt\stray", kind, new SizeInfo());
        return orphan with
        {
            Orphan = orphan.Orphan with { LinkTarget = kind == OrphanKind.Link ? ReportSamples.Under("target") : null, LiveGitdir = liveGitdir },
        };
    }

    /// <summary>The only decision of a finished review.</summary>
    /// <param name="review">The review.</param>
    /// <returns>The decision.</returns>
    private static Decision OnlyDecision(ReviewSession review)
    {
        IReadOnlyList<Decision>? decisions = review.Decisions();
        Assert.NotNull(decisions);
        return Assert.Single(decisions);
    }
}
