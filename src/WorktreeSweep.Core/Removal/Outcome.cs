using WorktreeSweep.Report;

namespace WorktreeSweep.Removal;

/// <summary>What happened to one pick.</summary>
public abstract record Outcome
{
    private protected Outcome() { }

    /// <summary>Moved to the Recycle Bin.</summary>
    /// <param name="Size">The size the scan read; <see langword="null"/> when unknown.</param>
    public sealed record Recycled(KnownSize? Size) : Outcome;

    /// <summary>Deleted for good.</summary>
    /// <param name="Size">The size the scan read; <see langword="null"/> when unknown.</param>
    public sealed record Permanent(KnownSize? Size) : Outcome;

    /// <summary>The link was deleted; its target was not touched.</summary>
    public sealed record LinkRemoved : Outcome;

    /// <summary>Its registration was pruned (the folder was already gone).</summary>
    public sealed record Pruned : Outcome;

    /// <summary>Left in place.</summary>
    /// <param name="Reason">Why it was left.</param>
    public sealed record Skipped(string Reason) : Outcome;

    /// <summary>Removing it failed.</summary>
    /// <param name="Reason">Why it failed.</param>
    public sealed record Failed(string Reason) : Outcome;
}
