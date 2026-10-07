namespace WorktreeSweep.Unlock;

/// <summary>What the unlock flow did.</summary>
public enum UnlockOutcome
{
    /// <summary>Every lock on the paths was cleared; the removals can be retried.</summary>
    Unlocked,

    /// <summary>Some locks were cleared; the removals can be retried and some may still fail.</summary>
    PartlyUnlocked,

    /// <summary>The flow ran but no lock was cleared.</summary>
    StillLocked,

    /// <summary>The locks were left in place without trying.</summary>
    Skipped,
}
