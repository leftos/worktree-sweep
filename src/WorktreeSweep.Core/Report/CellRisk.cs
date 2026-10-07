namespace WorktreeSweep.Report;

/// <summary>How risky a table cell's value is, as the picker tints it.</summary>
public enum CellRisk
{
    /// <summary>Nothing to flag: the cell carries no value, or one that needs no attention.</summary>
    None,

    /// <summary>The value is safe (a merge that loses no commits).</summary>
    Good,

    /// <summary>The value deserves a second look (a branch with no commits of its own, uncommitted work, unpushed commits).</summary>
    Caution,

    /// <summary>The value may lose work (commits not merged, a detached HEAD not contained).</summary>
    Danger,
}
