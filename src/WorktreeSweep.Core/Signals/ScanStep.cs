namespace WorktreeSweep.Signals;

/// <summary>A step of reading one candidate, in the order a registered worktree's steps run.</summary>
public enum ScanStep
{
    /// <summary>Its merge state against the default branches.</summary>
    Merge,

    /// <summary>Its modified and untracked counts.</summary>
    Status,

    /// <summary>Its branch's upstream.</summary>
    Upstream,

    /// <summary>Its last activity.</summary>
    Activity,

    /// <summary>Its size, walking every folder.</summary>
    Size,
}
