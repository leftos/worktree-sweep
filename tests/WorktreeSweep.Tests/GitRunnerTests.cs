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
