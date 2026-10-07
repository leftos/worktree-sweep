using WorktreeSweep.Agent;
using WorktreeSweep.Git;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>How a path an agent asks to remove resolves to a registered linked worktree, or why it is refused.</summary>
public sealed class AgentResolveTests
{
    /// <summary>A path with nothing on disk that no repo registers is not found, beside a repo's container too.</summary>
    [Fact]
    public void ResolveRefusesMissingPath()
    {
        using var fx = new Fixture();
        _ = RepoWithWorktree(fx);

        Assert.Equal(RefusalReason.NotFound, Refusal(fx.PathTo(@"nothing\here")));
        Assert.Equal(RefusalReason.NotFound, Refusal(fx.PathTo(@"x.wt\other")));
    }

    /// <summary>A folder in no repo is not a worktree.</summary>
    [Fact]
    public void ResolveRefusesPlainFolder()
    {
        using var fx = new Fixture();
        string plain = fx.PathTo("plain");
        Directory.CreateDirectory(plain);

        Assert.Equal(RefusalReason.NotAWorktree, Refusal(plain));
    }

    /// <summary>A repo's main worktree is refused.</summary>
    [Fact]
    public void ResolveRefusesMainWorktree()
    {
        using var fx = new Fixture();
        (string repo, _) = RepoWithWorktree(fx);

        Assert.Equal(RefusalReason.MainWorktree, Refusal(repo));
    }

    /// <summary>A bare repo is refused.</summary>
    [Fact]
    public void ResolveRefusesBareRepo()
    {
        using var fx = new Fixture();
        string bare = fx.PathTo("bare.git");
        _ = Fixture.Git(fx.Root, ["init", "-q", "--bare", bare]);

        Assert.Equal(RefusalReason.BareRepo, Refusal(bare));
    }

    /// <summary>A folder inside a linked worktree is refused as a subfolder.</summary>
    [Fact]
    public void ResolveRefusesSubfolder()
    {
        using var fx = new Fixture();
        (_, string worktree) = RepoWithWorktree(fx);
        string sub = Path.Join(worktree, "sub");
        Directory.CreateDirectory(sub);

        Assert.Equal(RefusalReason.Subfolder, Refusal(sub));
    }

    /// <summary>A junction to a linked worktree is refused as a link.</summary>
    [Fact]
    public void ResolveRefusesLink()
    {
        using var fx = new Fixture();
        (_, string worktree) = RepoWithWorktree(fx);
        string link = fx.PathTo("link");
        Assert.SkipUnless(Fixture.MakeJunction(link, worktree), "mklink /J is unavailable");

        Assert.Equal(RefusalReason.Link, Refusal(link));
    }

    /// <summary>A folder whose <c>.git</c> file points at a git dir that is gone is an orphan.</summary>
    [Fact]
    public void ResolveRefusesOrphan()
    {
        using var fx = new Fixture();
        _ = RepoWithWorktree(fx);
        string orphan = fx.PathTo(@"x.wt\gone");
        Directory.CreateDirectory(orphan);
        string missing = fx.PathTo(@"x\.git\worktrees\gone");
        File.WriteAllText(Path.Join(orphan, ".git"), $"gitdir: {missing}\n");

        Assert.Equal(RefusalReason.Orphan, Refusal(orphan));
    }

    /// <summary>A linked worktree resolves to its candidate, with its repo, branch and merge state.</summary>
    [Fact]
    public void ResolveFindsLinkedWorktree()
    {
        using var fx = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx);
        _ = Fixture.CommitFile(worktree, "work.txt", "work\n", "work");

        Resolution.Resolved found = Resolved(worktree);

        RegisteredCandidate candidate = found.Candidate;
        Assert.True(Fixture.SamePath(candidate.Path, worktree), candidate.Path);
        Assert.True(Fixture.SamePath(candidate.Repo, repo), candidate.Repo);
        Assert.True(Fixture.SamePath(found.MainWorktree, repo), found.MainWorktree);
        Assert.Equal("feat", candidate.Record.Branch);
        Assert.Null(candidate.Record.Prunable);
        Assert.Equal(MergeState.Unmerged(1), candidate.Signals.MergeState);
    }

    /// <summary>A registered worktree whose folder is gone resolves as prunable, its merge state read from the main worktree.</summary>
    [Fact]
    public void ResolveFindsPrunableRecord()
    {
        using var fx = new Fixture();
        (_, string worktree) = RepoWithWorktree(fx);
        Directory.Delete(worktree, recursive: true);

        RegisteredCandidate candidate = Resolved(worktree).Candidate;

        Assert.NotNull(candidate.Record.Prunable);
        Assert.Equal("feat", candidate.Record.Branch);
        Assert.Equal(MergeState.NoCommits, candidate.Signals.MergeState);
    }

    /// <summary>
    /// A missing path two levels under a repo's <c>{repo}.wt</c> container, whose nearest existing ancestor is the container, resolves
    /// to the prunable record the repo beside the container registers there.
    /// </summary>
    [Fact]
    public void ResolveFindsPrunableRecordUnderAContainerSibling()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("x");
        string worktree = fx.PathTo(@"x.wt\group\feat");
        Fixture.AddWorktree(repo, worktree, "feat");
        Directory.Delete(fx.PathTo(@"x.wt\group"), recursive: true);

        Resolution.Resolved found = Resolved(worktree);

        Assert.True(Fixture.SamePath(found.Candidate.Path, worktree), found.Candidate.Path);
        Assert.True(Fixture.SamePath(found.MainWorktree, repo), found.MainWorktree);
        Assert.NotNull(found.Candidate.Record.Prunable);
        Assert.Equal(MergeState.NoCommits, found.Candidate.Signals.MergeState);
    }

    /// <summary>
    /// A missing path whose repo's worktree list times out throws instead of being reported not found: every repo near the path
    /// sits on the same volume, so none could be read.
    /// </summary>
    [Fact]
    public void MissingPathOnAStalledVolumeThrowsInsteadOfNotFound()
    {
        using var fx = new Fixture();
        (_, string worktree) = RepoWithWorktree(fx);
        Directory.Delete(worktree, recursive: true);

        _ = Assert.Throws<GitTimeoutException>(() =>
            PathResolution.ResolveOne(worktree, new VolumeStalls(), _ => throw new GitTimeoutException("worktree list timed out"))
        );
    }

    /// <summary>
    /// A worktree folder whose <c>.git</c> file is gone is prunable to git but still on disk: it is refused as an orphan, as no live
    /// registration backs the folder.
    /// </summary>
    [Fact]
    public void PrunableRecordWhoseFolderRemainsIsRefused()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("x");
        string worktree = fx.PathTo(@"x\.claude\worktrees\a");
        Fixture.AddWorktree(repo, worktree, "a");
        File.Delete(Path.Join(worktree, ".git"));

        Assert.Equal(RefusalReason.Orphan, Refusal(worktree));
    }

    /// <summary>
    /// A worktree registered beside its repo (<c>x-feature</c> next to <c>x</c>), its folder deleted, has the fixture root as its
    /// nearest existing ancestor, so its record is found through the root's child repos.
    /// </summary>
    [Fact]
    public void MissingSiblingWorktreeResolvesThroughTheParentsChildRepos()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("x");
        string worktree = fx.PathTo("x-feature");
        Fixture.AddWorktree(repo, worktree, "feature");
        Directory.Delete(worktree, recursive: true);

        Resolution.Resolved found = Resolved(worktree);

        Assert.True(Fixture.SamePath(found.Candidate.Path, worktree), found.Candidate.Path);
        Assert.True(Fixture.SamePath(found.MainWorktree, repo), found.MainWorktree);
        Assert.NotNull(found.Candidate.Record.Prunable);
        Assert.Equal("feature", found.Candidate.Record.Branch);
        Assert.Equal(MergeState.NoCommits, found.Candidate.Signals.MergeState);
    }

    /// <summary>A repo <c>x</c> under the fixture root with a clean worktree at <c>x.wt\feat</c> on a new branch <c>feat</c>.</summary>
    private static (string Repo, string Worktree) RepoWithWorktree(Fixture fx)
    {
        string repo = fx.Repo("x");
        string worktree = fx.PathTo(@"x.wt\feat");
        Fixture.AddWorktree(repo, worktree, "feat");
        return (repo, worktree);
    }

    private static RefusalReason Refusal(string path) =>
        PathResolution.ResolveOne(path) switch
        {
            Resolution.Refusal refusal => refusal.Reason,
            var other => throw new Xunit.Sdk.XunitException($"{path} resolved to {other}, expected a refusal"),
        };

    private static Resolution.Resolved Resolved(string path) =>
        PathResolution.ResolveOne(path) switch
        {
            Resolution.Resolved resolved => resolved,
            var other => throw new Xunit.Sdk.XunitException($"{path} was refused: {other}"),
        };
}
