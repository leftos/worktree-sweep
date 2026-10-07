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

    /// <summary>
    /// Gets the worktree folder, spelled as a drive path when git recorded it through a loopback admin share of this machine
    /// (<c>\\localhost\X$\dev\x</c> is <c>X:\dev\x</c>), so the display, the removal and the holder search name the local folder.
    /// <see cref="WorktreeRecord.Path"/> keeps git's own spelling, which a git argument naming the worktree must use.
    /// </summary>
    public override string Path => LoopbackShare.ToLocalDrive(Record.Path, LoopbackShare.LocalHosts()) ?? Record.Path;

    /// <inheritdoc/>
    public override long? SizeBytes => Signals.Size?.Bytes;

    /// <inheritdoc/>
    protected override int Unreadable => Signals.Size?.Unreadable ?? 0;
}
