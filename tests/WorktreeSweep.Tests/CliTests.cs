using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WorktreeSweep.Discovery;
using WorktreeSweep.Git;
using WorktreeSweep.Report;

namespace WorktreeSweep.Tests;

/// <summary>The built <c>worktree-sweep.exe</c>: its modes, its output and its exit codes.</summary>
public sealed class CliTests
{
    private const string LogVariable = "WORKTREE_SWEEP_LOG";

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    private static readonly string Exe = Path.Join(AppContext.BaseDirectory, "worktree-sweep.exe");

    /// <summary><c>--list</c> prints the table: its header, then one numbered row per candidate.</summary>
    [Fact]
    public async Task ListPrintsTheTable()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        Fixture.AddWorktree(repo, fx.PathTo("repo.wt/feat"), "feat");
        Directory.CreateDirectory(fx.PathTo("repo.wt/stray"));

        Run run = await RunAsync(fx.Root, "--list");

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

        Run run = await RunAsync(fx.Root, "--json");

        Assert.Equal(0, run.Code);
        Assert.Equal(["orphan", "registered"], Kinds(run.Stdout).Order(StringComparer.Ordinal));
    }

    /// <summary><c>--list</c> with <c>--json</c> is a usage error.</summary>
    [Fact]
    public async Task ListWithJsonIsAUsageError()
    {
        using var fx = new Fixture();

        Run run = await RunAsync(fx.Root, "--list", "--json");

        Assert.Equal(2, run.Code);
        Assert.Equal("", run.Stdout);
    }

    /// <summary>An unknown option is a usage error.</summary>
    [Fact]
    public async Task UnknownOptionIsAUsageError()
    {
        using var fx = new Fixture();

        Run run = await RunAsync(fx.Root, "--list", "--bogus");

        Assert.Equal(2, run.Code);
        Assert.Contains("--bogus", run.Stderr, StringComparison.Ordinal);
    }

    /// <summary>A root that does not exist is a failure, reported on standard error.</summary>
    [Fact]
    public async Task MissingRootFails()
    {
        using var fx = new Fixture();

        Run run = await RunAsync(fx.PathTo("missing"), "--list");

        Assert.Equal(1, run.Code);
        Assert.StartsWith("Error: ", run.Stderr, StringComparison.Ordinal);
        Assert.Equal("", run.Stdout);
    }

    /// <summary>An empty root is a failure, reported on standard error.</summary>
    [Fact]
    public async Task EmptyRootFails()
    {
        Run run = await RunAsync("", "--list");

        Assert.Equal(1, run.Code);
        Assert.StartsWith("Error: ", run.Stderr, StringComparison.Ordinal);
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

        Run quiet = await RunAsync(fx.Root, "--list");
        Run debug = await RunWithEnvAsync(new Dictionary<string, string> { [LogVariable] = "debug" }, fx.Root, "--list");

        Assert.Equal(0, quiet.Code);
        Assert.DoesNotContain("debug:", quiet.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, debug.Code);
        Assert.Contains("debug: skipping file ", debug.Stderr, StringComparison.Ordinal);
        Assert.Equal(quiet.Stdout, debug.Stdout);
    }

    /// <summary>With no mode flag the window would open; until it exists that is a usage error naming the flags.</summary>
    [Fact]
    public async Task NoFlagSaysTheWindowIsNotBuilt()
    {
        using var fx = new Fixture();

        Run run = await RunAsync(fx.Root);

        Assert.Equal(2, run.Code);
        Assert.Equal("worktree-sweep: the window is not built yet; use --list or --json\n", run.Stderr);
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

        Run run = await RunAsync(fx.Root, "--json");

        Assert.Equal(0, run.Code);
        Assert.StartsWith("warning: ignoring the released marker", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(["registered"], Kinds(run.Stdout));
    }

    private static List<string?> Kinds(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.GetProperty("candidates").EnumerateArray().Select(candidate => candidate.GetProperty("kind").GetString())];
    }

    private static Task<Run> RunAsync(params string[] args) => RunWithEnvAsync(new Dictionary<string, string>(), args);

    /// <summary>
    /// Runs the exe with the repo-local git variables and <c>WORKTREE_SWEEP_LOG</c> cleared, then <paramref name="env"/> set, killing
    /// it when it outlives <see cref="RunTimeout"/> or the test run is cancelled.
    /// </summary>
    private static async Task<Run> RunWithEnvAsync(IReadOnlyDictionary<string, string> env, params string[] args)
    {
        var startInfo = new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        GitRunner.ClearRepoEnv(startInfo);
        startInfo.Environment.Remove(LogVariable);
        foreach (KeyValuePair<string, string> pair in env)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{Exe} did not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new Run(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            KillIfRunning(process);
            if (TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            throw new TimeoutException($"{Exe} {string.Join(' ', args)} did not exit within {RunTimeout}");
        }
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException error)
        {
            TestContext.Current.SendDiagnosticMessage($"{Exe} had already exited when it was to be killed: {error.Message}");
        }
    }

    private sealed record Run(int Code, string Stdout, string Stderr);
}
