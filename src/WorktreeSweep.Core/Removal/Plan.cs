namespace WorktreeSweep.Removal;

/// <summary>A pick's action, or why it is skipped.</summary>
public abstract record Plan
{
    private protected Plan() { }

    /// <summary>Remove it this way.</summary>
    /// <param name="Action">How it is removed.</param>
    public sealed record Run(RemoveAction Action) : Plan;

    /// <summary>Leave it, for this reason.</summary>
    /// <param name="Reason">Why it is left.</param>
    public sealed record Skip(string Reason) : Plan;
}
