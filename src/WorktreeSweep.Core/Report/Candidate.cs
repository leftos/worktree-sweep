namespace WorktreeSweep.Report;

/// <summary>A folder the tool offers to remove: a <see cref="RegisteredCandidate"/> or an <see cref="OrphanCandidate"/>.</summary>
public abstract record Candidate
{
    private protected Candidate() { }

    /// <summary>Gets the candidate's folder or link.</summary>
    public abstract string Path { get; }

    /// <summary>Gets the bytes on disk; <see langword="null"/> when unknown (a prunable registration). A link counts as zero.</summary>
    public abstract long? SizeBytes { get; }
}
