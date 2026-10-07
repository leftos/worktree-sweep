using System.ComponentModel;
using System.Globalization;
using System.Text;
using WorktreeSweep.Processes;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The elevated unlock session, driven through its seams with scripted dumps, tables and answers.</summary>
public sealed class ElevatedSessionTests
{
    private const string Locked = @"D:\a.wt\x";

    /// <summary>Nothing holds the files, so the session never asks and ends all clear.</summary>
    [Fact]
    public void NothingHeldExitsAllClear()
    {
        StringWriter output = Writer();
        ElevatedSession session = Session(
            new StringReader(""),
            output,
            new FakeHandleExe("No matching handles found.\n"),
            new ScriptedProcessControl(Running())
        );

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Contains("Scanning open handles…\nNothing holds files under those folders.\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>One locker, an empty answer takes the default Stop, and the next scan finds nothing, so the session ends all
    /// clear after stopping the process once.</summary>
    [Fact]
    public void StopClearsAndExitsAllClear()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeHandleExe(OneLocker(), ""), processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Equal<int[]>([642], [.. processes.Stopped]);
        Assert.Contains("Stopped pwsh.exe (pid 642).\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The caller is marked in the description and its prompt defaults to Skip, so an empty answer stops nothing.</summary>
    [Fact]
    public void CallerDefaultsToSkip()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeHandleExe(OneLocker(), OneLocker()), processes);

        int exit = session.Run([Locked], callerPid: 642, callerStarted: null, sweepPid: null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(processes.Stopped);
        Assert.Contains("  This is the shell you started worktree-sweep from; stopping it closes that shell.\n", text, StringComparison.Ordinal);
        Assert.Contains("1) Stop process (closes your shell)  2) Close its handles  3) Skip  4) Done [3]: ", text, StringComparison.Ordinal);
        Assert.Contains("Skipped pwsh.exe (pid 642).\n", text, StringComparison.Ordinal);
    }

    /// <summary>A locker with the caller's PID and its creation time is the caller, so its default is Skip.</summary>
    [Fact]
    public void CallerWithAMatchingStartTimeDefaultsToSkip()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeHandleExe(OneLocker(), OneLocker()), processes);

        int exit = session.Run([Locked], callerPid: 642, callerStarted: 1000, sweepPid: null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(processes.Stopped);
        Assert.Contains("  This is the shell you started worktree-sweep from; stopping it closes that shell.\n", text, StringComparison.Ordinal);
        Assert.Contains("Skipped pwsh.exe (pid 642).\n", text, StringComparison.Ordinal);
    }

    /// <summary>A PID that now names a newer process than the caller is an ordinary locker, so its default is Stop.</summary>
    [Fact]
    public void CallerPidReusedByANewerProcessIsAnOrdinaryLocker()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 2000;
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeHandleExe(OneLocker(), ""), processes);

        int exit = session.Run([Locked], callerPid: 642, callerStarted: 1000, sweepPid: null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Equal<int[]>([642], [.. processes.Stopped]);
        Assert.Contains("Stopped pwsh.exe (pid 642).\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("closes your shell", text, StringComparison.Ordinal);
    }

    /// <summary>A locker whose creation time cannot be read keeps counting as the caller, whose default is the safe one.</summary>
    [Fact]
    public void CallerWithAnUnreadableStartTimeKeepsItsDefault()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeHandleExe(OneLocker(), OneLocker()), processes);

        int exit = session.Run([Locked], callerPid: 642, callerStarted: 1000, sweepPid: null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(processes.Stopped);
        Assert.Contains("  This is the shell you started worktree-sweep from; stopping it closes that shell.\n", text, StringComparison.Ordinal);
        Assert.Contains("Skipped pwsh.exe (pid 642).\n", text, StringComparison.Ordinal);
    }

    /// <summary>A round that acted on nothing ends the session, and the scan that follows reports what still holds.</summary>
    [Fact]
    public void SkipEverythingExitsNothingDone()
    {
        StringWriter output = Writer();
        ElevatedSession session = Session(
            new StringReader("3\n"),
            output,
            new FakeHandleExe(OneLocker()),
            new ScriptedProcessControl(Running(("pwsh.exe", 642)))
        );

        int exit = session.Run([Locked], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Contains("Skipped pwsh.exe (pid 642).\n", text, StringComparison.Ordinal);
        Assert.Contains("1 process(es) still hold files under those folders.\n", text, StringComparison.Ordinal);
    }

    /// <summary>Done ends the round early, after the first locker was stopped, and the next scan reports the one that is left.</summary>
    [Fact]
    public void DoneStopsAskingAndExitsSomeLeftAfterAnAction()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("code.exe", 200), ("pwsh.exe", 642)));
        processes.StartedTimes[200] = 1000;
        ElevatedSession session = Session(
            new StringReader("\n4\n"),
            output,
            new FakeHandleExe(TwoLockers(), OneLockerFor("pwsh.exe", 642)),
            processes
        );

        int exit = session.Run([Locked], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.SomeLeft, exit);
        Assert.Equal<int[]>([200], [.. processes.Stopped]);
        Assert.Contains("Stopped code.exe (pid 200).\n", text, StringComparison.Ordinal);
        Assert.Contains("Done; leaving the rest alone.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopped pwsh.exe", text, StringComparison.Ordinal);
    }

    /// <summary>End of input is Done, which acts on nothing and so ends the session as nothing done.</summary>
    [Fact]
    public void EofMeansDone()
    {
        StringWriter output = Writer();
        ElevatedSession session = Session(
            new StringReader(""),
            output,
            new FakeHandleExe(OneLocker()),
            new ScriptedProcessControl(Running(("pwsh.exe", 642)))
        );

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Contains("Done; leaving the rest alone.\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A PID that is no longer the same process is not stopped, and is reported as exited.</summary>
    [Fact]
    public void ExitedProcessIsSkipped()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running());
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeHandleExe(OneLocker()), processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(processes.Stopped);
        Assert.Contains("pwsh.exe (pid 642) has exited; skipped.\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Close its handles warns first, then closes every handle and reports how many, ending all clear when nothing is
    /// left.</summary>
    [Fact]
    public void CloseHandlesAsksAndClosesEachHandle()
    {
        StringWriter output = Writer();
        var handleExe = new FakeHandleExe(TwoHandles(), "");
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("2\ny\n"), output, handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Equal<ulong[]>([0x1A0, 0x58], [.. handleExe.Closed.Select(entry => entry.Handle)]);
        Assert.Equal<int[]>([642, 642], [.. handleExe.Closed.Select(entry => entry.Pid)]);
        Assert.Contains("Closing handles behind a program's back can make it crash or lose data.\n", text, StringComparison.Ordinal);
        Assert.Contains("Closed 2 of 2 handle(s) of pwsh.exe (pid 642).\n", text, StringComparison.Ordinal);
    }

    /// <summary>Closing handles is not the default, and an empty answer leaves the process alone.</summary>
    [Fact]
    public void CloseHandlesAsksAndLeavesAloneByDefault()
    {
        StringWriter output = Writer();
        var handleExe = new FakeHandleExe(TwoHandles());
        ElevatedSession session = Session(new StringReader("2\n\n"), output, handleExe, new ScriptedProcessControl(Running(("pwsh.exe", 642))));

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(handleExe.Closed);
        Assert.Contains(
            "Close 2 handle(s) of pwsh.exe (pid 642)? [y/N] Left pwsh.exe (pid 642) alone.\n",
            output.ToString(),
            StringComparison.Ordinal
        );
    }

    /// <summary>The description shows five handle lines and says how many it left out.</summary>
    [Fact]
    public void ShowsAtMostFiveHandles()
    {
        StringWriter output = Writer();
        ElevatedSession session = Session(
            new StringReader("3\n"),
            output,
            new FakeHandleExe(SevenHandles()),
            new ScriptedProcessControl(Running(("pwsh.exe", 642)))
        );

        int exit = session.Run([Locked], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Contains("pwsh.exe (pid 642) holds 7 handle(s) there:\n", text, StringComparison.Ordinal);
        Assert.Contains("  File     D:\\a.wt\\x\\f1\n", text, StringComparison.Ordinal);
        Assert.Contains("  File     D:\\a.wt\\x\\f5\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"D:\a.wt\x\f6", text, StringComparison.Ordinal);
        Assert.Contains("  … and 2 more\n", text, StringComparison.Ordinal);
    }

    /// <summary>The unelevated worktree-sweep and its children are never offered.</summary>
    [Fact]
    public void ExcludedPidsAreNotOffered()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("code.exe", 800), ("worktree-sweep.exe", 900)));
        processes.StartedTimes[800] = 1000;
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeHandleExe(ExcludedAndKept(), ""), processes);

        int exit = session.Run([Locked], callerPid: null, callerStarted: null, sweepPid: 900);

        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Equal<int[]>([800], [.. processes.Stopped]);
        Assert.DoesNotContain("pid 900", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>An ancestor's PID that was reused is not excluded, so a locker that now holds it is still offered.</summary>
    [Fact]
    public void ReusedAncestorPidIsStillOffered()
    {
        StringWriter output = Writer();
        int own = Environment.ProcessId;
        var table = new Dictionary<int, ProcessEntry>
        {
            [own] = new(600, "worktree-sweep.exe"),
            [600] = new(500, "conhost.exe"),
            [500] = new(400, "sudo.exe"),
            [400] = new(1, "services.exe"),
        };
        var processes = new ScriptedProcessControl(table);
        processes.StartedTimes[own] = 100;
        processes.StartedTimes[600] = 60;
        processes.StartedTimes[500] = 200; // newer than its child 600: the PID no longer names the sudo.exe
        processes.StartedTimes[400] = 50;
        ElevatedSession session = Session(new StringReader("3\n"), output, new FakeHandleExe(OneLockerFor("code.exe", 500)), processes);

        int exit = session.Run([Locked], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Contains("code.exe (pid 500)", text, StringComparison.Ordinal);
    }

    /// <summary>A stop that fails is reported, nothing is acted on, and the session ends as nothing done.</summary>
    [Fact]
    public void StopFailureIsReportedAndNothingIsActed()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642))) { StopThrows = new Win32Exception("cannot open process 642") };
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeHandleExe(OneLocker()), processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(processes.Stopped);
        Assert.Contains("Could not stop pwsh.exe (pid 642): cannot open process 642\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A stop refused because the PID now belongs to another image is reported the same way.</summary>
    [Fact]
    public void StopOfAReusedPidIsReportedAndNothingIsActed()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)))
        {
            StopThrows = new InvalidOperationException("process 642 is now other.exe, not pwsh.exe; not stopped"),
        };
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeHandleExe(OneLocker()), processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(processes.Stopped);
        Assert.Contains(
            "Could not stop pwsh.exe (pid 642): process 642 is now other.exe, not pwsh.exe; not stopped\n",
            output.ToString(),
            StringComparison.Ordinal
        );
    }

    /// <summary>One of two handles refusing to close is counted as not closed, while the one that closed is still an act.</summary>
    [Fact]
    public void CloseFailureCountsAsNotClosed()
    {
        StringWriter output = Writer();
        var handleExe = new FakeHandleExe(TwoHandles(), "");
        handleExe.Failing.Add(0x58);
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("2\ny\n"), output, handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Equal<ulong[]>([0x1A0], [.. handleExe.Closed.Select(entry => entry.Handle)]);
        Assert.Contains("Closed 1 of 2 handle(s) of pwsh.exe (pid 642).\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A close that throws is traced and counted as not closed, so nothing was acted on.</summary>
    [Fact]
    public void CloseThrowIsCountedAsNotClosed()
    {
        StringWriter output = Writer();
        var handleExe = new FakeHandleExe(TwoHandles()) { CloseThrows = new UnlockException("cannot run handle.exe: it went missing") };
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("2\ny\n"), output, handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(handleExe.Closed);
        Assert.Contains("Closed 0 of 2 handle(s) of pwsh.exe (pid 642).\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A handle value that now names another file is not closed: the value may have been reused since the scan.</summary>
    [Fact]
    public void HandleThatNoLongerNamesALockedFileIsNotClosed()
    {
        StringWriter output = Writer();
        var handleExe = new FakeHandleExe(TwoHandles(), "")
        {
            ProcessDump = Dump(("pwsh.exe", 642, "File", 0x1A0, @"D:\elsewhere\a.txt"), ("pwsh.exe", 642, "File", 0x58, @"D:\a.wt\x\b.txt")),
        };
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("2\ny\n"), output, handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Equal<ulong[]>([0x58], [.. handleExe.Closed.Select(entry => entry.Handle)]);
        Assert.Contains("Closed 1 of 2 handle(s) of pwsh.exe (pid 642).\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A process table that cannot be read for the same-process check counts as exited, so nothing is stopped.</summary>
    [Fact]
    public void SnapshotFailureInTheSameProcessCheckCountsAsExited()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642))) { SnapshotThrowsOn = 2 };
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeHandleExe(OneLocker()), processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(processes.Stopped);
        Assert.Contains("pwsh.exe (pid 642) has exited; skipped.\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A PID taken by a newer process since the scan is not stopped, even under the same image name.</summary>
    [Fact]
    public void StopOfAPidTakenByANewerProcessIsSkipped()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimeSequence[642] = StartTimes(1000, 2000);
        ElevatedSession session = Session(new StringReader("1\n"), output, new FakeHandleExe(OneLocker()), processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(processes.Stopped);
        Assert.Contains("pwsh.exe (pid 642) is no longer the process the scan saw; skipped.\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A PID taken by a newer process since the scan has no handles closed.</summary>
    [Fact]
    public void CloseOfAPidTakenByANewerProcessIsSkipped()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimeSequence[642] = StartTimes(1000, 2000);
        var handleExe = new FakeHandleExe(OneLocker(), OneLocker());
        ElevatedSession session = Session(new StringReader("2\ny\n"), output, handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(handleExe.Closed);
        Assert.Contains("pwsh.exe (pid 642) is no longer the process the scan saw; skipped.\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The start time the scan saw is passed to the stop, so it needs no second check of its own.</summary>
    [Fact]
    public void StopPassesTheScanStartTime()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimeSequence[642] = StartTimes(1000);
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeHandleExe(OneLocker(), ""), processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.AllClear, exit);
        (int Pid, string Exe, ulong? Started) stop = Assert.Single(processes.Stops);
        Assert.Equal(642, stop.Pid);
        Assert.Equal("pwsh.exe", stop.Exe);
        Assert.Equal<ulong?>(1000, stop.Started);
    }

    /// <summary>A locker whose start time could not be read at the scan is not acted on: its PID cannot be confirmed.</summary>
    [Fact]
    public void LockerWithAnUnreadableScanTimeIsSkipped()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeHandleExe(OneLocker()), processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(processes.Stopped);
        Assert.Contains(
            "pwsh.exe (pid 642) could not be confirmed as the process the scan saw; skipped.\n",
            output.ToString(),
            StringComparison.Ordinal
        );
    }

    /// <summary>A locker whose start time can no longer be read at the re-check cannot be confirmed, so nothing is acted on.</summary>
    [Fact]
    public void LockerWhoseStartTimeBecomesUnreadableIsSkippedAsUnconfirmed()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimeSequence[642] = StartTimes(1000, null);
        ElevatedSession session = Session(new StringReader("1\n"), output, new FakeHandleExe(OneLocker()), processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(processes.Stopped);
        Assert.Contains(
            "pwsh.exe (pid 642) could not be confirmed as the process the scan saw; skipped.\n",
            output.ToString(),
            StringComparison.Ordinal
        );
    }

    /// <summary>A path <c>handle.exe</c> cannot name is warned about, and the session never reports it clear.</summary>
    [Fact]
    public void UnnameablePathIsWarnedAndNeverReportedClear()
    {
        StringWriter output = Writer();
        var session = new ElevatedSession(
            new StringReader(""),
            output,
            new FakeHandleExe("No matching handles found.\n"),
            new ScriptedProcessControl(Running()),
            path => path != Locked
        );

        int exit = session.Run([Locked], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.SomeLeft, exit);
        Assert.Contains(
            @"handle.exe cannot name files under D:\a.wt\x: its path holds characters outside this machine's code pages; close what holds it, then run the sweep again."
                + "\n",
            text,
            StringComparison.Ordinal
        );
        Assert.Contains("Nothing handle.exe can see holds files under those folders.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing holds files under those folders.", text, StringComparison.Ordinal);
    }

    /// <summary>A locker under a nameable path is still offered while another path cannot be named, and the stop that clears it
    /// leaves the session at some left, because the unnameable path was never seen.</summary>
    [Fact]
    public void UnnameablePathStillOffersLockersOfOtherPaths()
    {
        const string Other = @"D:\b.wt\y";
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        var session = new ElevatedSession(
            new StringReader("\n"),
            output,
            new FakeHandleExe(Dump(("pwsh.exe", 642, "File", 0x54, @"D:\b.wt\y\file.txt")), ""),
            processes,
            path => path == Other
        );

        int exit = session.Run([Locked, Other], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.SomeLeft, exit);
        Assert.Equal<int[]>([642], [.. processes.Stopped]);
        Assert.Contains("Stopped pwsh.exe (pid 642).\n", text, StringComparison.Ordinal);
        Assert.Contains("Nothing handle.exe can see holds files under those folders.\n", text, StringComparison.Ordinal);
    }

    /// <summary>A nameable path that is clear and one <c>handle.exe</c> cannot name still leave the session at some left, so the
    /// sweep retries every locked pick rather than believing the unnameable one is clear.</summary>
    [Fact]
    public void UnnameablePathWithANameableOneExitsSomeLeft()
    {
        const string Other = @"D:\b.wt\y";
        StringWriter output = Writer();
        var session = new ElevatedSession(
            new StringReader(""),
            output,
            new FakeHandleExe("No matching handles found.\n"),
            new ScriptedProcessControl(Running()),
            path => path == Other
        );

        int exit = session.Run([Locked, Other], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.SomeLeft, exit);
        Assert.Contains("Nothing handle.exe can see holds files under those folders.\n", text, StringComparison.Ordinal);
    }

    /// <summary>The start times <see cref="ScriptedProcessControl.StartedAt"/> answers with, in turn, the last repeating.</summary>
    /// <param name="times">The times, in the order they are read.</param>
    /// <returns>The queue.</returns>
    private static Queue<ulong?> StartTimes(params ulong?[] times) => new(times);

    /// <summary>An elevated session whose <c>handle.exe</c> can name every path, driven through the given seams.</summary>
    /// <param name="input">Where the answers come from.</param>
    /// <param name="output">Where the session prints.</param>
    /// <param name="handleExe">The <c>handle.exe</c> the session dumps and closes handles through.</param>
    /// <param name="processes">The process table and the stop operation.</param>
    /// <returns>The session.</returns>
    private static ElevatedSession Session(TextReader input, TextWriter output, IHandleExe handleExe, IProcessControl processes) =>
        new(input, output, handleExe, processes, _ => true);

    /// <summary>A <c>StringWriter</c> that ends its lines with <c>\n</c>.</summary>
    private static StringWriter Writer() => new() { NewLine = "\n" };

    /// <summary>A process table holding the given PID and image name pairs, each with parent 1.</summary>
    private static Dictionary<int, ProcessEntry> Running(params (string Exe, int Pid)[] processes)
    {
        var table = new Dictionary<int, ProcessEntry>();
        foreach ((string Exe, int Pid) process in processes)
        {
            table[process.Pid] = new ProcessEntry(1, process.Exe);
        }
        return table;
    }

    /// <summary>A <c>handle.exe -nobanner -v</c> dump holding the given rows.</summary>
    private static string Dump(params (string Process, int Pid, string Kind, ulong Handle, string Name)[] rows)
    {
        var text = new StringBuilder("Process,PID,User,Handle,Type,Share Flags,Name\n");
        foreach ((string Process, int Pid, string Kind, ulong Handle, string Name) row in rows)
        {
            text.Append(CultureInfo.InvariantCulture, $"{row.Process},{row.Pid},EXAMPLE-PC\\user,0x{row.Handle:X8},{row.Kind},,{row.Name}\n");
        }
        return text.ToString();
    }

    private static string OneLocker() => Dump(("pwsh.exe", 642, "File", 0x54, @"D:\a.wt\x\file.txt"));

    private static string OneLockerFor(string process, int pid) => Dump((process, pid, "File", 0x54, @"D:\a.wt\x\file.txt"));

    private static string TwoLockers() =>
        Dump(("code.exe", 200, "File", 0x10, @"D:\a.wt\x\a.txt"), ("pwsh.exe", 642, "File", 0x54, @"D:\a.wt\x\file.txt"));

    private static string TwoHandles() =>
        Dump(("pwsh.exe", 642, "File", 0x1A0, @"D:\a.wt\x\a.txt"), ("pwsh.exe", 642, "File", 0x58, @"D:\a.wt\x\b.txt"));

    private static string ExcludedAndKept() =>
        Dump(("worktree-sweep.exe", 900, "File", 0x20, @"D:\a.wt\x\held.txt"), ("code.exe", 800, "File", 0x30, @"D:\a.wt\x\other.txt"));

    private static string SevenHandles() =>
        Dump(
            ("pwsh.exe", 642, "File", 0x101, @"D:\a.wt\x\f1"),
            ("pwsh.exe", 642, "File", 0x102, @"D:\a.wt\x\f2"),
            ("pwsh.exe", 642, "File", 0x103, @"D:\a.wt\x\f3"),
            ("pwsh.exe", 642, "File", 0x104, @"D:\a.wt\x\f4"),
            ("pwsh.exe", 642, "File", 0x105, @"D:\a.wt\x\f5"),
            ("pwsh.exe", 642, "File", 0x106, @"D:\a.wt\x\f6"),
            ("pwsh.exe", 642, "File", 0x107, @"D:\a.wt\x\f7")
        );

    /// <summary>A handle.exe that returns its scripted dumps in order, repeats the last, and records what it closed.</summary>
    private sealed class FakeHandleExe(params string[] dumps) : IHandleExe
    {
        private readonly Queue<string> pending = new(dumps);
        private readonly List<(ulong Handle, int Pid)> closed = [];
        private string last = "";

        /// <summary>Gets the handles closed, with the process each belonged to.</summary>
        public IReadOnlyList<(ulong Handle, int Pid)> Closed => closed;

        /// <summary>Gets or sets the dump <see cref="DumpProcess"/> answers with, or <see langword="null"/> to answer with the
        /// last <see cref="Dump"/>.</summary>
        public string? ProcessDump { get; set; }

        /// <summary>Gets the handles whose closing reports failure.</summary>
        public HashSet<ulong> Failing { get; } = [];

        /// <summary>Gets or sets the failure every closing throws, or <see langword="null"/> for none.</summary>
        public UnlockException? CloseThrows { get; set; }

        public string Dump()
        {
            last = pending.Count > 1 ? pending.Dequeue() : pending.Peek();
            return last;
        }

        public string DumpProcess(int pid) => ProcessDump ?? last;

        public bool Close(ulong handle, int pid, out string output)
        {
            if (CloseThrows is UnlockException failure)
            {
                throw failure;
            }
            if (Failing.Contains(handle))
            {
                output = "Access is denied.";
                return false;
            }
            closed.Add((handle, pid));
            output = "";
            return true;
        }
    }

    /// <summary>A process control with a scripted table, a scripted start-time reader, a record of the stops and scripted
    /// failures.</summary>
    private sealed class ScriptedProcessControl(IReadOnlyDictionary<int, ProcessEntry> table) : IProcessControl
    {
        private readonly List<(int Pid, string Exe, ulong? Started)> stops = [];
        private int snapshots;

        /// <summary>Gets the PIDs stopped, in the order they were stopped.</summary>
        public IReadOnlyList<int> Stopped => [.. stops.Select(stop => stop.Pid)];

        /// <summary>Gets the stops made, with the image name and the start time each was given.</summary>
        public IReadOnlyList<(int Pid, string Exe, ulong? Started)> Stops => stops;

        /// <summary>Gets or sets the failure <see cref="Stop"/> throws, or <see langword="null"/> for none.</summary>
        public Exception? StopThrows { get; set; }

        /// <summary>Gets or sets the 1-based snapshot call that throws, or 0 so that none does.</summary>
        public int SnapshotThrowsOn { get; set; }

        /// <summary>Gets the creation times <see cref="StartedAt"/> answers with, so that a PID it does not name reads as
        /// unknown.</summary>
        public Dictionary<int, ulong?> StartedTimes { get; } = [];

        /// <summary>Gets the creation times <see cref="StartedAt"/> answers with in turn per PID, the last value repeating.</summary>
        public Dictionary<int, Queue<ulong?>> StartedTimeSequence { get; } = [];

        public IReadOnlyDictionary<int, ProcessEntry> Snapshot()
        {
            snapshots++;
            if (snapshots == SnapshotThrowsOn)
            {
                throw new Win32Exception("cannot take a snapshot of the running processes");
            }
            return table;
        }

        public ulong? StartedAt(int pid)
        {
            if (StartedTimeSequence.TryGetValue(pid, out Queue<ulong?>? times) && times.Count > 0)
            {
                return times.Count > 1 ? times.Dequeue() : times.Peek();
            }
            return StartedTimes.TryGetValue(pid, out ulong? started) ? started : null;
        }

        public void Stop(int pid, string exe, ulong? started)
        {
            if (StopThrows is Exception failure)
            {
                throw failure;
            }
            stops.Add((pid, exe, started));
        }
    }
}
