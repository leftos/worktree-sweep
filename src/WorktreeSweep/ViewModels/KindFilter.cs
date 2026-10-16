namespace WorktreeSweep.ViewModels;

/// <summary>Which kind of row the List screen shows.</summary>
public enum KindFilter
{
    /// <summary>Every row.</summary>
    All,

    /// <summary>Registered worktrees only.</summary>
    Worktree,

    /// <summary>Orphan folders only.</summary>
    Orphan,

    /// <summary>Orphan links only.</summary>
    Link,
}
