using System.Globalization;
using WorktreeSweep.Discovery;
using WorktreeSweep.Git;
using WorktreeSweep.Report;

namespace WorktreeSweep.Tests;

/// <summary>Finding a worktree's admin dir and reading the released marker in it.</summary>
public sealed class ReleasedMarkerTests
{
    private const string Marker = """{"released_at": 1790000000, "reason": "locked", "holders": []}""";

    /// <summary>A marker in the worktree's admin dir is read with its time, reason and holders.</summary>
    [Fact]
    public void MarkerInAdminDirMarksWorktreeReleased()
    {
        using var fx = new Fixture();
        (string Worktree, string Admin) wt = WorktreeWithAdmin(fx);
        File.WriteAllText(
            Path.Join(wt.Admin, ReleasedMarker.FileName),
            """{"released_at": 1790000000, "reason": "locked", "holders": [{"pid": 42, "exe": "devenv.exe"}]}"""
        );

        string? admin = ReleasedMarker.AdminDir(null, wt.Worktree);
        Assert.NotNull(admin);
        Released? released = ReleasedMarker.Read(admin);

        Assert.NotNull(released);
        Assert.Equal(Reason.Locked, released.Reason);
        Assert.Equal(1_790_000_000, released.ReleasedAtUnix);
        Assert.Equal(new ProcessRef { Pid = 42, Exe = "devenv.exe" }, Assert.Single(released.Holders));
        Assert.Equal(Reason.Locked, ReleasedMarker.Find(null, wt.Worktree)?.Reason);
    }

    /// <summary>An admin dir without a marker reads as not released.</summary>
    [Fact]
    public void WorktreeWithoutMarkerIsNotReleased()
    {
        using var fx = new Fixture();
        (string Worktree, string Admin) wt = WorktreeWithAdmin(fx);

        string? admin = ReleasedMarker.AdminDir(null, wt.Worktree);

        Assert.NotNull(admin);
        Assert.Null(ReleasedMarker.Read(admin));
    }

    /// <summary>A marker that is not JSON, lacks a field, names an unknown reason or a time out of range reads as not released.</summary>
    /// <param name="text">The marker file's content.</param>
    [Theory]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("""{"released_at": 1790000000, "reason": "frozen", "holders": []}""")]
    [InlineData("""{"released_at": 1790000000, "reason": "locked"}""")]
    [InlineData("""{"released_at": 1790000000, "reason": "locked", "holders": null}""")]
    [InlineData("""{"released_at": 999999999999999, "reason": "locked", "holders": []}""")]
    public void MalformedMarkerIsIgnored(string text)
    {
        using var fx = new Fixture();
        (string Worktree, string Admin) wt = WorktreeWithAdmin(fx);
        File.WriteAllText(Path.Join(wt.Admin, ReleasedMarker.FileName), text);

        Assert.Null(ReleasedMarker.Read(wt.Admin));
    }

    /// <summary>A refusal reason written untagged reads as <see cref="ReasonKind.NotRemovable"/> carrying it.</summary>
    [Fact]
    public void RefusalReasonReadsAsNotRemovable()
    {
        using var fx = new Fixture();
        (string Worktree, string Admin) wt = WorktreeWithAdmin(fx);
        File.WriteAllText(Path.Join(wt.Admin, ReleasedMarker.FileName), """{"released_at": 1790000000, "reason": "not_a_worktree", "holders": []}""");

        Released? released = ReleasedMarker.Read(wt.Admin);

        Assert.NotNull(released);
        Assert.Equal(Reason.NotRemovable(RefusalReason.NotAWorktree), released.Reason);
        Assert.Equal(ReasonKind.NotRemovable, released.Reason.Kind);
    }

    /// <summary>A worktree whose folder is gone is found through the common dir's <c>worktrees</c> entries, marker and all.</summary>
    [Fact]
    public void PrunableWorktreeKeepsItsMarker()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        Fixture.AddWorktree(repo, fx.PathTo("repo.wt/feat"), "feat");
        string worktree = MarkAndDelete(repo, fx.PathTo("repo.wt/feat"));

        Assert.Null(ReleasedMarker.AdminDir(null, worktree));
        string? admin = ReleasedMarker.AdminDir(Path.Join(repo, ".git"), worktree);
        Assert.NotNull(admin);
        Assert.NotNull(ReleasedMarker.Read(admin));
    }

    /// <summary>A worktree path written with a <c>.\</c> segment matches its entry once the segment folds.</summary>
    [Fact]
    public void WorktreePathWithDotSegmentFindsItsAdminDir()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        Fixture.AddWorktree(repo, fx.PathTo("repo.wt/feat"), "feat");
        string worktree = MarkAndDelete(repo, fx.PathTo("repo.wt/feat"));
        string dotted = Path.Join(Path.GetDirectoryName(worktree), ".", Path.GetFileName(worktree));

        string? admin = ReleasedMarker.AdminDir(Path.Join(repo, ".git"), dotted);

        Assert.NotNull(admin);
        Assert.NotNull(ReleasedMarker.Read(admin));
    }

    /// <summary>A relative common dir is refused rather than resolved against the current directory.</summary>
    [Fact]
    public void RelativeCommonDirIsRefused() => Assert.Throws<ArgumentException>(() => ReleasedMarker.AdminDir(".git", @"C:\nowhere\feat"));

    /// <summary>A <c>gitdir</c> file holding a relative path (<c>worktree.useRelativePaths</c>) is resolved against its entry.</summary>
    [Fact]
    public void PrunableWorktreeWithRelativePathsKeepsItsMarker()
    {
        using var fx = new Fixture();
        Assert.SkipUnless(GitHasRelativeWorktrees(fx.Root), "the installed git has no worktree.useRelativePaths (needs 2.48 or later)");
        string repo = fx.Repo("repo");
        string path = fx.PathTo("repo.wt/feat");
        Fixture.Git(repo, ["-c", "worktree.useRelativePaths=true", "worktree", "add", "-q", "-b", "feat", path]);
        string entry = Discoverer.ReadGitdirFile(path) ?? throw new InvalidOperationException($"{path} has no admin dir");
        Assert.False(Path.IsPathFullyQualified(File.ReadAllText(Path.Join(entry, "gitdir")).Trim()), "gitdir is not relative");
        string worktree = MarkAndDelete(repo, path);

        string? admin = ReleasedMarker.AdminDir(Path.Join(repo, ".git"), worktree);

        Assert.NotNull(admin);
        Assert.NotNull(ReleasedMarker.Read(admin));
    }

    /// <summary>A bare repo is its own common dir; its prunable worktree's marker is found there.</summary>
    [Fact]
    public void PrunableWorktreeOfBareRepoKeepsItsMarker()
    {
        using var fx = new Fixture();
        string source = fx.Repo("source");
        string bare = fx.PathTo("bare");
        Fixture.Git(fx.Root, ["clone", "-q", "--bare", source, bare]);
        Fixture.AddWorktree(bare, fx.PathTo("bare.wt/feat"), "feat");
        string worktree = MarkAndDelete(bare, fx.PathTo("bare.wt/feat"));

        string? admin = ReleasedMarker.AdminDir(bare, worktree);

        Assert.NotNull(admin);
        Assert.NotNull(ReleasedMarker.Read(admin));
    }

    private static (string Worktree, string Admin) WorktreeWithAdmin(Fixture fx)
    {
        string repo = fx.Repo("repo");
        string worktree = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, worktree, "feat");
        string admin = Discoverer.ReadGitdirFile(worktree) ?? throw new InvalidOperationException($"{worktree} has no admin dir");
        return (worktree, admin);
    }

    /// <summary>
    /// Writes <see cref="Marker"/> into the worktree's admin dir and deletes the worktree folder, then checks git calls the registration
    /// prunable.
    /// </summary>
    /// <returns>The worktree's path as git lists it.</returns>
    private static string MarkAndDelete(string repo, string worktree)
    {
        string admin = Discoverer.ReadGitdirFile(worktree) ?? throw new InvalidOperationException($"{worktree} has no admin dir");
        File.WriteAllText(Path.Join(admin, ReleasedMarker.FileName), Marker);
        Directory.Delete(worktree, recursive: true);
        WorktreeRecord record = Assert.Single(Discoverer.ListWorktrees(repo), candidate => Fixture.SamePath(candidate.Path, worktree));
        Assert.NotNull(record.Prunable);
        return record.Path;
    }

    private static bool GitHasRelativeWorktrees(string dir)
    {
        string[] numbers = GitRunner.Run(dir, ["--version"]).Split(' ')[2].Split('.');
        (int Major, int Minor) version = (int.Parse(numbers[0], CultureInfo.InvariantCulture), int.Parse(numbers[1], CultureInfo.InvariantCulture));
        return version.CompareTo((2, 48)) >= 0;
    }
}
