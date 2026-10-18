using System.Diagnostics;
using System.Globalization;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>Running a child process, finding the program to run, and bounding the wait for a session that cannot exit.</summary>
[Collection(ProcessEnvironment.Name)]
public sealed class ChildProcessTests
{
    /// <summary>Only the codes a missing program gives mean it is not on the PATH.</summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(5, false)]
    public void IsNotFoundReadsTheStartFailureCode(int code, bool expected) => Assert.Equal(expected, ChildProcess.IsNotFound(code));

    /// <summary>The PATH search returns the first fully qualified folder that holds the program, quotes and all.</summary>
    [Fact]
    public void FindOnPathTakesTheFirstFolderHoldingTheProgram()
    {
        using var fx = new Fixture();
        string empty = Directory.CreateDirectory(fx.PathTo("empty")).FullName;
        string first = Directory.CreateDirectory(fx.PathTo("first")).FullName;
        string second = Directory.CreateDirectory(fx.PathTo("second")).FullName;
        File.WriteAllText(Path.Join(first, "handle.exe"), "");
        File.WriteAllText(Path.Join(second, "handle.exe"), "");

        string? found = ChildProcess.FindOnPath("handle.exe", $"{empty};;\"{first}\";{second}");

        Assert.Equal(Path.Join(first, "handle.exe"), found);
    }

    /// <summary>
    /// A <c>handle.exe</c> only in the current directory, or in a folder a relative PATH entry names, is not found. Changes the
    /// process's current directory, so it runs in the collection that runs alone.
    /// </summary>
    [Fact]
    public void FindOnPathNeverSearchesTheCurrentDirectory()
    {
        using var fx = new Fixture();
        string empty = Directory.CreateDirectory(fx.PathTo("empty")).FullName;
        Directory.CreateDirectory(fx.PathTo("bin"));
        File.WriteAllText(fx.PathTo("handle.exe"), "");
        File.WriteAllText(fx.PathTo("bin/handle.exe"), "");
        string previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = fx.Root;
        try
        {
            Assert.Null(ChildProcess.FindOnPath("handle.exe", $".;;bin;{empty}"));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    /// <summary>
    /// A child that writes its result and then hangs is left running once the bound after the result passes: its code is used and
    /// the result file is gone. The child writes its PID first so the test can stop it.
    /// </summary>
    [Fact]
    public void ChildThatWritesItsResultAndHangsIsLeftAfterTheBound()
    {
        using var fx = new Fixture();
        string resultFile = fx.PathTo("result.txt");
        string pidFile = fx.PathTo("pid.txt");
        try
        {
            AttachedExit exit = ChildProcess.RunAttached(
                "pwsh",
                ["-NoProfile", "-NonInteractive", "-Command", WriteAndHang(pidFile, resultFile)],
                resultFile,
                TimeSpan.FromSeconds(1)
            );

            Assert.Equal(new AttachedExit(3, StillRunning: true), exit);
            Assert.False(File.Exists(resultFile));
        }
        finally
        {
            StopChild(pidFile);
        }
    }

    /// <summary>A child that writes its result and exits within the bound is judged by its exit code.</summary>
    [Fact]
    public void ChildThatWritesItsResultAndExitsUsesItsExitCode()
    {
        using var fx = new Fixture();
        string resultFile = fx.PathTo("result.txt");

        AttachedExit exit = ChildProcess.RunAttached(
            "pwsh",
            ["-NoProfile", "-NonInteractive", "-Command", $"Set-Content -LiteralPath {ReadyChild.Quoted(resultFile)} 3; exit 0"],
            resultFile,
            TimeSpan.FromSeconds(10)
        );

        Assert.Equal(new AttachedExit(0, StillRunning: false), exit);
        Assert.False(File.Exists(resultFile));
    }

    /// <summary>A child that exits without writing a result is judged by its exit code.</summary>
    [Fact]
    public void ChildThatExitsWithoutAResultUsesItsExitCode()
    {
        using var fx = new Fixture();
        string resultFile = fx.PathTo("result.txt");

        AttachedExit exit = ChildProcess.RunAttached(
            "pwsh",
            ["-NoProfile", "-NonInteractive", "-Command", "exit 2"],
            resultFile,
            TimeSpan.FromSeconds(10)
        );

        Assert.Equal(new AttachedExit(2, StillRunning: false), exit);
        Assert.False(File.Exists(resultFile));
    }

    /// <summary>The script of a child that names itself, reports 3 and then hangs.</summary>
    /// <param name="pidFile">Where the child writes its PID, before its result.</param>
    /// <param name="resultFile">Where the child writes the code.</param>
    /// <returns>The PowerShell script.</returns>
    private static string WriteAndHang(string pidFile, string resultFile) =>
        $"Set-Content -LiteralPath {ReadyChild.Quoted(pidFile)} $PID; "
        + $"Set-Content -LiteralPath {ReadyChild.Quoted(resultFile)} 3; "
        + "Start-Sleep 120";

    /// <summary>Stops the child that wrote its PID to the file, when it wrote one.</summary>
    /// <param name="pidFile">The file the child writes its PID to.</param>
    private static void StopChild(string pidFile)
    {
        if (!File.Exists(pidFile))
        {
            return;
        }
        int pid = int.Parse(File.ReadAllText(pidFile), NumberStyles.Integer, CultureInfo.InvariantCulture);
        try
        {
            using var child = Process.GetProcessById(pid);
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit();
            }
        }
        catch (ArgumentException)
        {
            // The child exited by itself.
        }
    }
}
