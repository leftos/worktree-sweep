using WorktreeSweep.Unlock;

namespace WorktreeSweep.Removal;

/// <summary>Removes items one index at a time; the unit <see cref="TwoPassSweep"/> drives.</summary>
public interface ISweeper
{
    /// <summary>Removes item <paramref name="index"/>.</summary>
    /// <param name="index">The item's index.</param>
    /// <exception cref="LockedException">A file or folder in the item's tree is in use.</exception>
    /// <exception cref="Exception">Removing it failed for any other reason.</exception>
    void Remove(int index);

    /// <summary>
    /// Runs item <paramref name="index"/>'s follow-ups once it is gone. It must not throw: the sweep traces what it throws and keeps
    /// the item removed.
    /// </summary>
    /// <param name="index">The item's index.</param>
    void Finish(int index);

    /// <summary>
    /// Offers, once, to clear the locks on <paramref name="paths"/>. Called synchronously on the sweeping thread, before any retry.
    /// </summary>
    /// <param name="paths">The locked items' paths, in index order.</param>
    /// <returns>What the unlock flow did.</returns>
    /// <exception cref="Exception">The unlock flow failed.</exception>
    UnlockOutcome OfferUnlock(IReadOnlyList<string> paths);
}
