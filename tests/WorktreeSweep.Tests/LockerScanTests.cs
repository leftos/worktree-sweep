using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using WorktreeSweep.Holders;
using WorktreeSweep.Processes;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The production locker scan over children the tests start: the elevated scan of every process, and the re-check before a
/// close.</summary>
[Collection(ProcessEnvironment.Name)]
public sealed class LockerScanTests : IDisposable
{
    /// <summary>The command that runs the elevated test, named when the run is not elevated.</summary>
    private const string RunCommand =
        "sudo dotnet test WorktreeSweep.slnx -c Release --no-build -- --filter-method *ElevatedScanFindsTheHolderOfAGreekPath --explicit on";

    private readonly Fixture fx = new();

    /// <summary>
    /// Elevated, the scan over every process finds a child holding <c>δοκιμή\αρχείο.txt</c> under the fixture folder, shared with no
    /// one, with the Greek path intact. Run by hand, elevated, from the repo root after a Release build:
    /// <c>sudo dotnet test WorktreeSweep.slnx -c Release --no-build -- --filter-method *ElevatedScanFindsTheHolderOfAGreekPath --explicit on</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public void ElevatedScanFindsTheHolderOfAGreekPath()
    {
        if (!Elevated())
        {
            Assert.Skip($"run elevated: {RunCommand}");
        }
        string folder = fx.PathTo("δοκιμή");
        _ = Directory.CreateDirectory(folder);
        string file = Path.Join(folder, "αρχείο.txt");
        File.WriteAllText(file, "held");
        string ready = fx.PathTo("ready-elevated");
        using var child = ReadyChild.Run(fx.Root, ready, HoldScript([file], ready));
        int own = Environment.ProcessId;
        IReadOnlyList<int> pids = [.. ProcessTable.Snapshot().Keys.Where(pid => !ProcessTable.SystemPids.Contains(pid) && pid != own)];

        LockerFindings found = LockerScan.Elevated().Find([folder], pids);

        Locker locker = Assert.Single(found.Lockers, candidate => candidate.Pid == child.Id);
        HeldHandle held = Assert.Single(locker.Handles);
        Assert.EndsWith(@"\δοκιμή\αρχείο.txt", held.Name, StringComparison.Ordinal);
        child.Stop();
    }

    /// <summary>
    /// The closing pass hands the close a handle a same-user child still holds under the folder, and hands it nothing under another
    /// start time or an unknown start time, reporting the process unconfirmed; it hands it nothing under a folder the handle is not
    /// under and reports 0 there because the process was confirmed, and it hands it nothing once the child has exited, reporting the
    /// process unconfirmed there too.
    /// </summary>
    [Fact]
    public void CloseStillHeldClosesOnlyForTheSameProcessAndFolder()
    {
        string folder = fx.PathTo("held");
        string other = fx.PathTo("other");
        _ = Directory.CreateDirectory(other);
        string ready = fx.PathTo("ready-held");
        var child = ReadyChild.Run(fx.Root, ready, HoldScript([HeldFile(folder)], ready));
        bool stopped = false;
        try
        {
            var scan = new LockerScan();
            Locker locker = Assert.Single(scan.Find([folder], [child.Id]).Lockers);
            HeldHandle held = Assert.Single(locker.Handles);
            var handed = new List<HeldHandle>();
            bool Close(HeldHandle candidate)
            {
                handed.Add(candidate);
                return true;
            }

            Assert.Equal(1, scan.CloseStillHeld(locker, [folder], Close));
            Assert.Equal([held], handed);
            handed.Clear();
            Assert.Null(scan.CloseStillHeld(locker with { Started = locker.Started + 1 }, [folder], Close));
            Assert.Null(scan.CloseStillHeld(locker with { Started = null }, [folder], Close));
            Assert.Equal(0, scan.CloseStillHeld(locker, [other], Close));
            // Kills the child, waits for its exit and drops the test's own handle to it; a PID reused since has another start time.
            child.Dispose();
            stopped = true;
            Assert.Null(scan.CloseStillHeld(locker, [folder], Close));
            Assert.Empty(handed);
        }
        finally
        {
            if (!stopped)
            {
                child.Dispose();
            }
        }
    }

    /// <summary>
    /// The closing pass reports the process unconfirmed, closing nothing, once the child has exited even while this test still holds
    /// the child's process object open, which keeps its PID and start time alive for the pass's open.
    /// </summary>
    [Fact]
    public void CloseStillHeldReportsAnExitedLockerUnconfirmedWhileItsProcessIsStillOpen()
    {
        string folder = fx.PathTo("exited");
        string ready = fx.PathTo("ready-exited");
        var child = ReadyChild.Run(fx.Root, ready, HoldScript([HeldFile(folder)], ready));
        bool stopped = false;
        try
        {
            using var traces = new TraceCapture();
            var scan = new LockerScan();
            Locker locker = Assert.Single(scan.Find([folder], [child.Id]).Lockers);
            using SafeFileHandle? kept = OpenHandles.OpenSource(child.Id);
            Assert.NotNull(kept);
            var handed = new List<HeldHandle>();
            bool Close(HeldHandle candidate)
            {
                handed.Add(candidate);
                return true;
            }

            // Kills the child and waits for its exit; the handle kept above holds its process object, PID and start time alive.
            child.Dispose();
            stopped = true;
            Assert.Null(scan.CloseStillHeld(locker, [folder], Close));
            Assert.Empty(handed);
            Assert.Contains("has exited; no handle closed", traces.Text, StringComparison.Ordinal);
        }
        finally
        {
            if (!stopped)
            {
                child.Dispose();
            }
        }
    }

    /// <summary>While the close runs, the locker's PID still opens as the process the scan saw, with the same start time: the pass
    /// keeps the process open until the close has returned.</summary>
    [Fact]
    public void CloseStillHeldKeepsTheProcessWhileTheCloseRuns()
    {
        string folder = fx.PathTo("kept");
        string ready = fx.PathTo("ready-kept");
        using var child = ReadyChild.Run(fx.Root, ready, HoldScript([HeldFile(folder)], ready));
        var scan = new LockerScan();
        Locker locker = Assert.Single(scan.Find([folder], [child.Id]).Lockers);
        var startedDuringClose = new List<ulong?>();

        int? closed = scan.CloseStillHeld(
            locker,
            [folder],
            _ =>
            {
                startedDuringClose.Add(HolderFinder.StartedAt(locker.Pid));
                return true;
            }
        );

        Assert.Equal(1, closed);
        Assert.NotNull(locker.Started);
        Assert.Equal([locker.Started], startedDuringClose);
    }

    /// <summary>The real pass re-checks a handle and hands it to the close before it re-checks the next.</summary>
    [Fact]
    public void CloseStillHeldRechecksAndClosesEachHandleInTurn()
    {
        string folder = fx.PathTo("turns");
        string ready = fx.PathTo("ready-turns");
        using var child = ReadyChild.Run(fx.Root, ready, HoldScript(TwoHeldFiles(folder), ready));
        var steps = new List<(string Step, string File)>();
        var scan = new LockerScan(
            (source, pid, held, roots, account) =>
            {
                steps.Add(("recheck", Path.GetFileName(held.Name)));
                return LockerScan.StillNamesALockedFile(source, pid, held, roots, account);
            }
        );
        Locker locker = Assert.Single(scan.Find([folder], [child.Id]).Lockers);

        int? closed = scan.CloseStillHeld(
            locker,
            [folder],
            held =>
            {
                steps.Add(("close", Path.GetFileName(held.Name)));
                return true;
            }
        );

        Assert.Equal(2, closed);
        Assert.Equal([("recheck", "a.txt"), ("close", "a.txt"), ("recheck", "b.txt"), ("close", "b.txt")], steps);
    }

    /// <summary>In the real pass, a re-check that throws for the first handle leaves it unclosed, and the second is still closed.</summary>
    [Fact]
    public void CloseStillHeldLeavesAHandleWhoseRecheckThrows()
    {
        string folder = fx.PathTo("throws");
        string ready = fx.PathTo("ready-throws");
        using var child = ReadyChild.Run(fx.Root, ready, HoldScript(TwoHeldFiles(folder), ready));
        var scan = new LockerScan(
            (source, pid, held, roots, account) =>
                Path.GetFileName(held.Name) == "a.txt"
                    ? throw new IOException("cannot duplicate the handle")
                    : LockerScan.StillNamesALockedFile(source, pid, held, roots, account)
        );
        Locker locker = Assert.Single(scan.Find([folder], [child.Id]).Lockers);
        var handed = new List<string>();

        int? closed = scan.CloseStillHeld(
            locker,
            [folder],
            held =>
            {
                handed.Add(Path.GetFileName(held.Name));
                return true;
            }
        );

        Assert.Equal(1, closed);
        Assert.Equal(["b.txt"], handed);
    }

    /// <summary>A folder that opens but whose users the file system cannot list comes back unreachable, as the user picked it: what
    /// holds it is not known.</summary>
    [Fact]
    public void FolderWhoseUsersCannotBeListedIsUnreachable()
    {
        string folder = fx.PathTo("unlisted");
        _ = Directory.CreateDirectory(folder);
        var scan = new LockerScan(root => throw new IOException($"cannot list the processes using {root}: NTSTATUS 0xC0000022"));

        LockerFindings found = scan.Find([folder], []);

        Assert.Equal([folder], found.Unreachable);
        Assert.Empty(found.Lockers);
        Assert.Empty(found.Unreadable);
    }

    /// <summary>A folder whose users the file system lists is not unreachable.</summary>
    [Fact]
    public void FolderWhoseUsersAreListedIsNotUnreachable()
    {
        string folder = fx.PathTo("listed");
        _ = Directory.CreateDirectory(folder);
        var scan = new LockerScan(_ => []);

        LockerFindings found = scan.Find([folder], []);

        Assert.Empty(found.Unreachable);
    }

    /// <summary>
    /// Each process with a timed-out lookup is listed once, by PID, with its image name; one already reported as using a folder unread
    /// is left out, so it gets only that line.
    /// </summary>
    [Fact]
    public void TimedOutLookupsLeaveOutAnUnreadableProcess()
    {
        var table = new Dictionary<int, ProcessEntry> { [642] = new(1, "pwsh.exe"), [800] = new(1, "code.exe") };

        IReadOnlyList<TimedOutProcess> timedOut = LockerScan.TimedOutLookups(
            new HashSet<int> { 900, 800, 642 },
            [new UnreadableUser("pwsh.exe", 642, @"D:\a.wt\x")],
            [],
            table
        );

        Assert.Equal([new TimedOutProcess("code.exe", 800), new TimedOutProcess("unknown", 900)], timedOut);
    }

    /// <summary>A locker with a timed-out lookup is left out: it is offered as a locker, so it is not also reported as timed out.</summary>
    [Fact]
    public void TimedOutLookupsLeaveOutALocker()
    {
        var table = new Dictionary<int, ProcessEntry> { [642] = new(1, "pwsh.exe"), [800] = new(1, "code.exe") };
        var locker = new Locker("pwsh.exe", 642, [new HeldHandle(0x54, @"D:\a.wt\x\file.txt")], 1000);

        IReadOnlyList<TimedOutProcess> timedOut = LockerScan.TimedOutLookups(new HashSet<int> { 800, 642 }, [], [locker], table);

        Assert.Equal([new TimedOutProcess("code.exe", 800)], timedOut);
    }

    /// <inheritdoc/>
    public void Dispose() => fx.Dispose();

    /// <summary>Whether this process's token is in the Administrator role.</summary>
    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Creates <paramref name="folder"/> with a file in it, and returns the file's path.</summary>
    private static string HeldFile(string folder)
    {
        _ = Directory.CreateDirectory(folder);
        string file = Path.Join(folder, "held.txt");
        File.WriteAllText(file, "held");
        return file;
    }

    /// <summary>Creates <paramref name="folder"/> with <c>a.txt</c> and <c>b.txt</c> in it, and returns their paths in that order.</summary>
    private static string[] TwoHeldFiles(string folder)
    {
        _ = Directory.CreateDirectory(folder);
        string[] files = [Path.Join(folder, "a.txt"), Path.Join(folder, "b.txt")];
        foreach (string file in files)
        {
            File.WriteAllText(file, "held");
        }
        return files;
    }

    /// <summary>A child script that holds each of <paramref name="files"/> shared with no one, then writes <paramref name="ready"/>.</summary>
    private static string HoldScript(IReadOnlyList<string> files, string ready) =>
        string.Concat(files.Select((file, index) => $"$f{index} = [IO.File]::Open({ReadyChild.Quoted(file)}, 'Open', 'Read', 'None'); "))
        + $"Set-Content -LiteralPath {ReadyChild.Quoted(ready)} ready; Start-Sleep 300";
}
