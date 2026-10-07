namespace WorktreeSweep.Removal;

/// <summary>What happens to a removed worktree's branch.</summary>
public abstract record BranchChoice
{
    private protected BranchChoice() { }

    /// <summary>No branch deletion applies.</summary>
    public sealed record NotOffered : BranchChoice;

    /// <summary>Delete it once the worktree is gone.</summary>
    /// <param name="Offer">The offer that was asked about.</param>
    public sealed record Delete(BranchOffer Offer) : BranchChoice;

    /// <summary>The user kept it.</summary>
    /// <param name="Offer">The offer that was asked about.</param>
    public sealed record Keep(BranchOffer Offer) : BranchChoice;
}
