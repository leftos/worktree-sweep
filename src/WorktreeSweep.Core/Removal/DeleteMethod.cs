namespace WorktreeSweep.Removal;

/// <summary>How a folder is deleted.</summary>
public enum DeleteMethod
{
    /// <summary>Moved to the Recycle Bin.</summary>
    Recycle,

    /// <summary>Deleted for good.</summary>
    Permanent,
}
