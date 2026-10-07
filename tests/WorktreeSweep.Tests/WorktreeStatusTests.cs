using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>A worktree's uncommitted work and the state of its branch's upstream.</summary>
public sealed class WorktreeStatusTests
{
    /// <summary>Staged and unstaged changes count as modified, each new file as untracked.</summary>
    [Fact]
    public void DirtyCountsModifiedAndUntracked()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        File.WriteAllText(Path.Join(wt, "README.md"), "changed\n");
        File.WriteAllText(Path.Join(wt, "staged.txt"), "staged\n");
        _ = Fixture.Git(wt, ["add", "staged.txt"]);
        File.WriteAllText(Path.Join(wt, "new-1.txt"), "1\n");
        File.WriteAllText(Path.Join(wt, "new-2.txt"), "2\n");

        Assert.Equal(new Dirty { Modified = 2, Untracked = 2 }, fx.Registered(wt).Signals.Dirty);
    }

    /// <summary>Ignored files and folders are not uncommitted work.</summary>
    [Fact]
    public void IgnoredFilesAreNotDirty()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        _ = Fixture.CommitFile(repo, ".gitignore", "*.log\nbuild/\n", "ignore logs");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        File.WriteAllText(Path.Join(wt, "debug.log"), "log\n");
        _ = Directory.CreateDirectory(Path.Join(wt, "build"));
        File.WriteAllText(Path.Join(wt, "build", "out.bin"), "bin");

        Assert.Equal(new Dirty(), fx.Registered(wt).Signals.Dirty);
    }

    /// <summary>A rename or copy entry is one modified entry, and the source path field after it is not counted.</summary>
    /// <param name="text">The <c>git status --porcelain=v1 -z</c> output.</param>
    [Theory]
    [InlineData("R  new\0old\0?? u\0")]
    [InlineData("C  new\0old\0?? u\0")]
    public void RenameAndCopyConsumeTheirSourceField(string text) =>
        Assert.Equal(new Dirty { Modified = 1, Untracked = 1 }, SignalReader.ParseStatusZ(text));

    /// <summary>An upstream branch deleted from the remote is gone.</summary>
    [Fact]
    public void UpstreamGoneIsReported()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        _ = fx.Origin(repo);
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        _ = Fixture.CommitFile(wt, "b.txt", "b\n", "b");
        _ = Fixture.Git(wt, ["push", "-q", "-u", "origin", "feat"]);
        _ = Fixture.Git(wt, ["push", "-q", "origin", "--delete", "feat"]);

        Assert.Equal(Upstream.Gone, fx.Registered(wt).Signals.Upstream);
    }

    /// <summary>Commits made after the last push are counted as ahead of the upstream.</summary>
    [Fact]
    public void UnpushedCommitsAreCounted()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        _ = fx.Origin(repo);
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        _ = Fixture.CommitFile(wt, "b.txt", "b\n", "b");
        _ = Fixture.Git(wt, ["push", "-q", "-u", "origin", "feat"]);
        _ = Fixture.CommitFile(wt, "c.txt", "c\n", "c");
        _ = Fixture.CommitFile(wt, "d.txt", "d\n", "d");

        Assert.Equal(Upstream.Tracking(2), fx.Registered(wt).Signals.Upstream);
    }
}
