using System.Globalization;
using WorktreeSweep.Discovery;
using WorktreeSweep.Git;
using WorktreeSweep.Report;

namespace WorktreeSweep.Tests;

/// <summary>The scan: which registered worktrees and orphans become candidates, and the released markers it reads for them.</summary>
public sealed class ScanTests
{
    private const string Marker = """{"released_at": 1790000000, "reason": "locked", "holders": []}""";

    /// <summary>A linked worktree and an orphan folder are candidates; the repo's main worktree is not.</summary>
    [Fact]
    public void LinkedWorktreeAndOrphanAreCandidatesButMainWorktreeIsNot()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string worktree = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, worktree, "feat");
        string orphan = fx.PathTo("repo.wt/stray");
        Directory.CreateDirectory(orphan);

        ScanReport report = fx.Scan();

        Assert.Equal(2, report.Candidates.Count);
        Assert.Single(report.Repos, entry => Fixture.SamePath(entry.Path, repo));
        Assert.Single(report.Candidates.OfType<RegisteredCandidate>(), candidate => Fixture.SamePath(candidate.Path, worktree));
        Assert.Single(report.Candidates.OfType<OrphanCandidate>(), candidate => Fixture.SamePath(candidate.Path, orphan));
        Assert.DoesNotContain(report.Candidates, candidate => Fixture.SamePath(candidate.Path, repo));
    }

    /// <summary>A registration whose folder is gone is a candidate whose signals are not read, and not an orphan.</summary>
    [Fact]
    public void PrunableRegistrationIsACandidate()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string worktree = fx.PathTo("repo.wt/gone");
        Fixture.AddWorktree(repo, worktree, "gone");
        Directory.Delete(worktree, recursive: true);

        ScanReport report = fx.Scan();

        RegisteredCandidate candidate = Registered(report, worktree);
        Assert.NotNull(candidate.Record.Prunable);
        Assert.Null(candidate.Signals.MergeState);
        Assert.Null(candidate.Signals.Size);
        Assert.Empty(report.Candidates.OfType<OrphanCandidate>());
    }

    /// <summary>A marker in the worktree's admin dir marks its candidate released, with the marker's time, reason and holders.</summary>
    [Fact]
    public void MarkerInAdminDirMarksWorktreeReleased()
    {
        using var fx = new Fixture();
        (string worktree, string admin) = WorktreeWithAdmin(fx);
        File.WriteAllText(
            Path.Join(admin, ReleasedMarker.FileName),
            """{"released_at": 1790000000, "reason": "locked", "holders": [{"pid": 42, "exe": "devenv.exe"}]}"""
        );

        Released? released = Registered(fx.Scan(), worktree).Released;

        Assert.NotNull(released);
        Assert.Equal(Reason.Locked, released.Reason);
        Assert.Equal(1_790_000_000, released.ReleasedAtUnix);
        Assert.Equal("devenv.exe", Assert.Single(released.Holders).Exe);
    }

    /// <summary>A worktree without a marker is not released.</summary>
    [Fact]
    public void WorktreeWithoutMarkerIsNotReleased()
    {
        using var fx = new Fixture();
        (string worktree, _) = WorktreeWithAdmin(fx);

        Assert.Null(Registered(fx.Scan(), worktree).Released);
    }

    /// <summary>A prunable registration's marker is found through the repo's common dir.</summary>
    [Fact]
    public void PrunableWorktreeKeepsItsMarker()
    {
        using var fx = new Fixture();
        (string worktree, _) = WorktreeWithAdmin(fx);
        MarkAndDelete(worktree);

        RegisteredCandidate candidate = Registered(fx.Scan(), worktree);

        Assert.NotNull(candidate.Record.Prunable);
        Assert.NotNull(candidate.Released);
    }

    /// <summary>A prunable registration made with <c>worktree.useRelativePaths</c> keeps its marker.</summary>
    [Fact]
    public void PrunableWorktreeWithRelativePathsKeepsItsMarker()
    {
        using var fx = new Fixture();
        Assert.SkipUnless(GitHasRelativeWorktrees(fx.Root), "the installed git has no worktree.useRelativePaths (needs 2.48 or later)");
        string repo = fx.Repo("repo");
        string path = fx.PathTo("repo.wt/feat");
        Fixture.Git(repo, ["-c", "worktree.useRelativePaths=true", "worktree", "add", "-q", "-b", "feat", path]);
        MarkAndDelete(path);

        RegisteredCandidate candidate = Registered(fx.Scan(), path);

        Assert.NotNull(candidate.Record.Prunable);
        Assert.NotNull(candidate.Released);
    }

    /// <summary>
    /// A bare repo is its own common dir; its prunable worktree keeps its marker. The bare repo sits in a <c>.git</c> folder so
    /// discovery finds it.
    /// </summary>
    [Fact]
    public void PrunableWorktreeOfBareRepoKeepsItsMarker()
    {
        using var fx = new Fixture();
        string source = fx.Repo("source");
        string bare = fx.PathTo("bare");
        Fixture.Git(fx.Root, ["clone", "-q", "--bare", source, Path.Join(bare, ".git")]);
        string worktree = fx.PathTo("bare.wt/feat");
        Fixture.AddWorktree(bare, worktree, "feat");
        MarkAndDelete(worktree);

        RegisteredCandidate candidate = Registered(fx.Scan(), worktree);

        Assert.NotNull(candidate.Record.Prunable);
        Assert.NotNull(candidate.Released);
    }

    /// <summary>A marker that does not parse is ignored.</summary>
    [Fact]
    public void MalformedMarkerIsIgnored()
    {
        using var fx = new Fixture();
        (string worktree, string admin) = WorktreeWithAdmin(fx);
        File.WriteAllText(Path.Join(admin, ReleasedMarker.FileName), "{not json");

        Assert.Null(Registered(fx.Scan(), worktree).Released);
    }

    private static RegisteredCandidate Registered(ScanReport report, string path) =>
        Assert.Single(report.Candidates.OfType<RegisteredCandidate>(), candidate => Fixture.SamePath(candidate.Path, path));

    private static (string Worktree, string Admin) WorktreeWithAdmin(Fixture fx)
    {
        string repo = fx.Repo("repo");
        string worktree = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, worktree, "feat");
        string admin = Discoverer.ReadGitdirFile(worktree) ?? throw new InvalidOperationException($"{worktree} has no admin dir");
        return (worktree, admin);
    }

    /// <summary>Writes <see cref="Marker"/> into the worktree's admin dir, then deletes the worktree folder so git calls it prunable.</summary>
    private static void MarkAndDelete(string worktree)
    {
        string admin = Discoverer.ReadGitdirFile(worktree) ?? throw new InvalidOperationException($"{worktree} has no admin dir");
        File.WriteAllText(Path.Join(admin, ReleasedMarker.FileName), Marker);
        Directory.Delete(worktree, recursive: true);
    }

    private static bool GitHasRelativeWorktrees(string dir)
    {
        string[] numbers = GitRunner.Run(dir, ["--version"]).Split(' ')[2].Split('.');
        (int Major, int Minor) version = (int.Parse(numbers[0], CultureInfo.InvariantCulture), int.Parse(numbers[1], CultureInfo.InvariantCulture));
        return version.CompareTo((2, 48)) >= 0;
    }
}
