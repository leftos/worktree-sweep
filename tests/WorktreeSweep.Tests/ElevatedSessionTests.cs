using System.ComponentModel;
using WorktreeSweep.Processes;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The elevated unlock session, driven through its seams with scripted scans, tables and answers.</summary>
public sealed class ElevatedSessionTests : IDisposable
{
    private const string Locked = @"D:\a.wt\x";

    private readonly Fixture fx = new();

    /// <summary>Nothing holds the files, so the session never asks and ends all clear.</summary>
    [Fact]
    public void NothingHeldExitsAllClear()
    {
        StringWriter output = Writer();
        ElevatedSession session = Session(
            new StringReader(""),
            output,
            new FakeScan(Found()),
            new FakeHandleExe(),
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
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeScan(Found(Pwsh(1000)), Found()), new FakeHandleExe(), processes);

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
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeScan(Found(Pwsh(null))), new FakeHandleExe(), processes);

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
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeScan(Found(Pwsh(1000))), new FakeHandleExe(), processes);

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
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeScan(Found(Pwsh(2000)), Found()), new FakeHandleExe(), processes);

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
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeScan(Found(Pwsh(null))), new FakeHandleExe(), processes);

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
            new FakeScan(Found(Pwsh(null))),
            new FakeHandleExe(),
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
        var scan = new FakeScan(Found(LockerOf("code.exe", 200, 1000), Pwsh(null)), Found(Pwsh(null)));
        ElevatedSession session = Session(new StringReader("\n4\n"), output, scan, new FakeHandleExe(), processes);

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
            new FakeScan(Found(Pwsh(null))),
            new FakeHandleExe(),
            new ScriptedProcessControl(Running(("pwsh.exe", 642)))
        );

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Contains("Done; leaving the rest alone.\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A session that finishes with lockers left, by skipping them all, by Done or at end of input, while a process uses a folder
    /// unread or a folder cannot be read, ends with something left: what holds the folders is not known, so it is never nothing done.
    /// </summary>
    [Theory]
    [InlineData("3\n", false)]
    [InlineData("3\n", true)]
    [InlineData("4\n", false)]
    [InlineData("", true)]
    public void LockersLeftWithSomethingUnknownExitSomeLeft(string answers, bool unreachable)
    {
        StringWriter output = Writer();
        LockerFindings found = unreachable ? new([Pwsh(null)], [], [Locked]) : new([Pwsh(null)], [new UnreadableUser("code.exe", 300, Locked)], []);
        ElevatedSession session = Session(
            new StringReader(answers),
            output,
            new FakeScan(found),
            new FakeHandleExe(),
            new ScriptedProcessControl(Running(("pwsh.exe", 642)))
        );

        int exit = session.Run([Locked], null, null, null);

        string text = output.ToString();
        string unknownLine = unreachable
            ? $"{Locked} cannot be read here, so it is not known what holds it.\n"
            : $"code.exe (300) uses {Locked}, but its handles cannot be read.\n";
        Assert.Equal(UnlockExit.SomeLeft, exit);
        Assert.Contains(unknownLine, text, StringComparison.Ordinal);
        Assert.Contains("1 process(es) still hold files under those folders.\n", text, StringComparison.Ordinal);
    }

    /// <summary>A PID that is no longer the same process is not stopped, and is reported as exited.</summary>
    [Fact]
    public void ExitedProcessIsSkipped()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running());
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeScan(Found(Pwsh(1000))), new FakeHandleExe(), processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(processes.Stopped);
        Assert.Contains("pwsh.exe (pid 642) has exited; skipped.\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Close its handles warns first, hands the locker with its PID and scan start time to one re-checking pass, which closes
    /// every handle, and reports how many, ending all clear when nothing is left.</summary>
    [Fact]
    public void CloseHandlesAsksAndClosesEachHandle()
    {
        StringWriter output = Writer();
        var scan = new FakeScan(Found(TwoHandles(1000)), Found());
        var handleExe = new FakeHandleExe();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("2\ny\n"), output, scan, handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Equal<nint[]>([0x1A0, 0x58], [.. handleExe.Closed.Select(entry => entry.Handle)]);
        Assert.Equal<int[]>([642, 642], [.. handleExe.Closed.Select(entry => entry.Pid)]);
        CloseCall call = Assert.Single(scan.CloseCalls);
        Assert.Equal((642, (ulong?)1000), (call.Pid, call.Started));
        Assert.Contains("Closing handles behind a program's back can make it crash or lose data.\n", text, StringComparison.Ordinal);
        Assert.Contains("Closed 2 of 2 handle(s) of pwsh.exe (pid 642).\n", text, StringComparison.Ordinal);
    }

    /// <summary>Closing handles is not the default, and an empty answer leaves the process alone.</summary>
    [Fact]
    public void CloseHandlesAsksAndLeavesAloneByDefault()
    {
        StringWriter output = Writer();
        var handleExe = new FakeHandleExe();
        ElevatedSession session = Session(
            new StringReader("2\n\n"),
            output,
            new FakeScan(Found(TwoHandles(null))),
            handleExe,
            new ScriptedProcessControl(Running(("pwsh.exe", 642)))
        );

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(handleExe.Closed);
        Assert.Contains(
            "Close 2 handle(s) of pwsh.exe (pid 642)? [y/N] Left pwsh.exe (pid 642) alone.\n",
            output.ToString(),
            StringComparison.Ordinal
        );
    }

    /// <summary>The description shows five handle lines, each the file's name alone, and says how many it left out.</summary>
    [Fact]
    public void ShowsAtMostFiveHandles()
    {
        StringWriter output = Writer();
        ElevatedSession session = Session(
            new StringReader("3\n"),
            output,
            new FakeScan(Found(SevenHandles())),
            new FakeHandleExe(),
            new ScriptedProcessControl(Running(("pwsh.exe", 642)))
        );

        int exit = session.Run([Locked], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Contains("pwsh.exe (pid 642) holds 7 handle(s) there:\n", text, StringComparison.Ordinal);
        Assert.Contains("  D:\\a.wt\\x\\f1\n", text, StringComparison.Ordinal);
        Assert.Contains("  D:\\a.wt\\x\\f5\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"D:\a.wt\x\f6", text, StringComparison.Ordinal);
        Assert.Contains("  … and 2 more\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scan is given every running process but the idle and System processes, this one and the unelevated worktree-sweep with
    /// its children, so those are never opened or offered; one of them the file system reports using the folder is not reported.
    /// </summary>
    [Fact]
    public void ExcludedPidsAreNotOffered()
    {
        StringWriter output = Writer();
        int own = Environment.ProcessId;
        Dictionary<int, ProcessEntry> table = Running(
            ("Idle", 0),
            ("System", 4),
            ("code.exe", 800),
            ("worktree-sweep.exe", 900),
            ("conhost.exe", 901)
        );
        table[901] = new ProcessEntry(900, "conhost.exe");
        table[own] = new ProcessEntry(1, "worktree-sweep.exe");
        var processes = new ScriptedProcessControl(table);
        processes.StartedTimes[800] = 1000;
        processes.StartedTimes[900] = 10;
        processes.StartedTimes[901] = 20;
        var unreadable = new UnreadableUser("worktree-sweep.exe", 900, Locked);
        var scan = new FakeScan(new LockerFindings([LockerOf("code.exe", 800, 1000)], [unreadable], []), Found());
        ElevatedSession session = Session(new StringReader("\n"), output, scan, new FakeHandleExe(), processes);

        int exit = session.Run([Locked], callerPid: null, callerStarted: null, sweepPid: 900);

        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Equal<int[]>([800], [.. processes.Stopped]);
        Assert.Equal<int[]>([800], [.. scan.Scanned[0]]);
        Assert.DoesNotContain("900", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>An ancestor's PID that was reused is not excluded, so it is scanned and a locker that now holds it is still offered.</summary>
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
        var scan = new FakeScan(Found(LockerOf("code.exe", 500, 200)));
        ElevatedSession session = Session(new StringReader("3\n"), output, scan, new FakeHandleExe(), processes);

        int exit = session.Run([Locked], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Contains(500, scan.Scanned[0]);
        Assert.Contains("code.exe (pid 500)", text, StringComparison.Ordinal);
    }

    /// <summary>A stop that fails is reported, nothing is acted on, and the session ends as nothing done.</summary>
    [Fact]
    public void StopFailureIsReportedAndNothingIsActed()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642))) { StopThrows = new Win32Exception("cannot open process 642") };
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeScan(Found(Pwsh(1000))), new FakeHandleExe(), processes);

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
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeScan(Found(Pwsh(1000))), new FakeHandleExe(), processes);

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
        var handleExe = new FakeHandleExe();
        handleExe.Failing.Add(0x58);
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("2\ny\n"), output, new FakeScan(Found(TwoHandles(1000)), Found()), handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Equal<nint[]>([0x1A0], [.. handleExe.Closed.Select(entry => entry.Handle)]);
        Assert.Contains("Closed 1 of 2 handle(s) of pwsh.exe (pid 642).\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A close that throws is traced and counted as not closed, so nothing was acted on.</summary>
    [Fact]
    public void CloseThrowIsCountedAsNotClosed()
    {
        StringWriter output = Writer();
        var handleExe = new FakeHandleExe { CloseThrows = new UnlockException("cannot run handle.exe: it went missing") };
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("2\ny\n"), output, new FakeScan(Found(TwoHandles(1000))), handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(handleExe.Closed);
        Assert.Contains("Closed 0 of 2 handle(s) of pwsh.exe (pid 642).\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A handle the re-check no longer finds holding a locked file is not closed: the value may have been reused since the
    /// scan.</summary>
    [Fact]
    public void HandleThatNoLongerNamesALockedFileIsNotClosed()
    {
        StringWriter output = Writer();
        var scan = new FakeScan(Found(TwoHandles(1000)), Found());
        scan.NoLongerHeld.Add(0x1A0);
        var handleExe = new FakeHandleExe();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("2\ny\n"), output, scan, handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Equal<nint[]>([0x58], [.. handleExe.Closed.Select(entry => entry.Handle)]);
        Assert.Equal<(string, nint)[]>([("recheck", 0x1A0), ("recheck", 0x58), ("close", 0x58)], [.. Assert.Single(scan.CloseCalls).Steps]);
        Assert.Contains("Closed 1 of 2 handle(s) of pwsh.exe (pid 642).\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A re-check that throws for one handle leaves that handle open and counts it as not closed; the other is closed.</summary>
    [Fact]
    public void HandleWhoseRecheckThrowsIsNotClosed()
    {
        StringWriter output = Writer();
        var scan = new FakeScan(Found(TwoHandles(1000)), Found());
        scan.RecheckThrows.Add(0x1A0);
        var handleExe = new FakeHandleExe();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("2\ny\n"), output, scan, handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Equal<nint[]>([0x58], [.. handleExe.Closed.Select(entry => entry.Handle)]);
        Assert.Contains("Closed 1 of 2 handle(s) of pwsh.exe (pid 642).\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A close that throws something other than an unlock failure counts as not closed, and the other handle is still
    /// closed: one failed close never ends the session.</summary>
    [Fact]
    public void UnexpectedCloseThrowIsCountedAsNotClosedAndTheRestGoOn()
    {
        StringWriter output = Writer();
        var handleExe = new FakeHandleExe();
        handleExe.ThrowsUnexpectedly.Add(0x1A0);
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("2\ny\n"), output, new FakeScan(Found(TwoHandles(1000)), Found()), handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Equal<nint[]>([0x58], [.. handleExe.Closed.Select(entry => entry.Handle)]);
        Assert.Contains("Closed 1 of 2 handle(s) of pwsh.exe (pid 642).\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Each handle is re-checked and then closed before the next is re-checked, and handle.exe runs while the re-checking pass is
    /// still open, so the process it holds open cannot give its PID away before the close returns.
    /// </summary>
    [Fact]
    public void HandlesAreRecheckedAndClosedInTurnWhileThePassIsOpen()
    {
        StringWriter output = Writer();
        var scan = new FakeScan(Found(TwoHandles(1000)), Found());
        var handleExe = new FakeHandleExe { OnClose = scan.NoteHandleExeClose };
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("2\ny\n"), output, scan, handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.AllClear, exit);
        CloseCall call = Assert.Single(scan.CloseCalls);
        Assert.Equal<(string, nint)[]>(
            [("recheck", 0x1A0), ("close", 0x1A0), ("handle.exe", 0x1A0), ("recheck", 0x58), ("close", 0x58), ("handle.exe", 0x58)],
            [.. call.Steps]
        );
        Assert.True(call.ClosedWhileOpen);
        Assert.Equal(0, scan.ClosedOutsideACall);
    }

    /// <summary>Every locker the user closes gets one re-checking pass, however many handles it has.</summary>
    [Fact]
    public void EachLockerGetsOneRecheckingPass()
    {
        StringWriter output = Writer();
        Locker code = new("code.exe", 200, [.. Enumerable.Range(1, 5).Select(index => new HeldHandle(0x200 + index, $@"D:\a.wt\x\c{index}"))], 2000);
        var scan = new FakeScan(Found(code, TwoHandles(1000)), Found());
        var handleExe = new FakeHandleExe();
        var processes = new ScriptedProcessControl(Running(("code.exe", 200), ("pwsh.exe", 642)));
        processes.StartedTimes[200] = 2000;
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("2\ny\n2\ny\n"), output, scan, handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Equal<int[]>([200, 642], [.. scan.CloseCalls.Select(call => call.Pid)]);
        Assert.Equal<int[]>([200, 200, 200, 200, 200, 642, 642], [.. handleExe.Closed.Select(entry => entry.Pid)]);
        Assert.Contains("Closed 5 of 5 handle(s) of code.exe (pid 200).\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A process table that cannot be read for the same-process check counts as exited, so nothing is stopped.</summary>
    [Fact]
    public void SnapshotFailureInTheSameProcessCheckCountsAsExited()
    {
        StringWriter output = Writer();
        // The first snapshot finds the excluded processes, the second lists the processes to scan, the third is the check.
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642))) { SnapshotThrowsOn = 3 };
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeScan(Found(Pwsh(1000))), new FakeHandleExe(), processes);

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
        processes.StartedTimes[642] = 2000;
        ElevatedSession session = Session(new StringReader("1\n"), output, new FakeScan(Found(Pwsh(1000))), new FakeHandleExe(), processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(processes.Stopped);
        Assert.Contains("pwsh.exe (pid 642) is no longer the process the scan saw; skipped.\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A PID taken by a newer process since the scan has no handles closed, nor re-checked.</summary>
    [Fact]
    public void CloseOfAPidTakenByANewerProcessIsSkipped()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 2000;
        var scan = new FakeScan(Found(Pwsh(1000)));
        var handleExe = new FakeHandleExe();
        ElevatedSession session = Session(new StringReader("2\ny\n"), output, scan, handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(handleExe.Closed);
        Assert.Empty(scan.CloseCalls);
        Assert.Contains("pwsh.exe (pid 642) is no longer the process the scan saw; skipped.\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A closing pass that cannot confirm the locker as the process the scan saw reports it skipped, not zero handles
    /// closed, and counts as nothing acted on.</summary>
    [Fact]
    public void ClosePassThatCannotConfirmTheLockerIsSkipped()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        var scan = new FakeScan(Found(Pwsh(1000))) { UnconfirmedClose = true };
        var handleExe = new FakeHandleExe();
        ElevatedSession session = Session(new StringReader("2\ny\n"), output, scan, handleExe, processes);

        int exit = session.Run([Locked], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(handleExe.Closed);
        Assert.Contains("pwsh.exe (pid 642) could not be confirmed as the process the scan saw; skipped.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Closed 0 of", text, StringComparison.Ordinal);
    }

    /// <summary>The start time the scan saw is passed to the stop, so it needs no second check of its own.</summary>
    [Fact]
    public void StopPassesTheScanStartTime()
    {
        StringWriter output = Writer();
        var processes = new ScriptedProcessControl(Running(("pwsh.exe", 642)));
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeScan(Found(Pwsh(1000)), Found()), new FakeHandleExe(), processes);

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
        processes.StartedTimes[642] = 1000;
        ElevatedSession session = Session(new StringReader("\n"), output, new FakeScan(Found(Pwsh(null))), new FakeHandleExe(), processes);

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
        ElevatedSession session = Session(new StringReader("1\n"), output, new FakeScan(Found(Pwsh(1000))), new FakeHandleExe(), processes);

        int exit = session.Run([Locked], null, null, null);

        Assert.Equal(UnlockExit.NothingDone, exit);
        Assert.Empty(processes.Stopped);
        Assert.Contains(
            "pwsh.exe (pid 642) could not be confirmed as the process the scan saw; skipped.\n",
            output.ToString(),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    /// A process the file system reports using a folder, for which the scan named no handle there, is reported and never lets the
    /// session report clear, even when nothing else holds the files.
    /// </summary>
    [Fact]
    public void ProcessUsingAFolderUnreadIsReportedAndEndsSomeLeft()
    {
        StringWriter output = Writer();
        var scan = new FakeScan(new LockerFindings([], [new UnreadableUser("pwsh.exe", 642, Locked)], []));
        ElevatedSession session = Session(
            new StringReader(""),
            output,
            scan,
            new FakeHandleExe(),
            new ScriptedProcessControl(Running(("pwsh.exe", 642)))
        );

        int exit = session.Run([Locked], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.SomeLeft, exit);
        Assert.Contains("pwsh.exe (642) uses D:\\a.wt\\x, but its handles cannot be read.\n", text, StringComparison.Ordinal);
        Assert.Contains("Nothing else holds files under those folders.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing holds files under those folders.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// With the production scan, a process using the folder that the scan was not given, so its handles were never read, is reported
    /// by name and ends the session with something left.
    /// </summary>
    [Fact]
    public void UnscannedProcessUsingTheFolderEndsSomeLeftWithTheRealScan()
    {
        StringWriter output = Writer();
        string folder = fx.PathTo("used");
        _ = Directory.CreateDirectory(folder);
        using var child = ReadyChild.Start(folder, fx.PathTo("ready-used"));
        ElevatedSession session = Session(new StringReader(""), output, new LockerScan(), new FakeHandleExe(), new ScriptedProcessControl(Running()));

        int exit = session.Run([folder], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.SomeLeft, exit);
        Assert.Contains($"pwsh.exe ({child.Id}) uses {folder}, but its handles cannot be read.\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// With the production scan, a process that cannot be opened, and that the file system does not report using the folder, is not
    /// reported. Only that is asserted: an antivirus or indexer may open the fresh folder during the query.
    /// </summary>
    [Fact]
    public void UnopenedProcessNotUsingTheFolderIsNotReported()
    {
        StringWriter output = Writer();
        string folder = fx.PathTo("unused");
        _ = Directory.CreateDirectory(folder);
        // No process has this PID, so it cannot be opened, as a protected process cannot be.
        var processes = new ScriptedProcessControl(Running(("ghost.exe", 0x7FFFFFFC)));
        ElevatedSession session = Session(new StringReader(""), output, new LockerScan(), new FakeHandleExe(), processes);

        _ = session.Run([folder], null, null, null);

        string text = output.ToString();
        Assert.DoesNotContain("ghost.exe", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(2147483644)", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A folder the file system reports System using, whose kernel handles cannot be read, is reported and never lets the session
    /// report clear, though System itself is never scanned.
    /// </summary>
    [Fact]
    public void SystemUsingAFolderIsReportedAndEndsSomeLeft()
    {
        StringWriter output = Writer();
        var scan = new FakeScan(new LockerFindings([], [new UnreadableUser("System", 4, Locked)], []));
        ElevatedSession session = Session(
            new StringReader(""),
            output,
            scan,
            new FakeHandleExe(),
            new ScriptedProcessControl(Running(("System", 4), ("pwsh.exe", 642)))
        );

        int exit = session.Run([Locked], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.SomeLeft, exit);
        Assert.Contains("System (4) uses D:\\a.wt\\x, but its handles cannot be read.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(4, Assert.Single(scan.Scanned));
    }

    /// <summary>A folder picked twice, by the same full path in another case, is scanned and reported once.</summary>
    [Fact]
    public void FolderPickedTwiceIsReportedOnce()
    {
        StringWriter output = Writer();
        ElevatedSession session = Session(
            new StringReader(""),
            output,
            new UnreadablePerFolderScan(),
            new FakeHandleExe(),
            new ScriptedProcessControl(Running(("pwsh.exe", 642)))
        );

        _ = session.Run([Locked, Locked.ToUpperInvariant()], null, null, null);

        string text = output.ToString();
        Assert.Single(text.Split('\n'), line => line.Contains("but its handles cannot be read.", StringComparison.Ordinal));
        Assert.Contains("pwsh.exe (642) uses D:\\a.wt\\x, but its handles cannot be read.\n", text, StringComparison.Ordinal);
    }

    /// <summary>With the production scan, a locked folder that is gone from a drive that exists is clear.</summary>
    [Fact]
    public void MissingFolderOnAnExistingDriveIsClearWithTheRealScan()
    {
        StringWriter output = Writer();
        string folder = fx.PathTo("gone");
        ElevatedSession session = Session(new StringReader(""), output, new LockerScan(), new FakeHandleExe(), new ScriptedProcessControl(Running()));

        int exit = session.Run([folder], null, null, null);

        Assert.Equal(UnlockExit.AllClear, exit);
        Assert.Contains("Nothing holds files under those folders.\n", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// With the production scan, a locked folder on a drive this process cannot see, as an elevated token cannot see a drive mapped
    /// unelevated, is reported as unreadable and ends the session with something left.
    /// </summary>
    [Fact]
    public void FolderOnAMissingDriveIsNotClearWithTheRealScan()
    {
        char? letter = UnmountedDriveLetter();
        if (letter is null)
        {
            Assert.Skip("every drive letter from D to Z is in use");
        }
        StringWriter output = Writer();
        string folder = $@"{letter}:\worktree-sweep\gone";
        ElevatedSession session = Session(new StringReader(""), output, new LockerScan(), new FakeHandleExe(), new ScriptedProcessControl(Running()));

        int exit = session.Run([folder], null, null, null);

        string text = output.ToString();
        Assert.Equal(UnlockExit.SomeLeft, exit);
        Assert.Contains($"{folder} cannot be read here, so it is not known what holds it.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing else holds files under those folders.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing holds files under those folders.", text, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public void Dispose() => fx.Dispose();

    /// <summary>An elevated session driven through the given seams.</summary>
    private static ElevatedSession Session(TextReader input, TextWriter output, ILockerScan scan, IHandleExe handleExe, IProcessControl processes) =>
        new(input, output, scan, handleExe, processes);

    /// <summary>A drive letter from Z down to D whose root does not exist, or <see langword="null"/> when every one does.</summary>
    private static char? UnmountedDriveLetter()
    {
        for (char letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!Directory.Exists($@"{letter}:\"))
            {
                return letter;
            }
        }
        return null;
    }

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

    /// <summary>A scan that found these lockers and no process it could not read.</summary>
    private static LockerFindings Found(params Locker[] lockers) => new(lockers, [], []);

    /// <summary>A locker holding one file under the locked folder.</summary>
    private static Locker LockerOf(string process, int pid, ulong? started) =>
        new(process, pid, [new HeldHandle(0x54, @"D:\a.wt\x\file.txt")], started);

    /// <summary>pwsh.exe, pid 642, holding one file, with the start time the scan read.</summary>
    private static Locker Pwsh(ulong? started) => LockerOf("pwsh.exe", 642, started);

    private static Locker TwoHandles(ulong? started) =>
        new("pwsh.exe", 642, [new HeldHandle(0x1A0, @"D:\a.wt\x\a.txt"), new HeldHandle(0x58, @"D:\a.wt\x\b.txt")], started);

    private static Locker SevenHandles() =>
        new("pwsh.exe", 642, [.. Enumerable.Range(1, 7).Select(index => new HeldHandle(0x100 + index, $@"D:\a.wt\x\f{index}"))], null);

    /// <summary>One call to <see cref="ILockerScan.CloseStillHeld"/> on the fake scan.</summary>
    /// <param name="pid">The locker's PID.</param>
    /// <param name="started">The locker's start time as the scan read it.</param>
    private sealed class CloseCall(int pid, ulong? started)
    {
        /// <summary>Gets the locker's PID.</summary>
        public int Pid { get; } = pid;

        /// <summary>Gets the locker's start time as the scan read it.</summary>
        public ulong? Started { get; } = started;

        /// <summary>Gets each step in order: <c>recheck</c> of a handle, <c>close</c> when it was handed to the close, and
        /// <c>handle.exe</c> when handle.exe ran on it during this call.</summary>
        public List<(string Step, nint Handle)> Steps { get; } = [];

        /// <summary>Gets or sets a value indicating whether handle.exe ran while this call was still open.</summary>
        public bool ClosedWhileOpen { get; set; }
    }

    /// <summary>
    /// A scan that returns its scripted findings in order, repeats the last, records the processes it was given and each closing
    /// pass, and in a pass re-checks each handle of a locker with a known start time, handing it to the close unless it was told the
    /// handle is no longer held or that its re-check throws.
    /// </summary>
    private sealed class FakeScan(params LockerFindings[] rounds) : ILockerScan
    {
        private readonly Queue<LockerFindings> pending = new(rounds);
        private CloseCall? open;

        /// <summary>Gets the processes each scan was given, in order.</summary>
        public List<IReadOnlyList<int>> Scanned { get; } = [];

        /// <summary>Gets each closing pass, in order.</summary>
        public List<CloseCall> CloseCalls { get; } = [];

        /// <summary>Gets how many times handle.exe ran while no closing pass was open.</summary>
        public int ClosedOutsideACall { get; private set; }

        /// <summary>Gets the handle values the re-check no longer finds holding a locked file.</summary>
        public HashSet<nint> NoLongerHeld { get; } = [];

        /// <summary>Gets the handle values whose re-check throws; the real scan traces the failure and leaves the handle open.</summary>
        public HashSet<nint> RecheckThrows { get; } = [];

        /// <summary>Gets or sets a value indicating whether each closing pass reports that the locker could not be confirmed as the
        /// process the scan saw, closing nothing.</summary>
        public bool UnconfirmedClose { get; set; }

        public LockerFindings Find(IReadOnlyList<string> lockedPaths, IReadOnlyCollection<int> pids)
        {
            Scanned.Add([.. pids]);
            return pending.Count > 1 ? pending.Dequeue() : pending.Peek();
        }

        public int? CloseStillHeld(Locker locker, IReadOnlyList<string> lockedPaths, Func<HeldHandle, bool> close)
        {
            var call = new CloseCall(locker.Pid, locker.Started);
            CloseCalls.Add(call);
            if (UnconfirmedClose || locker.Started is null)
            {
                return null;
            }
            open = call;
            try
            {
                int closed = 0;
                foreach (HeldHandle held in locker.Handles)
                {
                    call.Steps.Add(("recheck", held.Handle));
                    if (NoLongerHeld.Contains(held.Handle) || RecheckThrows.Contains(held.Handle))
                    {
                        continue;
                    }
                    call.Steps.Add(("close", held.Handle));
                    if (close(held))
                    {
                        closed++;
                    }
                }
                return closed;
            }
            finally
            {
                open = null;
            }
        }

        /// <summary>Notes that handle.exe ran on <paramref name="handle"/>, inside the open closing pass or outside any.</summary>
        /// <param name="handle">The handle handle.exe was asked to close.</param>
        public void NoteHandleExeClose(nint handle)
        {
            if (open is null)
            {
                ClosedOutsideACall++;
                return;
            }
            open.Steps.Add(("handle.exe", handle));
            open.ClosedWhileOpen = true;
        }
    }

    /// <summary>A scan that reports pwsh.exe, pid 642, using every folder it is given, its handles there unread.</summary>
    private sealed class UnreadablePerFolderScan : ILockerScan
    {
        public LockerFindings Find(IReadOnlyList<string> lockedPaths, IReadOnlyCollection<int> pids) =>
            new([], [.. lockedPaths.Select(path => new UnreadableUser("pwsh.exe", 642, path))], []);

        public int? CloseStillHeld(Locker locker, IReadOnlyList<string> lockedPaths, Func<HeldHandle, bool> close) => 0;
    }

    /// <summary>A handle.exe that records what it closed.</summary>
    private sealed class FakeHandleExe : IHandleExe
    {
        private readonly List<(nint Handle, int Pid)> closed = [];

        /// <summary>Gets the handles closed, with the process each belonged to.</summary>
        public IReadOnlyList<(nint Handle, int Pid)> Closed => closed;

        /// <summary>Gets the handles whose closing reports failure.</summary>
        public HashSet<nint> Failing { get; } = [];

        /// <summary>Gets or sets the failure every closing throws, or <see langword="null"/> for none.</summary>
        public UnlockException? CloseThrows { get; set; }

        /// <summary>Gets or sets what is told of each closing as it starts, or <see langword="null"/> for no one.</summary>
        public Action<nint>? OnClose { get; set; }

        /// <summary>Gets the handles whose closing throws something other than an <see cref="UnlockException"/>.</summary>
        public HashSet<nint> ThrowsUnexpectedly { get; } = [];

        public bool Close(nint handle, int pid, out string output)
        {
            OnClose?.Invoke(handle);
            if (ThrowsUnexpectedly.Contains(handle))
            {
                throw new InvalidOperationException($"handle.exe exited before its output could be read for handle {handle:X}");
            }
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

        public IReadOnlyDictionary<int, ProcessEntry> Snapshot()
        {
            snapshots++;
            if (snapshots == SnapshotThrowsOn)
            {
                throw new Win32Exception("cannot take a snapshot of the running processes");
            }
            return table;
        }

        public ulong? StartedAt(int pid) => StartedTimes.TryGetValue(pid, out ulong? started) ? started : null;

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
