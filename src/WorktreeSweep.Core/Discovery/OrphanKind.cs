namespace WorktreeSweep.Discovery;

/// <summary>What an orphan entry is on disk.</summary>
public enum OrphanKind
{
    /// <summary>A plain folder.</summary>
    Folder,

    /// <summary>A junction or symbolic link; removing it removes the link, never its target.</summary>
    Link,
}
