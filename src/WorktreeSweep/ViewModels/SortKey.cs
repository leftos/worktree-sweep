namespace WorktreeSweep.ViewModels;

/// <summary>The List screen column the shown rows are sorted by.</summary>
public enum SortKey
{
    /// <summary>The PATH column, by text.</summary>
    Path,

    /// <summary>The KIND column, by text.</summary>
    Kind,

    /// <summary>The BRANCH column, by text.</summary>
    Branch,

    /// <summary>The MERGE column, by risk and then text.</summary>
    Merge,

    /// <summary>The DIRTY column, by risk and then text.</summary>
    Dirty,

    /// <summary>The UPSTREAM column, by risk and then text.</summary>
    Upstream,

    /// <summary>The ACTIVE column, by the time of the last activity.</summary>
    Active,

    /// <summary>The SIZE column, by bytes.</summary>
    Size,

    /// <summary>The FLAGS column, by text.</summary>
    Flags,
}
