namespace WorktreeSweep.Removal;

/// <summary>What removing a pick does.</summary>
public abstract record RemoveAction
{
    private protected RemoveAction() { }

    /// <summary>Delete the junction or symbolic link only; its target is not touched.</summary>
    public sealed record RemoveLink : RemoveAction;

    /// <summary>The folder is already gone: <c>git worktree prune</c> in its repo.</summary>
    public sealed record PruneRegistration : RemoveAction;

    /// <summary>Delete the folder.</summary>
    /// <param name="Method">How it is deleted.</param>
    public sealed record Delete(DeleteMethod Method) : RemoveAction;
}
