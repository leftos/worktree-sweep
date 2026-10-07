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

        GitException error = Assert.Throws<GitException>(() => GitRunner.Run(repo, BadRef));

        Assert.Equal($"`git -C {repo} rev-parse --verify no-such-ref` exited with code 128: fatal: Needed a single revision", error.Message);
        Assert.Equal(error.Message, Assert.Throws<GitException>(() => GitRunner.RunRaw(repo, BadRef)).Message);
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
            new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "1" }
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
            new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "1", ["GCM_INTERACTIVE"] = "always" }
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
                TimeSpan.FromSeconds(15)
            )
        );

        clock.Stop();
        Assert.Equal($"`git -C {repo} -c alias.hang=!sleep 30 hang` did not exit within 1 s and was killed", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the call took {clock.Elapsed}");
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
                TimeSpan.FromSeconds(1)
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

        GitStatus status = GitRunner.RunStatus(repo, BadRef);

        Assert.False(status.Success);
        Assert.Equal(128, status.Code);
        Assert.Equal("", status.Stdout);
        Assert.Equal("fatal: Needed a single revision", status.Stderr);
    }
}
