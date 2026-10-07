using WorktreeSweep.Discovery;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Report;

/// <summary>A linked worktree some repo registers, and its signals.</summary>
public sealed record RegisteredCandidate : Candidate
{
    /// <summary>Gets the worktree's record: its folder, branch, head, git lock and prunable reason.</summary>
    public required WorktreeRecord Record { get; init; }

    /// <summary>Gets the main worktree of the repo that registers it.</summary>
    public required string Repo { get; init; }

    /// <summary>Gets the released marker a removal left when it could not remove the worktree; <see langword="null"/> when there is none.</summary>
    public Released? Released { get; init; }

    /// <summary>Gets the merge state, dirty counts, upstream, last activity and size.</summary>
    public required WorktreeSignals Signals { get; init; }

    /// <inheritdoc/>
    public override string Path => Record.Path;

    /// <inheritdoc/>
    public override long? SizeBytes => Signals.Size?.Bytes;
}
