namespace WorktreeSweep.Removal;

/// <summary>How a pick can be removed before any question is asked.</summary>
public abstract record PlanNeed
{
    private protected PlanNeed() { }

    /// <summary>Remove it this way.</summary>
    /// <param name="Action">How it is removed.</param>
    public sealed record Run(RemoveAction Action) : PlanNeed;

    /// <summary>It cannot go to the Recycle Bin, for the reason given; a permanent delete needs the user's yes.</summary>
    /// <param name="Reason">Why it cannot.</param>
    public sealed record AskPermanent(string Reason) : PlanNeed;
}
