namespace WorktreeSweep.Signals;

/// <summary>The kinds of <see cref="Upstream"/>.</summary>
public enum UpstreamKind
{
    /// <summary>No upstream is configured.</summary>
    None,

    /// <summary>An upstream is configured but its ref no longer exists.</summary>
    Gone,

    /// <summary>The upstream exists.</summary>
    Tracking,
}
