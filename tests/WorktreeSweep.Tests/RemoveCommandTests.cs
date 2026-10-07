using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorktreeSweep.Agent;
using WorktreeSweep.Git;
using WorktreeSweep.Report;

namespace WorktreeSweep.Tests;

/// <summary>The built exe's <c>remove</c> subcommand: its report, its exit codes and what it leaves on disk.</summary>
public sealed class RemoveCommandTests
{
    private const string LogVariable = "WORKTREE_SWEEP_LOG";

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    private static readonly string Exe = Path.Join(AppContext.BaseDirectory, "worktree-sweep.exe");

    /// <summary>Each status has its exit code: 0 removed, 5 released, 6 refused.</summary>
    [Fact]
    public void RemoveExitCodeMapsEachStatus()
    {
        Assert.Equal(0, RemoveExitCode.For(RemoveStatus.Removed));
        Assert.Equal(5, RemoveExitCode.For(RemoveStatus.Released));
        Assert.Equal(6, RemoveExitCode.For(RemoveStatus.Refused));
    }

    /// <summary>A repo's main worktree is refused with exit 6.</summary>
    [Fact]
    public async Task RemoveRefusesMainWorktree()
    {
        using var fx = new Fixture();
        (string repo, _) = RepoWithWorktree(fx, @"x.wt\feat", "feat");

        Run run = await RemoveAsync(repo, fx.Root);

        AssertExit(6, run);
        Assert.Equal("refused", run.Text("status"));
        Assert.Equal("main_worktree", run.Text("reason"));
    }

    /// <summary>A worktree with an untracked file is refused with exit 6 and its loss, and the file stays.</summary>
    [Fact]
    public async Task RemoveRefusesDirtyWithoutForce()
    {
        using var fx = new Fixture();
        (_, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");
        File.WriteAllText(Path.Join(worktree, "scratch.txt"), "unsaved\n");

        Run run = await RemoveAsync(worktree, fx.Root);

        AssertExit(6, run);
        Assert.Equal("refused", run.Text("status"));
        Assert.Equal("would_lose", run.Text("reason"));
        string? loss = run.Text("loss");
        Assert.NotNull(loss);
        Assert.Contains("1 untracked file", loss, StringComparison.Ordinal);
        Assert.EndsWith("will be lost.", loss, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(worktree, "scratch.txt")), "the worktree was touched");
    }

    /// <summary>
    /// A caller whose current folder is inside the worktree, here the <c>pwsh</c> that runs the exe, is told to change folder to the
    /// main worktree, and is listed as a holder.
    /// </summary>
    [Fact]
    public async Task RemoveReportsCallerHolds()
    {
        using var fx = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");
        string script = $"& {ReadyChild.Quoted(Exe)} remove {ReadyChild.Quoted(worktree)} --json; exit $LASTEXITCODE";

        Run run = await RunInAsync(worktree, "pwsh", "-NoProfile", "-NonInteractive", "-Command", script);

        AssertExit(6, run);
        Assert.Equal("caller_holds", run.Text("reason"));
        string? cdTo = run.Text("cd_to");
        Assert.NotNull(cdTo);
        Assert.True(Fixture.SamePath(cdTo, repo), $"cd_to {cdTo} is not {repo}");
        Assert.NotEmpty(run.Pids("holders"));
        Assert.True(File.Exists(Path.Join(worktree, "README.md")), "the worktree was touched");
    }

    /// <summary>
    /// A worktree whose file is held open, and ignored so git does not call it modified, is released with exit 5: the holder is
    /// listed, and the released marker names it. Reaches the Shell's recycle on a locked fixture; run by hand.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task RemoveReleasesLockedWorktree()
    {
        using var fx = new Fixture();
        using var side = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");
        string info = Path.Join(repo, ".git", "info");
        Directory.CreateDirectory(info);
        File.WriteAllText(Path.Join(info, "exclude"), "held.txt\n");
        string held = Path.Join(worktree, "held.txt");
        File.WriteAllText(held, "held\n");
        string ready = side.PathTo("ready");
        string script =
            $"$f = [IO.File]::Open({ReadyChild.Quoted(held)}, 'Open', 'Read', 'None'); "
            + $"Set-Content -LiteralPath {ReadyChild.Quoted(ready)} ready; Start-Sleep 120";
        using var holder = ReadyChild.Run(side.Root, ready, script);

        Run run = await RemoveAsync(worktree, fx.Root);

        AssertExit(5, run);
        Assert.Equal("released", run.Text("status"));
        Assert.Equal("locked", run.Text("reason"));
        Assert.Contains(holder.Id, run.Pids("holders"));
        JsonNode released = run.Json["released"] ?? throw new InvalidOperationException($"no released record: {run.Stdout}");
        Assert.Equal("locked", released["reason"]?.GetValue<string>());
        Assert.Contains(holder.Id, Pids(released["holders"]));
        string markerPath = Path.Join(repo, ".git", "worktrees", "feat", ReleasedMarker.FileName);
        JsonNode marker = JsonNode.Parse(File.ReadAllText(markerPath)) ?? throw new InvalidOperationException($"{markerPath} is null");
        Assert.Equal("locked", marker["reason"]?.GetValue<string>());
        Assert.Equal(JsonValueKind.Number, marker["released_at"]?.GetValueKind());
        Assert.Contains(holder.Id, Pids(marker["holders"]));
        Assert.True(File.Exists(Path.Join(worktree, "README.md")), "part of the worktree was recycled");
    }

    /// <summary>A registered worktree whose folder is gone is pruned with exit 0, and its branch deleted.</summary>
    [Fact]
    public async Task RemovePrunableRecord()
    {
        using var fx = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");
        Directory.Delete(worktree, recursive: true);

        Run run = await RemoveAsync(worktree, fx.Root);

        AssertExit(0, run);
        Assert.Equal("removed", run.Text("status"));
        Assert.True(run.Json["branch_deleted"]?.GetValue<bool>(), run.Stdout);
        Assert.DoesNotContain("feat", WorktreeList(repo), StringComparison.Ordinal);
        Assert.False(BranchExists(repo, "feat"), "branch feat still there");
    }

    /// <summary>A worktree beside its repo (<c>x-feat</c> next to <c>x</c>), its folder deleted, is found through the sibling repo.</summary>
    [Fact]
    public async Task RemovePrunableSiblingWorktree()
    {
        using var fx = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx, "x-feat", "feat");
        Directory.Delete(worktree, recursive: true);

        Run run = await RemoveAsync(worktree, fx.Root);

        AssertExit(0, run);
        Assert.Equal("removed", run.Text("status"));
        Assert.True(run.Json["branch_deleted"]?.GetValue<bool>(), run.Stdout);
        Assert.DoesNotContain("x-feat", WorktreeList(repo), StringComparison.Ordinal);
        Assert.False(BranchExists(repo, "feat"), "branch feat still there");
    }

    /// <summary>
    /// A worktree folder whose <c>.git</c> file is gone is prunable to git but still on disk: it is refused as an orphan, and neither
    /// the record nor the branch is touched.
    /// </summary>
    [Fact]
    public async Task RemoveRefusesPrunableRecordWhoseFolderRemains()
    {
        using var fx = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx, @"x\.claude\worktrees\a", "a");
        File.Delete(Path.Join(worktree, ".git"));

        Run run = await RemoveAsync(worktree, fx.Root);

        AssertExit(6, run);
        Assert.Equal("refused", run.Text("status"));
        Assert.Equal("orphan", run.Text("reason"));
        Assert.True(File.Exists(Path.Join(worktree, "README.md")), "the folder was touched");
        Assert.Contains(".claude/worktrees/a", WorktreeList(repo), StringComparison.Ordinal);
        Assert.True(BranchExists(repo, "a"), "branch a was deleted");
    }

    /// <summary>A clean worktree goes to the Recycle Bin with exit 0, its record pruned and its branch deleted. Run by hand.</summary>
    [Fact(Explicit = true)]
    public async Task RemoveRecyclesCleanWorktree()
    {
        using var fx = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");

        Run run = await RemoveAsync(worktree, fx.Root);

        AssertExit(0, run);
        Assert.Equal("removed", run.Text("status"));
        Assert.False(Directory.Exists(worktree), $"{worktree} still there");
        Assert.DoesNotContain("feat", WorktreeList(repo), StringComparison.Ordinal);
        Assert.False(BranchExists(repo, "feat"), "branch feat still there");
    }

    /// <summary><c>remove</c> without <c>--json</c> is a usage error: exit 2, an error on standard error, nothing on standard output.</summary>
    [Fact]
    public async Task RemoveWithoutJsonIsAUsageError()
    {
        using var fx = new Fixture();

        Run run = await RunInAsync(fx.Root, Exe, "remove", fx.PathTo(@"x.wt\feat"));

        Assert.Equal(2, run.Code);
        Assert.StartsWith("error: ", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("--json", run.Stderr, StringComparison.Ordinal);
        Assert.Equal("", run.Stdout);
    }

    /// <summary>A removal that fails, here on an empty path, exits 1 with its message on standard error and nothing on standard output.</summary>
    [Fact]
    public async Task RemoveFailureWritesOnlyStderr()
    {
        using var fx = new Fixture();

        Run run = await RemoveAsync("", fx.Root);

        Assert.Equal(1, run.Code);
        Assert.StartsWith("error: ", run.Stderr, StringComparison.Ordinal);
        Assert.Equal("", run.Stdout);
    }

    private static (string Repo, string Worktree) RepoWithWorktree(Fixture fx, string relative, string branch)
    {
        string repo = fx.Repo("x");
        string worktree = fx.PathTo(relative);
        Fixture.AddWorktree(repo, worktree, branch);
        return (repo, worktree);
    }

    private static string WorktreeList(string repo) => Fixture.Git(repo, ["worktree", "list", "--porcelain"]);

    private static bool BranchExists(string repo, string branch) => Fixture.Git(repo, ["branch", "--list", branch]).Length > 0;

    private static List<int> Pids(JsonNode? items) =>
        [.. (items?.AsArray() ?? []).Select(item => item?["pid"]?.GetValue<int>() ?? throw new InvalidOperationException($"no pid in {item}"))];

    private static void AssertExit(int expected, Run run)
    {
        if (run.Code != expected)
        {
            Assert.Fail($"exit {run.Code}, expected {expected}\nstdout: {run.Stdout}\nstderr: {run.Stderr}");
        }
    }

    /// <summary>Runs <c>remove PATH --json</c> in <paramref name="workingDirectory"/>.</summary>
    private static Task<Run> RemoveAsync(string path, string workingDirectory) => RunInAsync(workingDirectory, Exe, "remove", path, "--json");

    /// <summary>
    /// Runs <paramref name="program"/> in <paramref name="workingDirectory"/> with the repo-local git variables and
    /// <c>WORKTREE_SWEEP_LOG</c> cleared, killing it when it outlives <see cref="RunTimeout"/> or the test run is cancelled.
    /// </summary>
    private static async Task<Run> RunInAsync(string workingDirectory, string program, params string[] args)
    {
        var startInfo = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory,
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
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{program} did not start");
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
            KillIfRunning(process, program);
            if (TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            throw new TimeoutException($"{program} {string.Join(' ', args)} did not exit within {RunTimeout}");
        }
    }

    private static void KillIfRunning(Process process, string program)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException error)
        {
            TestContext.Current.SendDiagnosticMessage($"{program} had already exited when it was to be killed: {error.Message}");
        }
    }

    /// <summary>One run's exit code and output; <see cref="Json"/> parses standard output as the report.</summary>
    private sealed record Run(int Code, string Stdout, string Stderr)
    {
        public JsonNode Json => JsonNode.Parse(Stdout) ?? throw new InvalidOperationException($"stdout is JSON null; stderr: {Stderr}");

        public string? Text(string key) => Json[key]?.GetValue<string>();

        public List<int> Pids(string key) => RemoveCommandTests.Pids(Json[key]);
    }
}
