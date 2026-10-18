using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The elevated <c>unlock</c> arguments, and the hint to run the command by hand.</summary>
public sealed class SudoCommandTests
{
    private const string Exe = @"C:\tools\worktree-sweep.exe";

    private const string Result = @"C:\Temp\worktree-sweep-unlock.txt";

    /// <summary>The arguments name this program, then the caller and the sweep, then the result file and every backslashed path.</summary>
    [Fact]
    public void ArgvPassesCallerPidAndBackslashPaths()
    {
        string[] paths = ["D:/a.wt/x", @"D:\b"];
        var self = new SweepProcess(Exe, 4242, null, 5150);

        Assert.Equal<string[]>(
            [Exe, "unlock", "--caller-pid", "4242", "--sweep-pid", "5150", "--result", Result, @"D:\a.wt\x", @"D:\b"],
            [.. SudoCommand.Argv(Exe, self, Result, paths)]
        );
    }

    /// <summary>A caller that is not known leaves the caller pid out.</summary>
    [Fact]
    public void ArgvLeavesOutAnUnknownCaller()
    {
        var self = new SweepProcess(Exe, null, null, 5150);

        Assert.Equal<string[]>(
            [Exe, "unlock", "--sweep-pid", "5150", "--result", Result, @"D:\a"],
            [.. SudoCommand.Argv(Exe, self, Result, [@"D:\a"])]
        );
    }

    /// <summary>A caller whose creation time is known names both its PID and that time, before the sweep's PID.</summary>
    [Fact]
    public void ArgvPassesCallerStarted()
    {
        var self = new SweepProcess(Exe, 4242, 133700000000000000UL, 5150);

        Assert.Equal<string[]>(
            [Exe, "unlock", "--caller-pid", "4242", "--caller-started", "133700000000000000", "--sweep-pid", "5150", "--result", Result, @"D:\a"],
            [.. SudoCommand.Argv(Exe, self, Result, [@"D:\a"])]
        );
    }

    /// <summary>A caller whose creation time is unknown leaves <c>--caller-started</c> out.</summary>
    [Fact]
    public void ArgvLeavesOutAnUnknownCallerStarted()
    {
        var self = new SweepProcess(Exe, 4242, null, 5150);

        Assert.Equal<string[]>(
            [Exe, "unlock", "--caller-pid", "4242", "--sweep-pid", "5150", "--result", Result, @"D:\a"],
            [.. SudoCommand.Argv(Exe, self, Result, [@"D:\a"])]
        );
    }

    /// <summary>A caller that is not known leaves both caller flags out, even when a creation time is at hand.</summary>
    [Fact]
    public void ArgvLeavesOutCallerStartedWithoutACallerPid()
    {
        var self = new SweepProcess(Exe, null, 133700000000000000UL, 5150);

        Assert.Equal<string[]>(
            [Exe, "unlock", "--sweep-pid", "5150", "--result", Result, @"D:\a"],
            [.. SudoCommand.Argv(Exe, self, Result, [@"D:\a"])]
        );
    }

    /// <summary>The manual command quotes the program and every path, and carries no pid flags.</summary>
    [Fact]
    public void ManualQuotesEveryArgument() =>
        Assert.Equal(
            """
            "C:\tools\worktree-sweep.exe" unlock "D:\a.wt\x" "D:\b"
            """,
            SudoCommand.Manual(@"C:\tools\worktree-sweep.exe", ["D:/a.wt/x", @"D:\b"])
        );

    /// <summary>A quoted path drops its trailing separator, which would otherwise escape the closing quote.</summary>
    [Fact]
    public void ManualDropsATrailingSeparator() =>
        Assert.Equal(
            """
            "C:\tools\worktree-sweep.exe" unlock "D:\a.wt\x"
            """,
            SudoCommand.Manual(@"C:\tools\worktree-sweep.exe", ["D:/a.wt/x/"])
        );

    /// <summary>A drive root keeps its separator, which is the whole path.</summary>
    [Fact]
    public void ManualKeepsADriveRoot() =>
        Assert.Equal(
            """
            "C:\tools\worktree-sweep.exe" unlock "D:\"
            """,
            SudoCommand.Manual(@"C:\tools\worktree-sweep.exe", [@"D:\"])
        );

    /// <summary>The hint is the two lines that name the administrator terminal and the command to run there.</summary>
    [Fact]
    public void WriteManualWritesTheHint()
    {
        StringWriter output = new() { NewLine = "\n" };

        SudoCommand.WriteManual(output, @"C:\tools\worktree-sweep.exe", [@"D:\a"]);

        string expected = """
            To find and clear the locks, run this in an administrator terminal, then run worktree-sweep again:
              "C:\tools\worktree-sweep.exe" unlock "D:\a"
            """;
        Assert.Equal(expected + "\n", output.ToString());
    }
}
