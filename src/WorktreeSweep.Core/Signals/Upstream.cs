namespace WorktreeSweep.Signals;

/// <summary>The state of a branch's upstream.</summary>
public sealed record Upstream
{
    private Upstream() { }

    /// <summary>Gets the state of a branch with no upstream configured.</summary>
    public static Upstream None { get; } = new() { Kind = UpstreamKind.None };

    /// <summary>Gets the state of a branch whose configured upstream ref no longer exists.</summary>
    public static Upstream Gone { get; } = new() { Kind = UpstreamKind.Gone };

    /// <summary>Gets which state this is.</summary>
    public UpstreamKind Kind { get; private init; }

    /// <summary>Gets, for <see cref="UpstreamKind.Tracking"/>, the commits on the branch that the upstream lacks; 0 otherwise.</summary>
    public int Ahead { get; private init; }

    /// <summary>The upstream exists.</summary>
    /// <param name="ahead">Commits on the branch that the upstream lacks.</param>
    /// <returns>The state.</returns>
    public static Upstream Tracking(int ahead) => new() { Kind = UpstreamKind.Tracking, Ahead = ahead };
}
