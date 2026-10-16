namespace WorktreeSweep.Report;

/// <summary>What a table row is, as its KIND cell names it.</summary>
public enum RowKind
{
    /// <summary>A registered worktree.</summary>
    Worktree,

    /// <summary>An orphan folder.</summary>
    Orphan,

    /// <summary>An orphan link.</summary>
    Link,
}
