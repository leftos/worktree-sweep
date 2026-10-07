using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>Running a child process, and finding the program to run.</summary>
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
}
