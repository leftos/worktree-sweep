using WorktreeSweep.Discovery;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Report;

/// <summary>An orphan folder or link and its size.</summary>
public sealed record OrphanCandidate : Candidate
{
    /// <summary>Gets where it is and what it is.</summary>
    public required Orphan Orphan { get; init; }

    /// <summary>Gets the disk usage; a link measures as empty, its target never counted.</summary>
    public required SizeInfo Size { get; init; }

    /// <inheritdoc/>
    public override string Path => Orphan.Path;

    /// <inheritdoc/>
    public override long? SizeBytes => Size.Bytes;

    /// <inheritdoc/>
    protected override int Unreadable => Size.Unreadable;
}
