using WorktreeSweep.Git;

namespace WorktreeSweep.Tests;

/// <summary>A git hook exports <c>GIT_DIR</c>, <c>GIT_INDEX_FILE</c> and the like to everything it starts.</summary>
[Collection(ProcessEnvironment.Name)]
public sealed class GitEnvTests
{
    /// <summary>The runner, under such an environment, still reads the repo it was pointed at.</summary>
    [Fact]
    public void GitRunnerIgnoresInheritedRepoEnv()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        File.WriteAllText(Path.Combine(wt, "README.md"), "changed\n");

        using var inherited = new InheritedEnv(
            new Dictionary<string, string> { ["GIT_DIR"] = fx.PathTo("nonexistent"), ["GIT_INDEX_FILE"] = ".git/index.lock" }
        );

        string top = GitRunner.Run(wt, ["rev-parse", "--show-toplevel"]);
        Assert.True(Fixture.SamePath(top, wt), $"toplevel {top}, worktree {wt}");
        Assert.Equal(" M README.md", GitRunner.RunRaw(wt, ["status", "--porcelain"]).TrimEnd('\n'));
        Assert.Equal("0", GitRunner.Run(wt, ["rev-list", "--count", "main..HEAD"]));
    }

    /// <summary>
    /// None of <see cref="GitRunner.RepoLocalEnvVars"/> reaches the child: each is set to a marked value in this process, and a
    /// shell alias run by the child git prints its environment without any of them.
    /// </summary>
    [Fact]
    public void RepoLocalEnvVarsAreRemovedFromTheChild()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        var marked = GitRunner.RepoLocalEnvVars.ToDictionary(name => name, name => $"inherited-{name}");

        using var inherited = new InheritedEnv(marked);

        GitStatus status = GitRunner.RunStatus(repo, ["-c", "alias.print-env=!env", "print-env"]);
        Assert.True(status.Success, $"git exited with code {status.Code}: {status.Stderr}");
        Assert.DoesNotContain("inherited-", status.Stdout, StringComparison.Ordinal);
    }
}
