namespace WorktreeSweep.Removal;

/// <summary>Where a removal is, by index into its decisions. A retried pick reports both again.</summary>
public abstract record Progress
{
    private protected Progress() { }

    /// <summary>Removing decision <paramref name="Index"/> started.</summary>
    /// <param name="Index">The decision's index, from zero.</param>
    public sealed record Started(int Index) : Progress;

    /// <summary>Decision <paramref name="Index"/> is finished, removed or failed.</summary>
    /// <param name="Index">The decision's index, from zero.</param>
    public sealed record Done(int Index) : Progress;
}
