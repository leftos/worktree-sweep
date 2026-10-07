using WorktreeSweep.Removal;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The two-pass sweep: one unlock offer for the locked picks, one retry each, and how cancel stops it.</summary>
public sealed class TwoPassSweepTests
{
    /// <summary>One pick locks until the offer, one stays locked through the retry and one always fails: one offer, retried in index order.</summary>
    [Fact]
    public void LockedPicksTriggerOneOfferAndAreRetried()
    {
        var sweeper = new FakeSweeper(UnlockOutcome.PartlyUnlocked);

        IReadOnlyList<SweepResult> results = TwoPassSweep.Run(4, sweeper, CancellationToken.None);

        Assert.Equal(["item1", "item2"], Assert.Single(sweeper.Offers));
        Assert.Equal([0, 1, 2, 3, 1, 2], sweeper.Removes);
        Assert.Equal([0, 1], sweeper.Finished);
        Assert.IsType<SweepResult.Removed>(results[0]);
        Assert.IsType<SweepResult.Removed>(results[1]);
        Assert.IsType<SweepResult.Locked>(results[2]);
        Assert.IsType<SweepResult.Failed>(results[3]);
    }

    /// <summary>A skipped offer leaves the locked picks locked, with no retry.</summary>
    [Fact]
    public void SkippedUnlockDoesNotRetry()
    {
        var sweeper = new FakeSweeper(UnlockOutcome.Skipped);

        IReadOnlyList<SweepResult> results = TwoPassSweep.Run(4, sweeper, CancellationToken.None);

        Assert.Single(sweeper.Offers);
        Assert.Equal([0, 1, 2, 3], sweeper.Removes);
        Assert.IsType<SweepResult.Locked>(results[1]);
    }

    /// <summary>With nothing locked, no offer is made at all.</summary>
    [Fact]
    public void NoLocksMeansNoOffer()
    {
        var sweeper = new FakeSweeper(UnlockOutcome.Unlocked);
        sweeper.LockedUntilUnlock.Clear();
        sweeper.AlwaysLocked.Clear();

        TwoPassSweep.Run(4, sweeper, CancellationToken.None);

        Assert.Empty(sweeper.Offers);
        Assert.Equal([0, 1, 2], sweeper.Finished);
    }

    /// <summary>An offer that throws counts as skipped: the locked picks stay locked and nothing propagates.</summary>
    [Fact]
    public void ThrowingOfferCountsAsSkipped()
    {
        var sweeper = new FakeSweeper(UnlockOutcome.Unlocked) { ThrowOnOffer = true };

        IReadOnlyList<SweepResult> results = TwoPassSweep.Run(4, sweeper, CancellationToken.None);

        Assert.Single(sweeper.Offers);
        Assert.Equal([0, 1, 2, 3], sweeper.Removes);
        Assert.IsType<SweepResult.Locked>(results[1]);
        Assert.IsType<SweepResult.Locked>(results[2]);
        Assert.IsType<SweepResult.Failed>(results[3]);
    }

    /// <summary>Follow-ups that throw leave the pick removed and the sweep goes on.</summary>
    [Fact]
    public void FinishThatThrowsKeepsTheItemRemovedAndGoesOn()
    {
        var sweeper = new FakeSweeper(UnlockOutcome.Unlocked);
        sweeper.LockedUntilUnlock.Clear();
        sweeper.AlwaysLocked.Clear();
        sweeper.FailingFinish.Add(0);

        IReadOnlyList<SweepResult> results = TwoPassSweep.Run(4, sweeper, CancellationToken.None);

        Assert.IsType<SweepResult.Removed>(results[0]);
        Assert.Equal([0, 1, 2, 3], sweeper.Removes);
        Assert.Equal([0, 1, 2], sweeper.Finished);
        Assert.IsType<SweepResult.Failed>(results[3]);
    }

    /// <summary>A token cancelled before the sweep starts attempts nothing and makes no offer.</summary>
    [Fact]
    public void CancelBeforeStartSkipsEverythingAndMakesNoOffer()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var sweeper = new FakeSweeper(UnlockOutcome.Unlocked);

        IReadOnlyList<SweepResult> results = TwoPassSweep.Run(4, sweeper, source.Token);

        Assert.Empty(sweeper.Removes);
        Assert.Empty(sweeper.Offers);
        Assert.All(results, result => Assert.IsType<SweepResult.Cancelled>(result));
    }

    /// <summary>A token cancelled inside the first pass stops after the pick in flight, which keeps its result, and makes no offer.</summary>
    [Fact]
    public void CancelDuringPassOneStopsAfterTheCurrentItem()
    {
        using var source = new CancellationTokenSource();
        var sweeper = new FakeSweeper(UnlockOutcome.Unlocked) { CancelOn = 1, Source = source };

        IReadOnlyList<SweepResult> results = TwoPassSweep.Run(4, sweeper, source.Token);

        Assert.Equal([0, 1], sweeper.Removes);
        Assert.Empty(sweeper.Offers);
        Assert.IsType<SweepResult.Removed>(results[0]);
        Assert.IsType<SweepResult.Locked>(results[1]);
        Assert.IsType<SweepResult.Cancelled>(results[2]);
        Assert.IsType<SweepResult.Cancelled>(results[3]);
    }

    /// <summary>A token cancelled inside the offer retries nothing: the locked picks come back cancelled, the other results stand.</summary>
    [Fact]
    public void CancelDuringTheOfferSkipsTheRetries()
    {
        using var source = new CancellationTokenSource();
        var sweeper = new FakeSweeper(UnlockOutcome.Unlocked) { CancelOnOffer = true, Source = source };

        IReadOnlyList<SweepResult> results = TwoPassSweep.Run(4, sweeper, source.Token);

        Assert.Single(sweeper.Offers);
        Assert.Equal([0, 1, 2, 3], sweeper.Removes);
        Assert.IsType<SweepResult.Removed>(results[0]);
        Assert.IsType<SweepResult.Cancelled>(results[1]);
        Assert.IsType<SweepResult.Cancelled>(results[2]);
        Assert.IsType<SweepResult.Failed>(results[3]);
    }

    /// <summary>
    /// A sweeper scripted by index: some picks lock until the offer answers that locks were cleared, one is always locked and one
    /// always fails. It records every call so the sweep's order can be asserted.
    /// </summary>
    /// <param name="outcome">What the offer answers.</param>
    private sealed class FakeSweeper(UnlockOutcome outcome) : ISweeper
    {
        /// <summary>Gets the picks that are locked until the offer answers that locks were cleared.</summary>
        public HashSet<int> LockedUntilUnlock { get; } = [1];

        /// <summary>Gets the picks that are locked whatever the offer answers.</summary>
        public HashSet<int> AlwaysLocked { get; } = [2];

        /// <summary>Gets the picks whose removal fails for another reason.</summary>
        public HashSet<int> Failing { get; } = [3];

        /// <summary>Gets the picks whose follow-ups throw.</summary>
        public HashSet<int> FailingFinish { get; } = [];

        /// <summary>Gets the picks <see cref="Remove"/> was called for, in order.</summary>
        public List<int> Removes { get; } = [];

        /// <summary>Gets the picks <see cref="Finish"/> was called for, in order.</summary>
        public List<int> Finished { get; } = [];

        /// <summary>Gets the paths each offer carried, in order.</summary>
        public List<IReadOnlyList<string>> Offers { get; } = [];

        /// <summary>Gets the pick whose removal cancels the token; <see langword="null"/> when none does.</summary>
        public int? CancelOn { get; init; }

        /// <summary>Gets a value indicating whether the offer cancels the token before answering.</summary>
        public bool CancelOnOffer { get; init; }

        /// <summary>Gets the token <see cref="CancelOn"/> or <see cref="CancelOnOffer"/> cancels; <see langword="null"/> when neither does.</summary>
        public CancellationTokenSource? Source { get; init; }

        /// <summary>Gets a value indicating whether the offer throws instead of answering.</summary>
        public bool ThrowOnOffer { get; init; }

        private bool unlocked;

        /// <inheritdoc/>
        public void Remove(int index)
        {
            Removes.Add(index);
            if (index == CancelOn)
            {
                Source?.Cancel();
            }
            if (AlwaysLocked.Contains(index) || (LockedUntilUnlock.Contains(index) && !unlocked))
            {
                throw new LockedException($"item{index}", firstLockedFile: null);
            }
            if (Failing.Contains(index))
            {
                throw new InvalidOperationException("broken");
            }
        }

        /// <inheritdoc/>
        public void Finish(int index)
        {
            Finished.Add(index);
            if (FailingFinish.Contains(index))
            {
                throw new InvalidOperationException("broken follow-up");
            }
        }

        /// <inheritdoc/>
        public UnlockOutcome OfferUnlock(IReadOnlyList<string> paths)
        {
            Offers.Add(paths);
            if (CancelOnOffer)
            {
                Source?.Cancel();
            }
            if (ThrowOnOffer)
            {
                throw new InvalidOperationException("no unlock flow");
            }
            unlocked = outcome is UnlockOutcome.Unlocked or UnlockOutcome.PartlyUnlocked;
            return outcome;
        }
    }
}
