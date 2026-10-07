using WorktreeSweep.Holders;
using WorktreeSweep.Report;

namespace WorktreeSweep.Agent;

/// <summary>What an agent's removal did, as <see cref="RemoveReportJson"/> writes it.</summary>
public sealed record RemoveReport
{
    /// <summary>Gets what happened.</summary>
    public required RemoveStatus Status { get; init; }

    /// <summary>Gets why the worktree was released or refused; <see langword="null"/> when removed.</summary>
    public required Reason? Reason { get; init; }

    /// <summary>Gets the worktree: the resolved path, or the path given, made absolute, when it did not resolve.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the repo's main worktree; <see langword="null"/> when unknown.</summary>
    public required string? Repo { get; init; }

    /// <summary>Gets the worktree's branch; <see langword="null"/> when detached or unknown.</summary>
    public required string? Branch { get; init; }

    /// <summary>Gets a value indicating whether the branch was deleted.</summary>
    public required bool BranchDeleted { get; init; }

    /// <summary>Gets what removing the worktree would lose, for <see cref="ReasonKind.WouldLose"/>.</summary>
    public required string? Loss { get; init; }

    /// <summary>Gets where the caller should change its current folder to before retrying, for <see cref="ReasonKind.CallerHolds"/>.</summary>
    public required string? CdTo { get; init; }

    /// <summary>Gets the processes that hold something inside the worktree.</summary>
    public required IReadOnlyList<Holder> Holders { get; init; }

    /// <summary>Gets the processes that may hold the worktree but could not be fully inspected.</summary>
    public required IReadOnlyList<MayHold> MayHold { get; init; }

    /// <summary>Gets the build servers stopped for <see cref="AgentOptions.StopBuildServers"/>.</summary>
    public required IReadOnlyList<ProcessRef> Stopped { get; init; }

    /// <summary>Gets the released marker written into the worktree's admin dir; <see langword="null"/> when none was.</summary>
    public required Released? Released { get; init; }

    /// <summary>Gets the follow-ups, and anything that failed along the way without stopping the run.</summary>
    public required IReadOnlyList<string> Notes { get; init; }
}
