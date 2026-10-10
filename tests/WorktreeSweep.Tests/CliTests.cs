using System.Text.Json;
using WorktreeSweep.Discovery;
using WorktreeSweep.Report;

namespace WorktreeSweep.Tests;

/// <summary>The built <c>worktree-sweep.exe</c>: its modes, its output and its exit codes.</summary>
public sealed class CliTests
{
    private const string LogVariable = "WORKTREE_SWEEP_LOG";

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    /// <summary><c>--list</c> prints the table: its header, then one numbered row per candidate.</summary>
    [Fact]
    public async Task ListPrintsTheTable()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        Fixture.AddWorktree(repo, fx.PathTo("repo.wt/feat"), "feat");
        Directory.CreateDirectory(fx.PathTo("repo.wt/stray"));

        Run run = await ExeRunner.RunAsync(RunTimeout, fx.Root, "--list");

        Assert.Equal(0, run.Code);
        string[] lines = run.Stdout.Split('\n');
        Assert.StartsWith("#  PATH", lines[0], StringComparison.Ordinal);
        Assert.Contains("KIND", lines[0], StringComparison.Ordinal);
        Assert.Matches(@"^1  repo\.wt[\\/]feat +worktree +feat ", lines[1]);
        Assert.Matches(@"^2  repo\.wt[\\/]stray +orphan ", lines[2]);
        Assert.Equal("", lines[3]);
        Assert.Equal(4, lines.Length);
    }

    /// <summary><c>--json</c> prints one JSON document whose candidates carry their kind.</summary>
    [Fact]
    public async Task JsonPrintsTheReport()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        Fixture.AddWorktree(repo, fx.PathTo("repo.wt/feat"), "feat");
        Directory.CreateDirectory(fx.PathTo("repo.wt/stray"));

        Run run = await ExeRunner.RunAsync(RunTimeout, fx.Root, "--json");

        Assert.Equal(0, run.Code);
        Assert.Equal(["orphan", "registered"], Kinds(run.Stdout).Order(StringComparer.Ordinal));
    }

    /// <summary>A non-ASCII candidate path in the table stays UTF-8 on a redirected standard output.</summary>
    [Fact]
    public async Task ListTableKeepsANonAsciiPathAsUtf8()
    {
        using var fx = Fixture.InRepoTmp();
        string repo = fx.Repo("repo");
        Fixture.AddWorktree(repo, fx.PathTo("repo.wt/feat"), "feat");
        Directory.CreateDirectory(fx.PathTo("repo.wt/δοκιμή"));

        Run run = await ExeRunner.RunAsync(RunTimeout, fx.Root, "--list");

        Assert.Equal(0, run.Code);
        Assert.Contains("δοκιμή", run.Stdout, StringComparison.Ordinal);
    }

    /// <summary>A non-ASCII candidate path in the report stays UTF-8 on a redirected standard output, and parses.</summary>
    [Fact]
    public async Task JsonKeepsANonAsciiPathAsUtf8()
    {
        using var fx = Fixture.InRepoTmp();
        string repo = fx.Repo("repo");
        Fixture.AddWorktree(repo, fx.PathTo("repo.wt/feat"), "feat");
        Directory.CreateDirectory(fx.PathTo("repo.wt/δοκιμή"));

        Run run = await ExeRunner.RunAsync(RunTimeout, fx.Root, "--json");

        Assert.Equal(0, run.Code);
        using var document = JsonDocument.Parse(run.Stdout);
        Assert.Contains("δοκιμή", run.Stdout, StringComparison.Ordinal);
    }

    /// <summary><c>--list</c> with <c>--json</c> is a usage error.</summary>
    [Fact]
    public async Task ListWithJsonIsAUsageError()
    {
        using var fx = new Fixture();

        Run run = await ExeRunner.RunAsync(RunTimeout, fx.Root, "--list", "--json");

        Assert.Equal(2, run.Code);
        Assert.Equal("", run.Stdout);
    }

    /// <summary>An unknown option is a usage error.</summary>
    [Fact]
    public async Task UnknownOptionIsAUsageError()
    {
        using var fx = new Fixture();

        Run run = await ExeRunner.RunAsync(RunTimeout, fx.Root, "--list", "--bogus");

        Assert.Equal(2, run.Code);
        Assert.Contains("--bogus", run.Stderr, StringComparison.Ordinal);
    }

    /// <summary>A root that does not exist is a failure, reported on standard error.</summary>
    [Fact]
    public async Task MissingRootFails()
    {
        using var fx = new Fixture();

        Run run = await ExeRunner.RunAsync(RunTimeout, fx.PathTo("missing"), "--list");

        Assert.Equal(1, run.Code);
        Assert.StartsWith("error: ", run.Stderr, StringComparison.Ordinal);
        Assert.Equal("", run.Stdout);
    }

    /// <summary>An empty root is a failure, reported on standard error.</summary>
    [Fact]
    public async Task EmptyRootFails()
    {
        Run run = await ExeRunner.RunAsync(RunTimeout, "", "--list");

        Assert.Equal(1, run.Code);
        Assert.StartsWith("error: ", run.Stderr, StringComparison.Ordinal);
        Assert.Equal("", run.Stdout);
    }

    /// <summary>An empty root with no flags is a failure reported on standard error, before any window is built.</summary>
    [Fact]
    public async Task EmptyRootWithoutFlagsFailsBeforeTheWindow()
    {
        Run run = await ExeRunner.RunAsync(RunTimeout, "");

        Assert.Equal(1, run.Code);
        Assert.StartsWith("error: ", run.Stderr, StringComparison.Ordinal);
        Assert.Equal("", run.Stdout);
    }

    /// <summary>Debug traces reach standard error only with <c>WORKTREE_SWEEP_LOG=debug</c>.</summary>
    [Fact]
    public async Task DebugTracesNeedTheLogVariable()
    {
        using var fx = new Fixture();
        fx.Repo("repo");
        Directory.CreateDirectory(fx.PathTo("repo.wt"));
        File.WriteAllText(fx.PathTo("repo.wt/notes.txt"), "");

        Run quiet = await ExeRunner.RunAsync(RunTimeout, fx.Root, "--list");
        Run debug = await ExeRunner.RunWithEnvAsync(RunTimeout, new Dictionary<string, string> { [LogVariable] = "debug" }, fx.Root, "--list");

        Assert.Equal(0, quiet.Code);
        Assert.DoesNotContain("debug:", quiet.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, debug.Code);
        Assert.Contains("debug: skipping file ", debug.Stderr, StringComparison.Ordinal);
        Assert.Equal(quiet.Stdout, debug.Stdout);
    }

    /// <summary>A trace warning goes to standard error as one line, leaving standard output one valid JSON document.</summary>
    [Fact]
    public async Task MalformedMarkerWarnsOnStandardError()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string worktree = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, worktree, "feat");
        string admin = Discoverer.ReadGitdirFile(worktree) ?? throw new InvalidOperationException($"{worktree} has no admin dir");
        File.WriteAllText(Path.Join(admin, ReleasedMarker.FileName), "{not json");

        Run run = await ExeRunner.RunAsync(RunTimeout, fx.Root, "--json");

        Assert.Equal(0, run.Code);
        Assert.StartsWith("warning: ignoring the released marker", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(["registered"], Kinds(run.Stdout));
    }

    /// <summary>The hidden <c>unlock</c> subcommand needs at least one path; without one it is a usage error.</summary>
    [Fact]
    public async Task UnlockWithNoPathsIsAUsageError()
    {
        Run run = await ExeRunner.RunAsync(RunTimeout, "unlock");

        Assert.Equal(2, run.Code);
        Assert.StartsWith("error: ", run.Stderr, StringComparison.Ordinal);
        Assert.Equal("", run.Stdout);
    }

    /// <summary><c>--help</c> does not list the hidden <c>unlock</c> subcommand.</summary>
    [Fact]
    public async Task HelpDoesNotListUnlock()
    {
        Run run = await ExeRunner.RunAsync(RunTimeout, "--help");

        Assert.Equal(0, run.Code);
        Assert.Contains("--list", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("unlock", run.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The hidden <c>unlock</c> subcommand accepts <c>--caller-started</c>: with no paths, only the missing paths are
    /// an error, not an unrecognised option.</summary>
    [Fact]
    public async Task UnlockAcceptsCallerStarted()
    {
        Run run = await ExeRunner.RunAsync(RunTimeout, "unlock", "--caller-started", "133700000000000000");

        Assert.Equal(2, run.Code);
        Assert.Equal("error: Required argument missing for command: 'unlock'.\n", run.Stderr);
        Assert.Equal("", run.Stdout);
    }

    /// <summary><c>--help</c> does not list the hidden <c>--caller-started</c> either.</summary>
    [Fact]
    public async Task HelpDoesNotListCallerStarted()
    {
        Run run = await ExeRunner.RunAsync(RunTimeout, "--help");

        Assert.Equal(0, run.Code);
        Assert.DoesNotContain("--caller-started", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--sweep-pid", run.Stdout, StringComparison.Ordinal);
    }

    /// <summary>A subcommand's help leaves out the scan's ROOT argument, which <c>remove</c> does not take.</summary>
    [Fact]
    public async Task RemoveHelpLeavesOutRoot()
    {
        Run run = await ExeRunner.RunAsync(RunTimeout, "remove", "--help");

        Assert.Equal(0, run.Code);
        Assert.Contains("worktree-sweep remove <PATH> [options]", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("ROOT", run.Stdout, StringComparison.Ordinal);
    }

    /// <summary>The root command's own help still shows ROOT, in the usage and under Arguments.</summary>
    [Fact]
    public async Task RootHelpStillShowsRoot()
    {
        Run run = await ExeRunner.RunAsync(RunTimeout, "--help");

        Assert.Equal(0, run.Code);
        Assert.Contains("[<ROOT>]", run.Stdout, StringComparison.Ordinal);
        int arguments = run.Stdout.IndexOf("Arguments:", StringComparison.Ordinal);
        Assert.True(arguments >= 0, run.Stdout);
        Assert.Contains("ROOT", run.Stdout[arguments..], StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>unlock</c> is the subcommand even when the current directory holds a folder named <c>unlock</c>: the error is the
    /// subcommand's missing paths, not the root's missing mode flag.
    /// </summary>
    [Fact]
    public async Task UnlockIsTheSubcommandOverAFolderNamedUnlock()
    {
        using var fx = new Fixture();
        Directory.CreateDirectory(fx.PathTo("unlock"));

        Run run = await ExeRunner.RunInAsync(fx.Root, RunTimeout, "unlock");

        Assert.Equal(2, run.Code);
        Assert.Equal("error: Required argument missing for command: 'unlock'.\n", run.Stderr);
        Assert.Equal("", run.Stdout);
    }

    private static List<string?> Kinds(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.GetProperty("candidates").EnumerateArray().Select(candidate => candidate.GetProperty("kind").GetString())];
    }
}
