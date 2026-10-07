namespace WorktreeSweep.Unlock;

/// <summary>What the elevated session's exit code means to the unelevated side.</summary>
public static class UnlockOutcomes
{
    /// <summary>Maps the elevated session's exit code to what the unlock flow did.</summary>
    /// <param name="code">The exit code <c>sudo</c> returned for the elevated session.</param>
    /// <returns><see cref="UnlockOutcome.Unlocked"/> for <see cref="UnlockExit.AllClear"/>, <see cref="UnlockOutcome.PartlyUnlocked"/>
    /// for <see cref="UnlockExit.SomeLeft"/>, <see cref="UnlockOutcome.StillLocked"/> for <see cref="UnlockExit.NothingDone"/>, and
    /// <see langword="null"/> for any other code.</returns>
    public static UnlockOutcome? ForExit(int code) =>
        code switch
        {
            UnlockExit.AllClear => UnlockOutcome.Unlocked,
            UnlockExit.SomeLeft => UnlockOutcome.PartlyUnlocked,
            UnlockExit.NothingDone => UnlockOutcome.StillLocked,
            _ => null,
        };
}
