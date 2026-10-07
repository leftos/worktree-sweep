using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The unelevated unlock offer: when it prints the manual command, when it asks, and what <c>sudo</c>'s exit means.</summary>
public sealed class UnlockOfferTests
{
    private const string Exe = @"C:\tools\worktree-sweep.exe";

    private const int CallerPid = 100;

    private const int SweepPid = 200;

    private static readonly string[] Paths = ["D:/a.wt/x", @"D:\b.wt\y"];

    /// <summary>The offer starts by naming every locked path as given.</summary>
    [Fact]
    public void OfferListsThePaths()
    {
        var sudo = new FakeSudo(SudoMode.Inline, 0);

        (_, string output) = Offer(sudo, "n\n", Exe);

        Assert.StartsWith("Another process holds files open under:\n  D:/a.wt/x\n  D:\\b.wt\\y\n", output, StringComparison.Ordinal);
    }

    /// <summary>Any mode but Inline names the mode, writes the command to run by hand, and skips without running sudo.</summary>
    [Theory]
    [InlineData(SudoMode.ForceNewWindow, "Force New Window")]
    [InlineData(SudoMode.DisableInput, "Disable Input")]
    [InlineData(SudoMode.Disabled, "Disabled")]
    [InlineData(SudoMode.Unknown, "an unrecognised")]
    public void OtherModeWritesTheManualCommandAndSkips(SudoMode mode, string label)
    {
        var sudo = new FakeSudo(mode, 0);

        (UnlockOutcome outcome, string output) = Offer(sudo, "y\n", Exe);

        Assert.Equal(UnlockOutcome.Skipped, outcome);
        Assert.Empty(sudo.Runs);
        Assert.Contains(
            $"sudo is set to {label} mode; worktree-sweep needs inline mode (sudo config --enable normal).\n",
            output,
            StringComparison.Ordinal
        );
        Assert.Contains($"  {SudoCommand.Manual(Exe, Paths)}\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[Y/n]", output, StringComparison.Ordinal);
    }

    /// <summary>No sudo on the machine says so, writes the command to run by hand, and skips.</summary>
    [Fact]
    public void MissingSudoWritesTheManualCommandAndSkips()
    {
        var sudo = new FakeSudo(null, 0);

        (UnlockOutcome outcome, string output) = Offer(sudo, "y\n", Exe);

        Assert.Equal(UnlockOutcome.Skipped, outcome);
        Assert.Empty(sudo.Runs);
        Assert.Contains("sudo is not available on this machine.\n", output, StringComparison.Ordinal);
        Assert.Contains($"  {SudoCommand.Manual(Exe, Paths)}\n", output, StringComparison.Ordinal);
    }

    /// <summary>Declining the question skips without running sudo.</summary>
    [Fact]
    public void DecliningSkips()
    {
        var sudo = new FakeSudo(SudoMode.Inline, 0);

        (UnlockOutcome outcome, string output) = Offer(sudo, "n\n", Exe);

        Assert.Equal(UnlockOutcome.Skipped, outcome);
        Assert.Empty(sudo.Runs);
        Assert.Contains("Run an elevated scan with sudo to find what holds them? [Y/n] ", output, StringComparison.Ordinal);
    }

    /// <summary>End of input declines.</summary>
    [Fact]
    public void EndOfInputDeclines()
    {
        var sudo = new FakeSudo(SudoMode.Inline, 0);

        (UnlockOutcome outcome, _) = Offer(sudo, "", Exe);

        Assert.Equal(UnlockOutcome.Skipped, outcome);
        Assert.Empty(sudo.Runs);
    }

    /// <summary>Accepting, by yes or by the default, runs sudo once with this program's unlock command and maps its exit.</summary>
    [Theory]
    [InlineData("y\n", 0, UnlockOutcome.Unlocked)]
    [InlineData("\n", 0, UnlockOutcome.Unlocked)]
    [InlineData("yes\n", 3, UnlockOutcome.PartlyUnlocked)]
    [InlineData("Y\n", 4, UnlockOutcome.StillLocked)]
    public void AcceptingRunsSudoAndMapsTheExit(string answer, int exitCode, UnlockOutcome expected)
    {
        var sudo = new FakeSudo(SudoMode.Inline, exitCode);

        (UnlockOutcome outcome, _) = Offer(sudo, answer, Exe);

        Assert.Equal(expected, outcome);
        IReadOnlyList<string> run = Assert.Single(sudo.Runs);
        Assert.Equal<string>([Exe, "unlock", "--caller-pid", "100", "--sweep-pid", "200", @"D:\a.wt\x", @"D:\b.wt\y"], run);
    }

    /// <summary>An exit code the elevated side never ends with is a failure that names the command to retry by hand.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(-1)]
    public void UnknownExitCodeThrows(int exitCode)
    {
        var sudo = new FakeSudo(SudoMode.Inline, exitCode);

        UnlockException error = Assert.Throws<UnlockException>(() => Offer(sudo, "y\n", Exe));

        Assert.Contains($"exit code {exitCode}", error.Message, StringComparison.Ordinal);
        Assert.Contains(SudoCommand.Manual(Exe, Paths), error.Message, StringComparison.Ordinal);
    }

    /// <summary>Run through <c>dotnet</c>, sudo would start dotnet rather than this program, so the offer fails naming the apphost.</summary>
    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"C:\Program Files\dotnet\DOTNET.EXE")]
    [InlineData("/usr/share/dotnet/dotnet")]
    public void DotnetHostThrowsNamingTheApphost(string processPath)
    {
        var sudo = new FakeSudo(SudoMode.Inline, 0);

        UnlockException error = Assert.Throws<UnlockException>(() => Offer(sudo, "y\n", processPath));

        Assert.Contains("worktree-sweep.exe", error.Message, StringComparison.Ordinal);
        Assert.Empty(sudo.Runs);
    }

    /// <summary>The bound offer fits the sweep's offer callback.</summary>
    [Fact]
    public void OfferFitsTheCallback()
    {
        var sudo = new FakeSudo(SudoMode.Inline, 3);
        var offer = new UnlockOffer(new StringReader("y\n"), new StringWriter { NewLine = "\n" }, sudo, new SweepProcess(Exe, CallerPid, SweepPid));

        Func<IReadOnlyList<string>, UnlockOutcome> callback = offer.Offer;

        Assert.Equal(UnlockOutcome.PartlyUnlocked, callback(Paths));
    }

    private static (UnlockOutcome Outcome, string Output) Offer(FakeSudo sudo, string input, string? processPath)
    {
        using var writer = new StringWriter { NewLine = "\n" };
        using var reader = new StringReader(input);
        var offer = new UnlockOffer(reader, writer, sudo, new SweepProcess(processPath, CallerPid, SweepPid));
        UnlockOutcome outcome = offer.Offer(Paths);
        return (outcome, writer.ToString());
    }

    /// <summary>A sudo that reports a fixed mode and records each run instead of starting anything.</summary>
    private sealed class FakeSudo(SudoMode? mode, int exitCode) : ISudoRunner
    {
        public List<IReadOnlyList<string>> Runs { get; } = [];

        public SudoMode? Mode() => mode;

        public int Run(IReadOnlyList<string> arguments)
        {
            Runs.Add(arguments);
            return exitCode;
        }
    }
}
