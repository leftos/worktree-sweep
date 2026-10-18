using WorktreeSweep.Scan;

namespace WorktreeSweep.ViewModels;

/// <summary>How far a scan has got, as one copy taken from its <see cref="ScanTally"/> and shown on the Scanning screen.</summary>
public sealed record ScanSnapshot
{
    /// <summary>Gets the child of the root discovery is classifying, from 1; 0 before the first.</summary>
    public int Child { get; init; }

    /// <summary>Gets how many children the root has; 0 before discovery reports one.</summary>
    public int Children { get; init; }

    /// <summary>Gets what discovery found; <see langword="null"/> while it runs.</summary>
    public ScanProgress.Discovered? Discovered { get; init; }

    /// <summary>Gets the repos whose default branches are looked up so far.</summary>
    public int ReposDone { get; init; }

    /// <summary>Gets the candidates read so far.</summary>
    public int CandidatesDone { get; init; }

    /// <summary>Gets the candidates to read; 0 before discovery is done.</summary>
    public int CandidatesTotal { get; init; }

    /// <summary>Gets the candidates being read now, each with its current step, in the order their reads started.</summary>
    public IReadOnlyList<ScanProgress.CandidateStep> Working { get; init; } = [];
}
