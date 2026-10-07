using System.ComponentModel;
using System.Diagnostics;
using WorktreeSweep.Processes;

namespace WorktreeSweep.Unlock;

/// <summary>
/// The elevated side of the unlock flow: finds the processes holding files under the locked folders and offers, for each, to
/// stop it, close its handles there, skip it, or end the session. After acting it scans again and offers what is left, until
/// nothing holds files there or the user picks Done.
/// </summary>
/// <param name="input">Where the answers come from.</param>
/// <param name="output">Where the session prints, flushed before every read.</param>
/// <param name="handleExe">The <c>handle.exe</c> the session dumps and closes handles through.</param>
/// <param name="processes">The process table and the stop operation.</param>
/// <param name="handleExeCanName">Whether <c>handle.exe</c> can print a path at all; one it cannot is never reported clear.</param>
public sealed class ElevatedSession(
    TextReader input,
    TextWriter output,
    IHandleExe handleExe,
    IProcessControl processes,
    Func<string, bool> handleExeCanName
)
{
    /// <summary>How many of a locker's handles the description shows before it says how many it left out.</summary>
    private const int ShownHandles = 5;

    /// <summary>The actions the prompt offers, in the order it numbers them.</summary>
    private static readonly LockerAction[] Actions = [LockerAction.Stop, LockerAction.CloseHandles, LockerAction.Skip, LockerAction.Done];

    /// <summary>The caller verdict for each locker PID offered so far, so a PID is judged, and its warning written, once.</summary>
    private readonly Dictionary<int, bool> callerVerdicts = [];

    /// <summary>
    /// Runs the session, scanning, offering and acting until nothing holds the files or the user ends it. A path
    /// <c>handle.exe</c> cannot name is warned about before the first scan, and the session never reports clear while one is in
    /// play: nothing it can see holding the others is no proof about that path. Such a path ends the session at
    /// <see cref="UnlockExit.SomeLeft"/> even when nothing is found holding the others, so the sweep retries every locked pick.
    /// </summary>
    /// <param name="paths">The folders whose open handles are cleared.</param>
    /// <param name="callerPid">The process that started the unelevated run, usually the user's shell, whose stopping is not the
    /// default; <see langword="null"/> when it is not known.</param>
    /// <param name="callerStarted">That process's creation time, which proves a PID match is still it; <see langword="null"/>
    /// when it is not known.</param>
    /// <param name="sweepPid">The unelevated worktree-sweep, which with its children is never offered; <see langword="null"/>
    /// when it is not known.</param>
    /// <returns><see cref="UnlockExit.AllClear"/> when nothing holds the files and <c>handle.exe</c> can name every path;
    /// <see cref="UnlockExit.SomeLeft"/> when something still holds them after an action, or when a path could not be named at
    /// all; otherwise <see cref="UnlockExit.NothingDone"/>.</returns>
    /// <exception cref="UnlockException"><c>handle.exe</c> is missing or fails.</exception>
    /// <exception cref="Win32Exception">The process table cannot be read.</exception>
    public int Run(IReadOnlyList<string> paths, int? callerPid, ulong? callerStarted, int? sweepPid)
    {
        ArgumentNullException.ThrowIfNull(paths);
        IReadOnlyList<string> unnameable = [.. paths.Where(path => !handleExeCanName(path))];
        foreach (string path in unnameable)
        {
            Say(UnnameableLine(path));
        }
        IReadOnlySet<int> excluded = ProcessTable.ExcludedPids(Environment.ProcessId, sweepPid, processes.Snapshot(), processes.StartedAt);
        bool acted = false;
        bool finished = false;
        while (true)
        {
            Say("Scanning open handles…");
            IReadOnlyList<Locker> lockers = LockerFinder.Find(handleExe.Dump(), paths, excluded, processes.StartedAt);
            if (lockers.Count == 0)
            {
                if (unnameable.Count > 0)
                {
                    Say("Nothing handle.exe can see holds files under those folders.");
                    return UnlockExit.SomeLeft;
                }
                Say("Nothing holds files under those folders.");
                return UnlockExit.For(true, acted);
            }
            if (finished)
            {
                Say($"{lockers.Count} process(es) still hold files under those folders.");
                return UnlockExit.For(false, acted);
            }
            Round round = OfferRound(lockers, callerPid, callerStarted, paths);
            acted |= round.Acted;
            finished = round.Done || !round.Acted;
        }
    }

    /// <summary>Offers every locker in turn and applies the answer.</summary>
    /// <param name="lockers">The processes holding files, in the order they are offered.</param>
    /// <param name="callerPid">The PID of the process that started the unelevated run, or <see langword="null"/>.</param>
    /// <param name="callerStarted">That process's creation time, or <see langword="null"/> when it is not known.</param>
    /// <param name="paths">The folders whose open handles are cleared.</param>
    /// <returns>Whether anything was acted on, and whether the user ended the session.</returns>
    private Round OfferRound(IReadOnlyList<Locker> lockers, int? callerPid, ulong? callerStarted, IReadOnlyList<string> paths)
    {
        bool acted = false;
        foreach (Locker locker in lockers)
        {
            bool isCaller = IsCaller(locker, callerPid, callerStarted);
            string label = $"{locker.Process} (pid {locker.Pid})";
            Describe(locker, label, isCaller);
            LockerAction action = Choose(label, isCaller);
            if (action == LockerAction.Done)
            {
                Say("Done; leaving the rest alone.");
                return new Round(acted, true);
            }
            Outcome outcome = Act(locker, label, action, paths);
            acted |= outcome.Acted;
            Say(outcome.Line);
        }
        return new Round(acted, false);
    }

    /// <summary>
    /// Whether the locker is the caller, judged once per PID per session so a PID offered again in a later round reuses the
    /// verdict and its warning is written once.
    /// </summary>
    /// <param name="locker">The process the scan saw, with the creation time it read then.</param>
    /// <param name="callerPid">The PID of the process that started the unelevated run, or <see langword="null"/>.</param>
    /// <param name="callerStarted">That process's creation time, or <see langword="null"/> when it is not known.</param>
    /// <returns><see langword="true"/> when the locker is the process that started the unelevated run.</returns>
    private bool IsCaller(Locker locker, int? callerPid, ulong? callerStarted)
    {
        if (!callerVerdicts.TryGetValue(locker.Pid, out bool verdict))
        {
            verdict = JudgeCaller(locker, callerPid, callerStarted);
            callerVerdicts[locker.Pid] = verdict;
        }
        return verdict;
    }

    /// <summary>
    /// Judges one locker: the PIDs must match, and when the caller's creation time is known so must the locker's, as the scan
    /// read it. A known different time means the PID was reused by another process, and the locker is an ordinary one, with a
    /// warning; a time the scan could not read keeps it the caller, whose default of Skip is the safe one.
    /// </summary>
    /// <param name="locker">The process the scan saw, with the creation time it read then.</param>
    /// <param name="callerPid">The PID of the process that started the unelevated run, or <see langword="null"/>.</param>
    /// <param name="callerStarted">That process's creation time, or <see langword="null"/> when it is not known.</param>
    /// <returns><see langword="true"/> when the locker is the process that started the unelevated run.</returns>
    private static bool JudgeCaller(Locker locker, int? callerPid, ulong? callerStarted)
    {
        if (callerPid != locker.Pid)
        {
            return false;
        }
        if (callerStarted is not ulong expected)
        {
            return true;
        }
        if (locker.Started is ulong started && started != expected)
        {
            Trace.TraceWarning($"pid {locker.Pid} is no longer the process that started worktree-sweep");
            return false;
        }
        return true;
    }

    /// <summary>Prints what a locker holds: at most <see cref="ShownHandles"/> handle lines, and a note when the caller's
    /// stopping would close the shell.</summary>
    /// <param name="locker">The process and its handles.</param>
    /// <param name="label">The process, as <c>{name} (pid {pid})</c>.</param>
    /// <param name="isCaller">Whether it is the process that started the unelevated run.</param>
    private void Describe(Locker locker, string label, bool isCaller)
    {
        Say($"{label} holds {locker.Handles.Count} handle(s) there:");
        foreach (HeldHandle held in locker.Handles.Take(ShownHandles))
        {
            Say($"  {held.Kind, -8} {held.Name}");
        }
        if (locker.Handles.Count > ShownHandles)
        {
            Say($"  … and {locker.Handles.Count - ShownHandles} more");
        }
        if (isCaller)
        {
            Say("  This is the shell you started worktree-sweep from; stopping it closes that shell.");
        }
    }

    /// <summary>Asks what to do with one locker, offering its default first.</summary>
    /// <param name="label">The process, as <c>{name} (pid {pid})</c>.</param>
    /// <param name="isCaller">Whether it is the process that started the unelevated run.</param>
    /// <returns>The chosen action.</returns>
    private LockerAction Choose(string label, bool isCaller)
    {
        string stop = isCaller ? "Stop process (closes your shell)" : "Stop process";
        IReadOnlyList<string> labels = [stop, "Close its handles", "Skip", "Done"];
        int defaultChoice = Array.IndexOf(Actions, LockerActions.DefaultAction(isCaller)) + 1;
        Say($"What should happen to {label}?");
        int choice = LinePrompt.AskChoice(input, output, new ChoicePrompt(labels, defaultChoice, EofChoice: 4, FallbackChoice: 3));
        return Actions[choice - 1];
    }

    /// <summary>Applies one answer and returns the line reporting it.</summary>
    /// <param name="locker">The process the answer is about.</param>
    /// <param name="label">The process, as <c>{name} (pid {pid})</c>.</param>
    /// <param name="action">The chosen action.</param>
    /// <param name="paths">The folders whose open handles are cleared.</param>
    /// <returns>The line and whether anything was acted on.</returns>
    private Outcome Act(Locker locker, string label, LockerAction action, IReadOnlyList<string> paths) =>
        action switch
        {
            LockerAction.Stop => StopLocker(locker, label),
            LockerAction.CloseHandles => CloseHandles(locker, label, paths),
            _ => new Outcome($"Skipped {label}.", false),
        };

    /// <summary>Stops a locker unless its PID no longer names the same program.</summary>
    /// <param name="locker">The process to stop.</param>
    /// <param name="label">The process, as <c>{name} (pid {pid})</c>.</param>
    /// <returns>The line and whether it was stopped.</returns>
    private Outcome StopLocker(Locker locker, string label)
    {
        LockerState state = CheckLocker(locker);
        if (state != LockerState.Same)
        {
            return Skipped(label, state);
        }
        try
        {
            processes.Stop(locker.Pid, locker.Process, locker.Started);
            return new Outcome($"Stopped {label}.", true);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return new Outcome($"Could not stop {label}: {error.Message}", false);
        }
    }

    /// <summary>Warns about closing handles, asks (default no), then closes the locker's handles and reports how many.</summary>
    /// <param name="locker">The process whose handles are closed.</param>
    /// <param name="label">The process, as <c>{name} (pid {pid})</c>.</param>
    /// <param name="paths">The folders whose open handles are cleared.</param>
    /// <returns>The line and whether any handle was closed.</returns>
    private Outcome CloseHandles(Locker locker, string label, IReadOnlyList<string> paths)
    {
        Say("Closing handles behind a program's back can make it crash or lose data.");
        if (!LinePrompt.AskYesNo(input, output, $"Close {locker.Handles.Count} handle(s) of {label}?", defaultAnswer: false))
        {
            return new Outcome($"Left {label} alone.", false);
        }
        LockerState state = CheckLocker(locker);
        if (state != LockerState.Same)
        {
            return Skipped(label, state);
        }
        IReadOnlyList<HandleRow> now = HandleCsv.Parse(handleExe.DumpProcess(locker.Pid));
        int closed = 0;
        foreach (HeldHandle held in locker.Handles)
        {
            closed += CloseOne(held, locker.Pid, label, StillNamesALockedFile(now, locker.Pid, held, paths));
        }
        return new Outcome($"Closed {closed} of {locker.Handles.Count} handle(s) of {label}.", closed > 0);
    }

    /// <summary>
    /// Whether a fresh dump of the locker's handles still shows this handle naming a file under the locked folders: a handle
    /// value can be reused between the scan and the answer, so one that now names something else is never closed.
    /// </summary>
    /// <param name="now">The locker's handles as a fresh dump lists them.</param>
    /// <param name="pid">The process holding the handle.</param>
    /// <param name="held">The handle the scan saw.</param>
    /// <param name="paths">The folders whose open handles are cleared.</param>
    /// <returns><see langword="true"/> when the handle still names a locked file.</returns>
    private static bool StillNamesALockedFile(IReadOnlyList<HandleRow> now, int pid, HeldHandle held, IReadOnlyList<string> paths) =>
        now.Any(row => row.Pid == pid && row.Handle == held.Handle && LockedPaths.Matches(row.Name, paths));

    /// <summary>Closes one handle, tracing a failure as a warning.</summary>
    /// <param name="held">The handle to close.</param>
    /// <param name="pid">The process holding it.</param>
    /// <param name="label">The process, as <c>{name} (pid {pid})</c>.</param>
    /// <param name="stillNamesALockedFile">Whether a fresh dump still shows the handle naming a locked file.</param>
    /// <returns>1 when the handle was closed, otherwise 0.</returns>
    private int CloseOne(HeldHandle held, int pid, string label, bool stillNamesALockedFile)
    {
        if (!stillNamesALockedFile)
        {
            Trace.TraceWarning($"handle {held.Handle:X} of {label} no longer names a locked file; not closed");
            return 0;
        }
        try
        {
            if (handleExe.Close(held.Handle, pid, out string failure))
            {
                return 1;
            }
            Trace.TraceWarning($"handle.exe could not close {held.Kind} handle {held.Handle:X} ({held.Name}) of {label}: {failure.Trim()}");
        }
        catch (UnlockException error)
        {
            Trace.TraceWarning($"cannot close handle {held.Handle:X} of {label}: {error.Message}");
        }
        return 0;
    }

    /// <summary>
    /// What a fresh look at the locker's PID finds: the process the scan saw, one that is gone, another process that has taken
    /// the PID since, or one that cannot be confirmed to be either. A table that cannot be taken is traced and counts as gone; a
    /// creation time that was not read at the scan, or cannot be read now, counts as unconfirmed, so nothing is acted on behind a
    /// PID that may have been reused.
    /// </summary>
    /// <param name="locker">The process the scan saw.</param>
    /// <returns>The verdict.</returns>
    private LockerState CheckLocker(Locker locker)
    {
        try
        {
            if (!ProcessTable.IsSameProcess(locker.Pid, locker.Process, processes.Snapshot()))
            {
                return LockerState.Exited;
            }
        }
        catch (Win32Exception error)
        {
            Trace.TraceWarning($"cannot check that pid {locker.Pid} is still {locker.Process}: {error.Message}");
            return LockerState.Exited;
        }
        if (locker.Started is not ulong expected)
        {
            return LockerState.Unconfirmed;
        }
        return processes.StartedAt(locker.Pid) switch
        {
            null => LockerState.Unconfirmed,
            ulong started when started == expected => LockerState.Same,
            _ => LockerState.Replaced,
        };
    }

    /// <summary>The outcome for a locker the re-check would not act on: nothing was acted on.</summary>
    /// <param name="label">The process, as <c>{name} (pid {pid})</c>.</param>
    /// <param name="state">What the re-check found; never <see cref="LockerState.Same"/>.</param>
    /// <returns>The line and that nothing was acted on.</returns>
    private static Outcome Skipped(string label, LockerState state) =>
        new(
            state switch
            {
                LockerState.Replaced => ReplacedLine(label),
                LockerState.Unconfirmed => UnconfirmedLine(label),
                _ => ExitedLine(label),
            },
            false
        );

    /// <summary>The line reporting a process that is gone.</summary>
    /// <param name="label">The process, as <c>{name} (pid {pid})</c>.</param>
    /// <returns>The line.</returns>
    private static string ExitedLine(string label) => $"{label} has exited; skipped.";

    /// <summary>The line reporting a PID that another process has taken since the scan.</summary>
    /// <param name="label">The process, as <c>{name} (pid {pid})</c>.</param>
    /// <returns>The line.</returns>
    private static string ReplacedLine(string label) => $"{label} is no longer the process the scan saw; skipped.";

    /// <summary>The line reporting a PID that could not be confirmed as the process the scan saw.</summary>
    /// <param name="label">The process, as <c>{name} (pid {pid})</c>.</param>
    /// <returns>The line.</returns>
    private static string UnconfirmedLine(string label) => $"{label} could not be confirmed as the process the scan saw; skipped.";

    /// <summary>The warning for a path <c>handle.exe</c> cannot print, and what the user can do about it.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The line.</returns>
    private static string UnnameableLine(string path) =>
        $"handle.exe cannot name files under {path}: its path holds characters outside this machine's code pages; "
        + "close what holds it, then run the sweep again.";

    /// <summary>Writes one line and flushes it, so a user sees it before the next read.</summary>
    /// <param name="line">The line.</param>
    private void Say(string line)
    {
        output.WriteLine(line);
        output.Flush();
    }

    /// <summary>What a fresh look at a locker's PID found.</summary>
    private enum LockerState
    {
        /// <summary>The PID still names the process the scan saw.</summary>
        Same,

        /// <summary>The process is gone, or the process table could not be read.</summary>
        Exited,

        /// <summary>Another process has taken the PID since the scan.</summary>
        Replaced,

        /// <summary>The PID cannot be confirmed, at either reading, as the process the scan saw.</summary>
        Unconfirmed,
    }

    /// <summary>What one round of offers did.</summary>
    /// <param name="Acted">Whether anything was stopped or had its handles closed.</param>
    /// <param name="Done">Whether the user ended the session.</param>
    private readonly record struct Round(bool Acted, bool Done);

    /// <summary>What the answer to one locker's prompt did.</summary>
    /// <param name="Line">The summary line to print.</param>
    /// <param name="Acted">Whether the process was stopped or any of its handles were closed.</param>
    private readonly record struct Outcome(string Line, bool Acted);
}
