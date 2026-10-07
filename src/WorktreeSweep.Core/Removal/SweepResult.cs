namespace WorktreeSweep.Removal;

/// <summary>What one item of a two-pass sweep came to.</summary>
public abstract record SweepResult
{
    private protected SweepResult() { }

    /// <summary>The item was removed, in either pass.</summary>
    public sealed record Removed : SweepResult;

    /// <summary>The item is still locked after the sweep.</summary>
    /// <param name="Error">The lock its last attempt hit.</param>
    public sealed record Locked(LockedException Error) : SweepResult;

    /// <summary>Removing the item failed.</summary>
    /// <param name="Error">The failure.</param>
    public sealed record Failed(Exception Error) : SweepResult;

    /// <summary>The token was cancelled before the item was attempted, or before its retry: it was left in place.</summary>
    public sealed record Cancelled : SweepResult;
}
