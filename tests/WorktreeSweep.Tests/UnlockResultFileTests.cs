using System.Diagnostics;
using System.Text;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The file the elevated unlock session writes its exit code to.</summary>
[Collection(ProcessEnvironment.Name)]
public sealed class UnlockResultFileTests
{
    /// <summary>A code written is read back.</summary>
    [Fact]
    public void WriteThenReadGivesTheCode()
    {
        using var fx = new Fixture();
        string path = fx.PathTo("result.txt");

        UnlockResultFile.Write(path, 3);

        Assert.Equal(3, UnlockResultFile.TryRead(path));
    }

    /// <summary>Writing never replaces a file that already has the name, so a planted link cannot be followed; the file keeps
    /// its content.</summary>
    [Fact]
    public void WriteRefusesAnExistingFile()
    {
        using var fx = new Fixture();
        string path = fx.PathTo("result.txt");
        File.WriteAllText(path, "old");

        Assert.Throws<IOException>(() => UnlockResultFile.Write(path, 3));

        Assert.Equal("old", File.ReadAllText(path));
    }

    /// <summary>A code without its closing newline is not read: the file may still be being written.</summary>
    [Fact]
    public void ReadIgnoresAnUnterminatedCode()
    {
        using var fx = new Fixture();
        string path = fx.PathTo("result.txt");
        File.WriteAllText(path, "3");

        Assert.Null(UnlockResultFile.TryRead(path));
    }

    /// <summary>An absent file, an empty one and one that does not hold an integer all read as no code.</summary>
    [Fact]
    public void TryReadIsNullForAnAbsentEmptyOrInvalidFile()
    {
        using var fx = new Fixture();
        string empty = fx.PathTo("empty.txt");
        string invalid = fx.PathTo("invalid.txt");
        File.WriteAllText(empty, "");
        File.WriteAllText(invalid, "abc");

        Assert.Null(UnlockResultFile.TryRead(fx.PathTo("absent.txt")));
        Assert.Null(UnlockResultFile.TryRead(empty));
        Assert.Null(UnlockResultFile.TryRead(invalid));
    }

    /// <summary>Deleting a file that was never written is not an error.</summary>
    [Fact]
    public void DeleteOfAMissingFileDoesNotThrow()
    {
        using var fx = new Fixture();

        UnlockResultFile.Delete(fx.PathTo("absent.txt"));
    }

    /// <summary>A write that cannot happen is traced as a warning and not thrown, so the session still ends with its code.</summary>
    [Fact]
    public void WriteTracedTracesAnUnwritablePath()
    {
        using var fx = new Fixture();
        using var capture = new TraceCapture();
        string path = fx.PathTo("no-such-folder/result.txt");

        UnlockResultFile.WriteTraced(path, 4);

        Assert.Contains($"cannot write the unlock result to {path}: ", capture.Text, StringComparison.Ordinal);
    }

    /// <summary>Collects what is traced while it is alive.</summary>
    private sealed class TraceCapture : TraceListener
    {
        private readonly StringBuilder text = new();

        public TraceCapture() => Trace.Listeners.Add(this);

        public string Text => text.ToString();

        public override void Write(string? message) => text.Append(message);

        public override void WriteLine(string? message) => text.AppendLine(message);

        protected override void Dispose(bool disposing)
        {
            Trace.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
}
