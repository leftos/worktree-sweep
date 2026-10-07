using System.Diagnostics;
using WorktreeSweep.Git;
using WorktreeSweep.Report;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Removal;

/// <summary>Removes every decided pick with the two-pass sweep and runs each one's follow-ups once it is gone.</summary>
public static class Remover
{
    /// <summary>
    /// Removes and follows up every decided pick with <see cref="TwoPassSweep"/>. A <see cref="Plan.Skip"/> decision becomes
    /// <see cref="Outcome.Skipped"/> without touching the disk, reports no <see cref="Progress"/> and is never offered for unlocking.
    /// A pick still locked after the retry pass is <c>failed: locked (&lt;file or path&gt;)</c>, one that fails any other way is
    /// <see cref="Outcome.Failed"/> with the failure's message, and one the token stopped is <c>skipped (cancelled)</c>. A registered
    /// worktree whose git lock was lifted and that was not removed gets its lock back, with its reason.
    /// </summary>
    /// <param name="decisions">The picks, each with its plan and branch choice.</param>
    /// <param name="onProgress">
    /// Hears each runnable pick start and finish, by index into <paramref name="decisions"/>, synchronously on this thread; a retried
    /// pick reports both again. What it throws, other than <see cref="OperationCanceledException"/>, is traced and ignored.
    /// </param>
    /// <param name="offerUnlock">
    /// Called at most once, synchronously on this thread, with the locked picks' paths; what it throws, other than
    /// <see cref="OperationCanceledException"/>, is traced and counts as <see cref="UnlockOutcome.Skipped"/>.
    /// </param>
    /// <param name="cancel">Checked before each pick in either pass and before the unlock offer.</param>
    /// <returns>One entry per decision, in order.</returns>
    public static IReadOnlyList<Swept> RemovePicks(
        IReadOnlyList<Decision> decisions,
        Action<Progress> onProgress,
        Func<IReadOnlyList<string>, UnlockOutcome> offerUnlock,
        CancellationToken cancel
    )
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(onProgress);
        ArgumentNullException.ThrowIfNull(offerUnlock);
        List<int> runnable = [.. Enumerable.Range(0, decisions.Count).Where(index => decisions[index].Plan is Plan.Run)];
        var sweeper = new LiveSweeper(decisions, runnable, onProgress, offerUnlock);
        IReadOnlyList<SweepResult> results = TwoPassSweep.Run(runnable.Count, sweeper, cancel);

        var swept = new Swept[decisions.Count];
        for (int index = 0; index < decisions.Count; index++)
        {
            Decision decision = decisions[index];
            if (decision.Plan is Plan.Skip skip)
            {
                swept[index] = new Swept(decision.Candidate, new Outcome.Skipped(skip.Reason), []);
            }
        }
        for (int item = 0; item < runnable.Count; item++)
        {
            Decision decision = decisions[runnable[item]];
            SweepResult result = results[item];
            IReadOnlyList<string> notes =
                result is not SweepResult.Removed && sweeper.GitUnlocked(item)
                    ? Relock((RegisteredCandidate)decision.Candidate)
                    : sweeper.NotesOf(item);
            swept[runnable[item]] = new Swept(decision.Candidate, OutcomeOf(decision, result), notes);
        }
        return swept;
    }

    /// <summary>What a runnable pick's sweep result means for it.</summary>
    /// <param name="decision">The pick, whose plan is <see cref="Plan.Run"/>.</param>
    /// <param name="result">What the sweep came to.</param>
    /// <returns>Its outcome.</returns>
    private static Outcome OutcomeOf(Decision decision, SweepResult result) =>
        result switch
        {
            SweepResult.Removed => Removed(ActionOf(decision), decision.Candidate),
            SweepResult.Locked locked => new Outcome.Failed($"locked ({locked.Error.FirstLockedFile ?? locked.Error.Path})"),
            SweepResult.Failed failed => new Outcome.Failed(failed.Error.Message),
            SweepResult.Cancelled => new Outcome.Skipped("cancelled"),
            _ => throw new UnreachableException($"unknown sweep result {result}"),
        };

    /// <summary>The outcome of a pick removed by <paramref name="action"/>; an unknown size counts as zero bytes.</summary>
    /// <param name="action">How it was removed.</param>
    /// <param name="candidate">The pick.</param>
    /// <returns>Its outcome.</returns>
    private static Outcome Removed(RemoveAction action, Candidate candidate)
    {
        long bytes = Math.Max(candidate.SizeBytes ?? 0, 0);
        return action switch
        {
            RemoveAction.RemoveLink => new Outcome.LinkRemoved(),
            RemoveAction.PruneRegistration => new Outcome.Pruned(),
            RemoveAction.Delete { Method: DeleteMethod.Recycle } => new Outcome.Recycled(bytes),
            RemoveAction.Delete { Method: DeleteMethod.Permanent } => new Outcome.Permanent(bytes),
            _ => throw new UnreachableException($"unknown remove action {action}"),
        };
    }

    /// <summary>The action of a runnable decision.</summary>
    /// <param name="decision">The decision, whose plan is <see cref="Plan.Run"/>.</param>
    /// <returns>How it is removed.</returns>
    private static RemoveAction ActionOf(Decision decision) => ((Plan.Run)decision.Plan).Action;

    /// <summary>Puts back the git lock of a worktree that was not removed.</summary>
    /// <param name="registered">The worktree.</param>
    /// <returns>No note when the lock is back; <c>git lock not restored: &lt;why&gt;</c> when git failed.</returns>
    private static IReadOnlyList<string> Relock(RegisteredCandidate registered)
    {
        try
        {
            CandidateRemover.GitRelock(registered);
            return [];
        }
        catch (GitException error)
        {
            return [$"git lock not restored: {error.Message}"];
        }
    }

    /// <summary>
    /// The live unit the sweep drives: item <c>i</c> is the <c>i</c>-th runnable decision. A registered worktree's git lock is lifted
    /// once, before its first attempt.
    /// </summary>
    /// <param name="decisions">Every decision.</param>
    /// <param name="runnable">The indices of the runnable ones, in order.</param>
    /// <param name="onProgress">The progress callback.</param>
    /// <param name="offerUnlock">The unlock offer callback.</param>
    private sealed class LiveSweeper(
        IReadOnlyList<Decision> decisions,
        List<int> runnable,
        Action<Progress> onProgress,
        Func<IReadOnlyList<string>, UnlockOutcome> offerUnlock
    ) : ISweeper
    {
        private readonly HashSet<int> gitUnlocked = [];
        private readonly Dictionary<int, IReadOnlyList<string>> notes = [];

        /// <summary>Whether item <paramref name="index"/>'s git lock was lifted.</summary>
        /// <param name="index">The item's index.</param>
        /// <returns><see langword="true"/> once <see cref="CandidateRemover.GitUnlock"/> ran for it.</returns>
        public bool GitUnlocked(int index) => gitUnlocked.Contains(index);

        /// <summary>Item <paramref name="index"/>'s follow-up notes; empty until it is removed.</summary>
        /// <param name="index">The item's index.</param>
        /// <returns>The notes.</returns>
        public IReadOnlyList<string> NotesOf(int index) => notes.GetValueOrDefault(index, []);

        /// <inheritdoc/>
        public void Remove(int index)
        {
            int decision = runnable[index];
            Report(new Progress.Started(decision));
            try
            {
                TryRemove(index);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Report(new Progress.Done(decision));
                throw;
            }
        }

        /// <inheritdoc/>
        public void Finish(int index)
        {
            Decision decision = decisions[runnable[index]];
            try
            {
                notes[index] = FollowUps.AfterRemoved(decision.Candidate, ActionOf(decision), decision.Branch);
            }
            finally
            {
                Report(new Progress.Done(runnable[index]));
            }
        }

        /// <inheritdoc/>
        public UnlockOutcome OfferUnlock(IReadOnlyList<string> paths) => offerUnlock(paths);

        /// <summary>Lifts a registered worktree's git lock the first time through, then removes the pick.</summary>
        /// <param name="index">The item's index.</param>
        private void TryRemove(int index)
        {
            Decision decision = decisions[runnable[index]];
            if (decision.Candidate is RegisteredCandidate registered && !gitUnlocked.Contains(index))
            {
                CandidateRemover.GitUnlock(registered);
                _ = gitUnlocked.Add(index);
            }
            CandidateRemover.Remove(decision.Candidate, ActionOf(decision));
        }

        /// <summary>Tells the progress callback; what it throws, other than a cancellation, is traced and ignored.</summary>
        /// <param name="progress">Where the removal is.</param>
        private void Report(Progress progress)
        {
            try
            {
                onProgress(progress);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Trace.TraceWarning($"the progress callback failed on {progress}: {error.Message}");
            }
        }
    }
}
