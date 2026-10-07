namespace WorktreeSweep.Report;

/// <summary>A process, by pid and image name.</summary>
public sealed record ProcessRef
{
    /// <summary>Gets the process id.</summary>
    public required int Pid { get; init; }

    /// <summary>Gets the image name.</summary>
    public required string Exe { get; init; }
}
