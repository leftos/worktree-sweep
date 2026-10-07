using System.Diagnostics;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Removal;

/// <summary>Removes items in two passes: every item once, then the locked ones once more after a single unlock offer.</summary>
public static class TwoPassSweep
{
    /// <summary>
    /// Removes items <c>0</c> to <paramref name="count"/> - 1. Pass 1 attempts every item in order and runs an item's follow-ups right
    /// after it goes; the items whose <see cref="ISweeper.Remove"/> threw <see cref="LockedException"/> go to one
    /// <see cref="ISweeper.OfferUnlock"/>, with their paths in index order, and on <see cref="UnlockOutcome.Unlocked"/> or
    /// <see cref="UnlockOutcome.PartlyUnlocked"/> each locked item is retried once, with its follow-ups on success. No locked item means
    /// no offer. The offer runs synchronously on the calling thread.
    /// </summary>
    /// <param name="count">How many items there are.</param>
    /// <param name="sweeper">The unit that removes one item, and the one the offer is made to.</param>
    /// <param name="cancel">
    /// Stops the sweep: it is checked before each item in either pass and before the offer. A cancelled token makes the run stop and
    /// mark the rest <see cref="SweepResult.Cancelled"/> without throwing; an <see cref="OperationCanceledException"/> thrown by
    /// <paramref name="sweeper"/> itself propagates, as it is never caught.
    /// </param>
    /// <returns>One result per index.</returns>
    public static IReadOnlyList<SweepResult> Run(int count, ISweeper sweeper, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(sweeper);
        List<SweepResult> results = [];
        List<int> locked = [];
        List<string> lockedPaths = [];
        for (int index = 0; index < count; index++)
        {
            SweepResult result = cancel.IsCancellationRequested ? new SweepResult.Cancelled() : Attempt(sweeper, index);
            results.Add(result);
            if (result is SweepResult.Locked lockedItem)
            {
                locked.Add(index);
                lockedPaths.Add(lockedItem.Error.Path);
            }
        }
        if (locked.Count == 0 || cancel.IsCancellationRequested)
        {
            return results;
        }
        UnlockOutcome outcome = Offer(sweeper, lockedPaths);
        if (outcome is UnlockOutcome.Unlocked or UnlockOutcome.PartlyUnlocked)
        {
            Retry(sweeper, locked, results, cancel);
        }
        return results;
    }

    /// <summary>Attempts item <paramref name="index"/> and runs its follow-ups when it goes.</summary>
    /// <param name="sweeper">The unit that removes one item.</param>
    /// <param name="index">The item's index.</param>
    /// <returns>What became of the item.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled inside <see cref="ISweeper.Remove"/>.</exception>
    private static SweepResult Attempt(ISweeper sweeper, int index)
    {
        try
        {
            sweeper.Remove(index);
        }
        catch (LockedException error)
        {
            return new SweepResult.Locked(error);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new SweepResult.Failed(error);
        }
        Finish(sweeper, index);
        return new SweepResult.Removed();
    }

    /// <summary>
    /// Runs item <paramref name="index"/>'s follow-ups. A failure is traced and changes nothing about the item: it is gone either way.
    /// </summary>
    /// <param name="sweeper">The unit whose follow-ups run.</param>
    /// <param name="index">The item's index.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled inside <see cref="ISweeper.Finish"/>.</exception>
    private static void Finish(ISweeper sweeper, int index)
    {
        try
        {
            sweeper.Finish(index);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Trace.TraceWarning($"the follow-up after removing item {index} failed: {error.Message}");
        }
    }

    /// <summary>Offers once to clear the locks on the locked items' paths; an offer that throws counts as skipped.</summary>
    /// <param name="sweeper">The unit that offers.</param>
    /// <param name="paths">The locked items' paths, in index order.</param>
    /// <returns>What the unlock flow did.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled inside the offer.</exception>
    private static UnlockOutcome Offer(ISweeper sweeper, IReadOnlyList<string> paths)
    {
        try
        {
            return sweeper.OfferUnlock(paths);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Trace.TraceWarning($"the unlock step failed: {error.Message}");
            return UnlockOutcome.Skipped;
        }
    }

    /// <summary>Retries each locked item once, checking the token before each.</summary>
    /// <param name="sweeper">The unit that removes one item.</param>
    /// <param name="locked">The locked items' indices, in index order.</param>
    /// <param name="results">What pass 1 came to; a retried item's entry is replaced.</param>
    /// <param name="cancel">The token.</param>
    private static void Retry(ISweeper sweeper, List<int> locked, List<SweepResult> results, CancellationToken cancel)
    {
        foreach (int index in locked)
        {
            results[index] = cancel.IsCancellationRequested ? new SweepResult.Cancelled() : Attempt(sweeper, index);
        }
    }
}
