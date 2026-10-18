using System.Diagnostics;
using WorktreeSweep.Git;

namespace WorktreeSweep.Tests;

/// <summary>How <see cref="GitRunner"/> reports a git command that exits non-zero.</summary>
public sealed class GitRunnerTests
{
    private static readonly string[] BadRef = ["rev-parse", "--verify", "no-such-ref"];

    /// <summary>A failing command's exception names the directory, the arguments, the exit code and git's standard error.</summary>
    [Fact]
    public void FailureNamesDirArgsAndStderr()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");

        GitException error = Assert.Throws<GitException>(() => GitRunner.Run(repo, BadRef, TestContext.Current.CancellationToken));

        Assert.Equal($"`git -C {repo} rev-parse --verify no-such-ref` exited with code 128: fatal: Needed a single revision", error.Message);
        Assert.Equal(error.Message, Assert.Throws<GitException>(() => GitRunner.RunRaw(repo, BadRef, TestContext.Current.CancellationToken)).Message);
    }

    /// <summary>Extra environment variables cannot turn optional locks back on: git still runs with <c>GIT_OPTIONAL_LOCKS=0</c>.</summary>
    [Fact]
    public void ExtraEnvCannotOverrideOptionalLocks()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");

        GitStatus status = GitRunner.RunStatusWithEnv(
            repo,
            ["-c", "alias.print-env=!env", "print-env"],
            new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "1" },
            TestContext.Current.CancellationToken
        );

        Assert.True(status.Success, $"git exited with code {status.Code}: {status.Stderr}");
        Assert.Contains("GIT_OPTIONAL_LOCKS=0", status.Stdout.Split('\n'));
    }

    /// <summary>
    /// Extra environment variables cannot turn git's prompts back on: git still runs with <c>GIT_TERMINAL_PROMPT=0</c> and
    /// <c>GCM_INTERACTIVE=never</c>.
    /// </summary>
    [Fact]
    public void PromptsAreDisabled()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");

        GitStatus status = GitRunner.RunStatusWithEnv(
            repo,
            ["-c", "alias.print-env=!env", "print-env"],
            new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "1", ["GCM_INTERACTIVE"] = "always" },
            TestContext.Current.CancellationToken
        );

        Assert.True(status.Success, $"git exited with code {status.Code}: {status.Stderr}");
        string[] lines = status.Stdout.Split('\n');
        Assert.Contains("GIT_TERMINAL_PROMPT=0", lines);
        Assert.Contains("GCM_INTERACTIVE=never", lines);
    }

    /// <summary>
    /// A git child that outlives the limit is killed with its whole process tree, so a grandchild holding the output pipes cannot
    /// block the call, and the exception names the command and the limit.
    /// </summary>
    [Fact]
    public void HungGitIsKilledAfterTheTimeout()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        var clock = Stopwatch.StartNew();

        GitTimeoutException error = Assert.Throws<GitTimeoutException>(() =>
            GitRunner.RunStatusWithEnv(
                repo,
                ["-c", "alias.hang=!sleep 30", "hang"],
                new Dictionary<string, string>(),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(15),
                TestContext.Current.CancellationToken
            )
        );

        clock.Stop();
        Assert.Equal($"`git -C {repo} -c alias.hang=!sleep 30 hang` did not exit within 1 s and was killed", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the call took {clock.Elapsed}");
    }

    /// <summary>
    /// A cancelled call kills git with its whole process tree, as a timeout does, and throws. The alias's shell opens a file and then
    /// loops on one-second sleeps, so the test cancels once the file exists, and the file is released only when the shell is gone; a
    /// sleep started as the tree is killed holds it at most a second longer.
    /// </summary>
    /// <returns>A task that completes when the test ends.</returns>
    [Fact]
    public async Task RunCancelledKillsGitTreeAndThrows()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string held = fx.PathTo("held");
        string alias = $"alias.hang=!exec 3> '{held.Replace('\\', '/')}'; while :; do sleep 1; done";
        using var cancel = new CancellationTokenSource();

        Task<GitStatus> run = Task.Run(
            () =>
                GitRunner.RunStatusWithEnv(
                    repo,
                    ["-c", alias, "hang"],
                    new Dictionary<string, string>(),
                    TimeSpan.FromMinutes(10),
                    TimeSpan.FromSeconds(15),
                    cancel.Token
                ),
            TestContext.Current.CancellationToken
        );
        Fixture.WaitUntil(() => File.Exists(held) || run.IsCompleted, "the alias to start sleeping");
        Assert.False(run.IsCompleted, "git ended before it was cancelled");
        await cancel.CancelAsync();

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal($"`git -C {repo} -c {alias} hang` was cancelled and was killed", error.Message);
        Assert.Equal(cancel.Token, error.CancellationToken);
        Fixture.WaitUntil(() => Fixture.IsReleased(held), "the killed alias to release its output file");
    }

    /// <summary>
    /// A cancel that arrives after the timeout has killed git ends the call as a cancel, without waiting out the grace for the
    /// output pipes. The alias first detaches a sleep that outlives the kill and holds the pipes, then opens a file and loops; the
    /// test waits for the file to be released, which is the kill, cancels, and waits for the call to end under a hang guard the
    /// detached sleep outlives. When the timeout fires before the alias has detached its sleep, the case cannot be exercised and is
    /// skipped; any other end before the alias starts fails the test.
    /// </summary>
    /// <returns>A task that completes when the test ends.</returns>
    [Fact]
    public async Task CancelAfterTimeoutEndsWithoutWaitingOutTheGrace()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string held = fx.PathTo("held");
        string alias = $"alias.hang=!(cd / && sleep 300 &); exec 3> '{held.Replace('\\', '/')}'; while :; do sleep 1; done";
        using var cancel = new CancellationTokenSource();

        Task<GitStatus> run = Task.Run(
            () =>
                GitRunner.RunStatusWithEnv(
                    repo,
                    ["-c", alias, "hang"],
                    new Dictionary<string, string>(),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMinutes(10),
                    cancel.Token
                ),
            TestContext.Current.CancellationToken
        );
        Fixture.WaitUntil(() => File.Exists(held) || run.IsCompleted, "the alias to start sleeping");
        if (run.IsCompleted)
        {
            if (run.Exception?.InnerException is GitTimeoutException)
            {
                Assert.Skip("the timeout killed git before the alias had detached its sleep, so no cancel can arrive after the timeout");
            }
            GitStatus? status = run.IsCompletedSuccessfully ? await run : null;
            string outcome = status is not null ? $"exit code {status.Code}: {status.Stderr}" : $"{run.Exception}";
            Assert.Fail($"the call ended before the alias started sleeping, with status {run.Status}: {outcome}");
        }
        Fixture.WaitUntil(() => Fixture.IsReleased(held) || run.IsCompleted, "the timeout to kill the alias");
        Assert.False(run.IsCompleted, "the call ended before it was cancelled");
        await cancel.CancelAsync();

        Fixture.WaitUntil(() => run.IsCompleted, "the cancelled call to end");
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal($"`git -C {repo} -c {alias} hang` was cancelled and was killed", error.Message);
        Assert.Equal(cancel.Token, error.CancellationToken);
    }

    /// <summary>
    /// A git that exits while a process it started still holds its output open is reported once the grace runs out, instead of
    /// waiting for that process to end.
    /// </summary>
    [Fact]
    public void OutputHeldOpenAfterExitIsReported()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        var clock = Stopwatch.StartNew();

        GitException error = Assert.Throws<GitException>(() =>
            GitRunner.RunStatusWithEnv(
                repo,
                ["-c", "alias.bg=!cd / && sleep 30 &", "bg"],
                new Dictionary<string, string>(),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken
            )
        );

        clock.Stop();
        Assert.Equal($"`git -C {repo} -c alias.bg=!cd / && sleep 30 & bg` exited but its output stayed open for 1 s", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the call took {clock.Elapsed}");
    }

    /// <summary><see cref="GitRunner.RunStatus"/> returns a non-zero exit code and the trimmed standard error without throwing.</summary>
    [Fact]
    public void RunStatusReturnsNonZeroCodeWithoutThrowing()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");

        GitStatus status = GitRunner.RunStatus(repo, BadRef, TestContext.Current.CancellationToken);

        Assert.False(status.Success);
        Assert.Equal(128, status.Code);
        Assert.Equal("", status.Stdout);
        Assert.Equal("fatal: Needed a single revision", status.Stderr);
    }
}
