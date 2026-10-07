namespace WorktreeSweep.Report;

/// <summary>A folder the tool offers to remove: a <see cref="RegisteredCandidate"/> or an <see cref="OrphanCandidate"/>.</summary>
public abstract record Candidate
{
    private protected Candidate() { }

    /// <summary>Gets the candidate's folder or link.</summary>
    public abstract string Path { get; }

    /// <summary>Gets the bytes on disk; <see langword="null"/> when unknown (a prunable registration). A link counts as zero.</summary>
    public abstract long? SizeBytes { get; }

    /// <summary>Gets the entries the scan could not read, whose size <see cref="SizeBytes"/> leaves out.</summary>
    protected abstract int Unreadable { get; }

    /// <summary>Gets the size the scan read; <see langword="null"/> when <see cref="SizeBytes"/> is <see langword="null"/> or negative.</summary>
    public KnownSize? KnownSize => SizeBytes is { } bytes && bytes >= 0 ? new KnownSize(bytes, Unreadable > 0) : null;
}
