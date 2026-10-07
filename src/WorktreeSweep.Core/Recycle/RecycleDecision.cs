namespace WorktreeSweep.Recycle;

/// <summary>How a folder can be removed: <see cref="Recycle"/> or <see cref="AskPermanent"/>.</summary>
public abstract record RecycleDecision
{
    private protected RecycleDecision() { }

    /// <summary>The folder fits in the Recycle Bin.</summary>
    public sealed record Recycle : RecycleDecision;

    /// <summary>The folder cannot go to the Recycle Bin; a permanent delete needs the user's yes.</summary>
    /// <param name="Reason">Why it cannot, as a phrase about the folder, such as "its 20 GB is more than ...".</param>
    public sealed record AskPermanent(string Reason) : RecycleDecision;
}
