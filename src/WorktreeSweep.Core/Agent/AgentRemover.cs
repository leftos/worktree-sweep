using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using WorktreeSweep.Discovery;
using WorktreeSweep.Git;
using WorktreeSweep.Holders;
using WorktreeSweep.Recycle;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Review;

namespace WorktreeSweep.Agent;

/// <summary>
/// Removes one worktree an agent asks for without asking, or reports what holds it and marks it released for the next interactive
/// sweep.
/// </summary>
/// <remarks>
/// The order is fixed: resolve the path, make the main worktree the current folder, prune a registration whose folder is gone,
/// refuse lost work, refuse when the caller's own process chain holds the folder, check the Recycle Bin can take it, recycle it on a
/// thread with a timeout, and on a lock find the holders, stop the allowlisted ones when asked, and retry once. A worktree that is
/// still there is marked released.
/// </remarks>
public static class AgentRemover
{
    private enum Recycled
    {
        Done,
        Locked,
        TimedOut,
    }

    /// <summary>
    /// Removes the worktree at <paramref name="path"/>, or reports why not. The process's current folder becomes the worktree's main
    /// worktree once the path resolves.
    /// </summary>
    /// <param name="path">The worktree; a relative path is made absolute against the current folder.</param>
    /// <param name="options">The flags.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or not a valid path.</exception>
    /// <exception cref="GitException">
    /// Git fails resolving the path, or a git lock cannot be lifted; a <see cref="GitTimeoutException"/> when a git read times out.
    /// </exception>
    /// <exception cref="IOException">
    /// The path's attributes cannot be read, the current folder cannot be changed, the caller's holders cannot be listed, or the
    /// recycle fails for a reason other than a lock.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The path's attributes cannot be read or the current folder changed for lack of access.
    /// </exception>
    /// <exception cref="Win32Exception">The process list cannot be read for the caller's holders.</exception>
    public static RemoveReport Run(string path, AgentOptions options) => Run(path, options, AgentSeams.Production);

    /// <summary>
    /// Removes the worktree at <paramref name="path"/>, as <see cref="Run(string, AgentOptions)"/> does, through <paramref name="seams"/>.
    /// </summary>
    /// <param name="path">The worktree.</param>
    /// <param name="options">The flags.</param>
    /// <param name="seams">What the removal reaches outside the process through.</param>
    /// <returns>The report.</returns>
    internal static RemoveReport Run(string path, AgentOptions options, AgentSeams seams)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(seams);
        Resolution resolution = PathResolution.ResolveOne(path);
        if (resolution is not Resolution.Resolved resolved)
        {
            var refusal = (Resolution.Refusal)resolution;
            var refused = new Draft(Path.GetFullPath(path), refusal.Repo, null);
            refused.Refuse(Reason.NotRemovable(refusal.Reason));
            return refused.ToReport();
        }
        seams.EnterMainWorktree(resolved.MainWorktree);
        RegisteredCandidate candidate = resolved.Candidate;
        var draft = new Draft(candidate.Path, candidate.Repo, candidate.Record.Branch);
        if (candidate.Record.Prunable is not null)
        {
            RemovePrunable(candidate, options, draft);
        }
        else if (!options.Force && WouldLose.Text(candidate) is { } loss)
        {
            draft.Refuse(Reason.WouldLose);
            draft.Loss = loss;
        }
        else if (CallerScan(candidate.Path, seams) is { } scan)
        {
            draft.Refuse(Reason.CallerHolds);
            draft.CdTo = resolved.MainWorktree;
            draft.TakeScan(scan);
        }
        else
        {
            RemoveFolder(candidate, options, seams, draft);
        }
        return draft.ToReport();
    }

    /// <summary>A prunable record: prune it and deal with its branch. A git-locked one needs <c>--force</c>, since prune skips it.</summary>
    private static void RemovePrunable(RegisteredCandidate candidate, AgentOptions options, Draft draft)
    {
        if (candidate.Record.Locked is not null)
        {
            if (!options.Force)
            {
                draft.Refuse(Reason.WouldLose);
                draft.Loss = LossText.Sentence(candidate, LossRoot(candidate))?.Context;
                return;
            }
            CandidateRemover.GitUnlock(candidate);
        }
        FinishRemoved(candidate, draft);
    }

    /// <summary>The folder a loss sentence names the worktree relative to: the repo's parent.</summary>
    private static string LossRoot(RegisteredCandidate candidate) => Path.GetDirectoryName(candidate.Repo) ?? candidate.Repo;

    /// <summary>The holders scan, when the caller's own process chain holds the folder; <see langword="null"/> when it does not.</summary>
    private static HolderReport? CallerScan(string folder, AgentSeams seams)
    {
        IReadOnlySet<int> chain = seams.OwnChain();
        HolderReport scan = seams.FindHolders(folder, [Environment.ProcessId]);
        return CallerHolds.Check(chain, scan) ? scan : null;
    }

    /// <summary>Capacity, recycle, and on a lock the holders, a retry and the release; the follow-ups on success.</summary>
    private static void RemoveFolder(RegisteredCandidate candidate, AgentOptions options, AgentSeams seams, Draft draft)
    {
        if (TooBig(candidate, seams) is { } why)
        {
            draft.Notes.Add($"cannot go to the Recycle Bin: {why}");
            Release(draft, candidate, Reason.TooBigForRecycleBin);
            return;
        }
        if (options.Force)
        {
            CandidateRemover.GitUnlock(candidate);
        }
        switch (RecycleWithTimeout(candidate.Path, seams))
        {
            case Recycled.TimedOut:
                Release(draft, candidate, Reason.ShellTimeout);
                RestoreGitLock(candidate, options, draft);
                return;
            case Recycled.Locked when !RetryLocked(candidate, options, seams, draft):
                RestoreGitLock(candidate, options, draft);
                return;
            default:
                break;
        }
        FinishRemoved(candidate, draft);
    }

    /// <summary>Why the folder cannot go to the Recycle Bin; <see langword="null"/> when it fits. An unknown size never fits.</summary>
    private static string? TooBig(RegisteredCandidate candidate, AgentSeams seams)
    {
        ulong size = candidate.Signals.Size is { } known ? (ulong)Math.Max(known.Bytes, 0) : ulong.MaxValue;
        return RecycleDecider.Decide(size, seams.ReadCapacity(candidate.Path)) is RecycleDecision.AskPermanent ask ? ask.Reason : null;
    }

    /// <summary>Puts back the git lock <c>--force</c> lifted, on a worktree that stays; git failing is a note.</summary>
    private static void RestoreGitLock(RegisteredCandidate candidate, AgentOptions options, Draft draft)
    {
        if (!options.Force)
        {
            return;
        }
        try
        {
            CandidateRemover.GitRelock(candidate);
        }
        catch (GitException error)
        {
            draft.Notes.Add($"git lock not restored: {error.Message}");
        }
    }

    /// <summary>
    /// Recycles <paramref name="path"/> on a dedicated thread, waiting at most <see cref="AgentSeams.RecycleTimeout"/>; a thread still
    /// stuck in the Shell is abandoned. A failure other than a lock is rethrown here.
    /// </summary>
    private static Recycled RecycleWithTimeout(string path, AgentSeams seams)
    {
        ExceptionDispatchInfo? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                seams.Recycle(path);
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
        })
        {
            IsBackground = true,
            Name = "worktree-sweep agent recycle",
        };
        worker.Start();
        if (!worker.Join(seams.RecycleTimeout))
        {
            Trace.TraceWarning(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"moving {path} to the Recycle Bin did not finish within {seams.RecycleTimeout.TotalSeconds} s"
                )
            );
            return Recycled.TimedOut;
        }
        if (failure?.SourceException is LockedException)
        {
            return Recycled.Locked;
        }
        failure?.Throw();
        return Recycled.Done;
    }

    /// <summary>
    /// On a lock: lists the holders, stops the allowlisted ones when asked, and retries once. Returns whether the folder is gone; when
    /// it is not, the worktree is released.
    /// </summary>
    private static bool RetryLocked(RegisteredCandidate candidate, AgentOptions options, AgentSeams seams, Draft draft)
    {
        HolderReport scan;
        try
        {
            scan = seams.FindHolders(candidate.Path, []);
        }
        catch (Exception error)
            when (error is IOException or Win32Exception or PlatformNotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            draft.Notes.Add($"cannot list the processes holding it: {error.Message}");
            scan = new HolderReport([], []);
        }
        if (options.StopBuildServers)
        {
            StopBuildServers(scan, seams, draft);
        }
        Reason reason = LockedReason.For(scan);
        draft.TakeScan(scan);
        switch (RecycleWithTimeout(candidate.Path, seams))
        {
            case Recycled.Done:
                return true;
            case Recycled.Locked:
                Release(draft, candidate, reason);
                return false;
            default:
                Release(draft, candidate, Reason.ShellTimeout);
                return false;
        }
    }

    /// <summary>Stops each allowlisted holder; one no longer the same process, or one that cannot be stopped, is a note.</summary>
    private static void StopBuildServers(HolderReport scan, AgentSeams seams, Draft draft)
    {
        foreach (Holder holder in StopAllowlist.Stoppable(scan))
        {
            if (!seams.StillSame(holder))
            {
                draft.Notes.Add($"pid {holder.Pid} ({holder.Exe}) not stopped: it is no longer the same process");
                continue;
            }
            try
            {
                seams.Stop(holder);
                draft.Stopped.Add(new ProcessRef { Pid = holder.Pid, Exe = holder.Exe });
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                draft.Notes.Add($"pid {holder.Pid} ({holder.Exe}) not stopped: {error.Message}");
            }
        }
    }

    /// <summary>Marks the report released and writes the marker; a marker that cannot be written is a note.</summary>
    private static void Release(Draft draft, RegisteredCandidate candidate, Reason reason)
    {
        draft.Status = RemoveStatus.Released;
        draft.Reason = reason;
        var released = new Released
        {
            ReleasedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Reason = reason,
            Holders =
            [
                .. draft.Holders.Select(holder => new ProcessRef { Pid = holder.Pid, Exe = holder.Exe }),
                .. draft.MayHold.Select(may => new ProcessRef { Pid = may.Pid, Exe = may.Exe }),
            ],
        };
        if (Discoverer.ReadGitdirFile(candidate.Path) is not { } admin)
        {
            draft.Notes.Add($"cannot mark it released: {candidate.Path} has no .git file naming its admin folder");
            return;
        }
        try
        {
            ReleasedMarker.Write(admin, released);
            draft.Released = released;
        }
        catch (IOException error)
        {
            draft.Notes.Add($"cannot mark it released: {error.Message}");
        }
    }

    /// <summary>Prunes the registration and deals with the branch; each failure is a note.</summary>
    private static void FinishRemoved(RegisteredCandidate candidate, Draft draft)
    {
        draft.Status = RemoveStatus.Removed;
        draft.Reason = null;
        try
        {
            CandidateRemover.PruneRepo(candidate.Repo);
        }
        catch (GitException error)
        {
            draft.Notes.Add($"prune failed: {error.Message}");
        }
        switch (AgentBranch.For(candidate))
        {
            case AgentBranch.Delete delete:
                DeleteBranch(candidate.Repo, delete.Branch, draft);
                break;
            case AgentBranch.Keep { Note: { } note }:
                draft.Notes.Add(note);
                break;
            case AgentBranch.Keep:
                break;
            default:
                throw new UnreachableException("unknown branch follow-up");
        }
    }

    private static void DeleteBranch(string repo, string branch, Draft draft)
    {
        try
        {
            FollowUps.DeleteBranch(repo, branch, force: false);
            draft.BranchDeleted = true;
        }
        catch (GitException error)
        {
            draft.Notes.Add($"branch {branch} kept: {error.Message}");
        }
    }

    /// <summary>The report as the run builds it.</summary>
    private sealed class Draft(string path, string? repo, string? branch)
    {
        public RemoveStatus Status { get; set; } = RemoveStatus.Removed;

        public Reason? Reason { get; set; }

        public bool BranchDeleted { get; set; }

        public string? Loss { get; set; }

        public string? CdTo { get; set; }

        public IReadOnlyList<Holder> Holders { get; private set; } = [];

        public IReadOnlyList<MayHold> MayHold { get; private set; } = [];

        public List<ProcessRef> Stopped { get; } = [];

        public Released? Released { get; set; }

        public List<string> Notes { get; } = [];

        public void Refuse(Reason reason)
        {
            Status = RemoveStatus.Refused;
            Reason = reason;
        }

        public void TakeScan(HolderReport scan)
        {
            Holders = scan.Holders;
            MayHold = scan.MayHold;
        }

        public RemoveReport ToReport() =>
            new()
            {
                Status = Status,
                Reason = Reason,
                Path = path,
                Repo = repo,
                Branch = branch,
                BranchDeleted = BranchDeleted,
                Loss = Loss,
                CdTo = CdTo,
                Holders = Holders,
                MayHold = MayHold,
                Stopped = [.. Stopped],
                Released = Released,
                Notes = [.. Notes],
            };
    }
}
