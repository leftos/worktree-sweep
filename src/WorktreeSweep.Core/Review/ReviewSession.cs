using System.Diagnostics;
using WorktreeSweep.Discovery;
using WorktreeSweep.Recycle;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;

namespace WorktreeSweep.Review;

/// <summary>
/// The questions asked about the ticked picks before anything is removed, answered one at a time. Per pick, in order: what it would
/// lose (or that only a link goes), whether to delete it permanently when it cannot go to the Recycle Bin, and whether to delete its
/// branch. A declined answer skips that pick's later questions. The answers become one <see cref="Decision"/> per pick.
/// </summary>
/// <remarks>
/// An orphan whose <see cref="Orphan.LiveGitdir"/> points into a repo discovery could not fully read is planned as
/// <see cref="Plan.Skip"/> and asked nothing: its repo's worktree list failed, the list left out this very worktree's
/// <c>gitdir</c> file, or the <c>worktrees</c> folder holding its git dir was unreadable. Both sides are resolved by
/// <see cref="PathResolver.Resolve"/> (junctions, symbolic links, subst drives, 8.3 names and <c>..</c> segments) before they compare
/// by <see cref="Discoverer.PathKey"/>.
/// </remarks>
public sealed class ReviewSession
{
    /// <summary>The scanned root, which the permanent-delete question's path is shown relative to.</summary>
    private readonly string root;

    /// <summary>One entry per pick, in candidate order.</summary>
    private readonly List<Entry> entries;

    /// <summary>Initializes a new instance of the <see cref="ReviewSession"/> class and moves to its first question.</summary>
    /// <param name="candidates">The picks, in list order.</param>
    /// <param name="root">The scanned root; paths are shown relative to it.</param>
    /// <param name="capacity">
    /// Reads the Recycle Bin settings of a pick's volume (<see langword="null"/> when unknown); called once for each pick whose plan
    /// depends on it (see <see cref="RemovalPlanner.NeedsCapacity"/>) and that is not skipped for a discovery error, and for no other.
    /// </param>
    /// <param name="discoveryErrors">The problems discovery met; an orphan in a repo one names is skipped.</param>
    public ReviewSession(
        IReadOnlyList<Candidate> candidates,
        string root,
        Func<string, BinCapacity?> capacity,
        IReadOnlyList<DiscoveryError> discoveryErrors
    )
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(discoveryErrors);
        this.root = root;
        ErrorKey[] errorKeys = [.. discoveryErrors.Select(error => new ErrorKey(Key(error.Repo), Key(error.Path), error.Message))];
        entries = [.. candidates.Select(candidate => NewEntry(candidate, capacity, errorKeys))];
        Current = NextStep();
    }

    /// <summary>Gets the question to answer now; <see langword="null"/> once every question is answered.</summary>
    public ReviewStep? Current { get; private set; }

    /// <summary>
    /// Answers the current question and moves to the next one, skipping those the answer made moot. Does nothing when every question
    /// is answered.
    /// </summary>
    /// <param name="yes">The answer.</param>
    public void Answer(bool yes)
    {
        if (Current is not { } step)
        {
            return;
        }
        Entry entry = entries[step.Pick];
        switch (step.Kind)
        {
            case StepKind.Loss or StepKind.Link:
                entry.LossAnswer = yes;
                break;
            case StepKind.Permanent:
                entry.PermanentAnswer = yes;
                break;
            case StepKind.Branch:
                entry.BranchAnswer = yes;
                break;
            default:
                throw new UnreachableException($"unknown step kind {step.Kind}");
        }
        Current = NextStep();
    }

    /// <summary>What the answers add up to.</summary>
    /// <returns>The totals; <see langword="null"/> until every question is answered.</returns>
    public ReviewTotals? Totals()
    {
        if (Current is not null)
        {
            return null;
        }
        var totals = new ReviewTotals();
        foreach (Entry entry in entries)
        {
            Plan plan = entry.ResolvedPlan();
            if (entry.Branch(plan) is BranchChoice.Delete)
            {
                totals = totals with { Branches = totals.Branches + 1 };
            }
            totals = Count(totals, plan, Math.Max(entry.Candidate.SizeBytes ?? 0, 0));
        }
        return totals;
    }

    /// <summary>One decision per pick, in candidate order.</summary>
    /// <returns>The decisions; <see langword="null"/> until every question is answered.</returns>
    public IReadOnlyList<Decision>? Decisions()
    {
        if (Current is not null)
        {
            return null;
        }
        return
        [
            .. entries.Select(entry =>
            {
                Plan plan = entry.ResolvedPlan();
                return new Decision(entry.Candidate, plan, entry.Branch(plan));
            }),
        ];
    }

    /// <summary>The entry for one pick, its capacity read only when its plan depends on it.</summary>
    /// <param name="candidate">The pick.</param>
    /// <param name="capacity">Reads the Recycle Bin settings of a volume.</param>
    /// <param name="errors">The problems discovery met, keyed.</param>
    /// <returns>The entry, unanswered.</returns>
    private static Entry NewEntry(Candidate candidate, Func<string, BinCapacity?> capacity, IReadOnlyList<ErrorKey> errors)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        string? guard = candidate is OrphanCandidate orphan ? DiscoveryGuard(orphan.Orphan, errors) : null;
        BinCapacity? bin = guard is null && RemovalPlanner.NeedsCapacity(candidate) ? capacity(candidate.Path) : null;
        return new Entry
        {
            Candidate = candidate,
            Guard = guard,
            Loss = LossText.For(candidate),
            Need = RemovalPlanner.Plan(candidate, bin),
            Offer = candidate is RegisteredCandidate registered ? BranchOffers.For(registered) : null,
        };
    }

    /// <summary>Why an orphan is skipped for a discovery error in the repo its live git dir belongs to.</summary>
    /// <param name="orphan">The orphan.</param>
    /// <param name="errors">The problems discovery met, keyed.</param>
    /// <returns>The skip reason; <see langword="null"/> when no error guards the orphan.</returns>
    private static string? DiscoveryGuard(Orphan orphan, IReadOnlyList<ErrorKey> errors)
    {
        if (orphan.LiveGitdir is not { } gitdir)
        {
            return null;
        }
        string resolved = PathResolver.Resolve(gitdir);
        string repo = PathResolver.Resolve(LossText.RepoOfGitdir(resolved));
        string repoKey = Discoverer.PathKey(repo);
        string gitdirFileKey = Key(Path.Join(resolved, "gitdir"));
        string? worktreesKey = Path.GetDirectoryName(resolved) is { } worktrees ? Key(worktrees) : null;
        ErrorKey? hit = errors.FirstOrDefault(error =>
            (error.ItemKey == error.RepoKey && error.ItemKey == repoKey) || error.ItemKey == gitdirFileKey || error.ItemKey == worktreesKey
        );
        return hit is null ? null : $"{repo} may still use it: {hit.Message}";
    }

    /// <summary>
    /// The comparison key of <paramref name="path"/>: resolved by <see cref="PathResolver.Resolve"/>, then <see cref="Discoverer.PathKey"/>.
    /// </summary>
    /// <param name="path">A path.</param>
    /// <returns>The key.</returns>
    private static string Key(string path) => Discoverer.PathKey(PathResolver.Resolve(path));

    /// <summary><paramref name="totals"/> with one more pick planned as <paramref name="plan"/>.</summary>
    /// <param name="totals">The totals so far.</param>
    /// <param name="plan">The pick's plan.</param>
    /// <param name="bytes">The pick's size; 0 when unknown.</param>
    /// <returns>The new totals.</returns>
    private static ReviewTotals Count(ReviewTotals totals, Plan plan, long bytes) =>
        plan switch
        {
            Plan.Run { Action: RemoveAction.Delete { Method: DeleteMethod.Recycle } } => totals with
            {
                Recycle = totals.Recycle + 1,
                RecycleBytes = totals.RecycleBytes + bytes,
            },
            Plan.Run { Action: RemoveAction.Delete { Method: DeleteMethod.Permanent } } => totals with
            {
                Permanent = totals.Permanent + 1,
                PermanentBytes = totals.PermanentBytes + bytes,
            },
            Plan.Run { Action: RemoveAction.RemoveLink } => totals with { Links = totals.Links + 1 },
            Plan.Run { Action: RemoveAction.PruneRegistration } => totals with { Prunes = totals.Prunes + 1 },
            Plan.Skip => totals with { Skipped = totals.Skipped + 1 },
            _ => throw new UnreachableException($"unknown plan {plan}"),
        };

    /// <summary>The first unanswered question of any pick.</summary>
    /// <returns>The question; <see langword="null"/> when every question is answered.</returns>
    private ReviewStep? NextStep()
    {
        for (int pick = 0; pick < entries.Count; pick++)
        {
            if (StepFor(pick, entries[pick]) is { } step)
            {
                return step;
            }
        }
        return null;
    }

    /// <summary>The first unanswered question about one pick.</summary>
    /// <param name="pick">The pick's index.</param>
    /// <param name="entry">The pick's entry.</param>
    /// <returns>The question; <see langword="null"/> when it has none left.</returns>
    private ReviewStep? StepFor(int pick, Entry entry)
    {
        if (entry.Settled)
        {
            return null;
        }
        if (entry.Loss is { } loss && entry.LossAnswer is null)
        {
            return new ReviewStep(pick, loss.IsLink ? StepKind.Link : StepKind.Loss, loss.Text, loss.Question, DefaultAnswer: false);
        }
        if (entry.Need is PlanNeed.AskPermanent ask && entry.PermanentAnswer is null)
        {
            string name = ReportTable.RelativePath(entry.Candidate.Path, root);
            string body = $"{name} cannot go to the Recycle Bin: {ask.Reason}; deleting it permanently cannot be undone.";
            return new ReviewStep(pick, StepKind.Permanent, body, "Delete it permanently?", DefaultAnswer: false);
        }
        if (entry.Offer is { } offer && entry.BranchAnswer is null)
        {
            return new ReviewStep(pick, StepKind.Branch, offer.Context, offer.Question, DefaultAnswer: true);
        }
        return null;
    }

    /// <summary>A discovery error with its repo and path keyed by <see cref="Key"/>.</summary>
    /// <param name="RepoKey">The key of the repo the problem is in.</param>
    /// <param name="ItemKey">The key of what the problem is about.</param>
    /// <param name="Message">What went wrong.</param>
    private sealed record ErrorKey(string RepoKey, string ItemKey, string Message);

    /// <summary>One pick, what can be asked about it, and the answers so far.</summary>
    private sealed class Entry
    {
        /// <summary>Gets the pick.</summary>
        public required Candidate Candidate { get; init; }

        /// <summary>Gets why a discovery error skips the pick; <see langword="null"/> when none does.</summary>
        public required string? Guard { get; init; }

        /// <summary>Gets what removing it loses; <see langword="null"/> when nothing is.</summary>
        public required Loss? Loss { get; init; }

        /// <summary>Gets how it can be removed before any question is asked.</summary>
        public required PlanNeed Need { get; init; }

        /// <summary>Gets the branch deletion offered for it; <see langword="null"/> when none applies.</summary>
        public required BranchOffer? Offer { get; init; }

        /// <summary>Gets or sets the answer to the loss or link question.</summary>
        public bool? LossAnswer { get; set; }

        /// <summary>Gets or sets the answer to the permanent-delete question.</summary>
        public bool? PermanentAnswer { get; set; }

        /// <summary>Gets or sets the answer to the branch question.</summary>
        public bool? BranchAnswer { get; set; }

        /// <summary>Gets a value indicating whether no question is left: the pick is guarded, or an answer declined it.</summary>
        public bool Settled => Guard is not null || LossAnswer == false || PermanentAnswer == false;

        /// <summary>The pick's plan given the answers so far.</summary>
        /// <returns>Its action, or why it is skipped.</returns>
        public Plan ResolvedPlan()
        {
            if (Guard is not null)
            {
                return new Plan.Skip(Guard);
            }
            if (Loss is not null && LossAnswer != true)
            {
                return new Plan.Skip("not confirmed");
            }
            return Need switch
            {
                PlanNeed.Run run => new Plan.Run(run.Action),
                PlanNeed.AskPermanent when PermanentAnswer == true => new Plan.Run(new RemoveAction.Delete(DeleteMethod.Permanent)),
                PlanNeed.AskPermanent ask => new Plan.Skip($"permanent delete declined; {ask.Reason}"),
                _ => throw new UnreachableException($"unknown plan need {Need}"),
            };
        }

        /// <summary>The pick's branch choice given its <paramref name="plan"/>: offered only when it runs.</summary>
        /// <param name="plan">The pick's plan.</param>
        /// <returns>The choice.</returns>
        public BranchChoice Branch(Plan plan)
        {
            if (Offer is null || plan is not Plan.Run)
            {
                return new BranchChoice.NotOffered();
            }
            return BranchAnswer == false ? new BranchChoice.Keep(Offer) : new BranchChoice.Delete(Offer);
        }
    }
}
