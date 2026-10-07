using WorktreeSweep.Discovery;
using WorktreeSweep.Git;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>
/// A worktree git registered through a loopback admin share (<c>git worktree add \\localhost\X$\dev\x.wt\feat</c>) is found by a scan
/// rooted at the drive. <see cref="LoopbackShare.ToLocalDrive"/> does the mapping by spelling, and path resolution applies it, while
/// the integration test drives a real git through the share.
/// </summary>
[Collection(ProcessEnvironment.Name)]
public sealed class LoopbackShareTests
{
    /// <summary>The hosts the theory treats as this machine's: a case-insensitive set holding the loopback names and a machine name.</summary>
    private static readonly IReadOnlySet<string> LocalHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "127.0.0.1",
        "MYPC",
    };

    /// <summary>A loopback admin share of a local volume maps to its drive path, in either separator and with or without a prefix.</summary>
    /// <param name="path">The path as spelled.</param>
    /// <param name="expected">The drive path, or <see langword="null"/> when the path is not a loopback admin share.</param>
    [Theory]
    [InlineData(@"\\localhost\X$\dev\x", @"X:\dev\x")]
    [InlineData(@"\\?\UNC\localhost\x$\dev\x", @"X:\dev\x")]
    [InlineData(@"//localhost/X$/dev/x", @"X:\dev\x")]
    [InlineData(@"\\127.0.0.1\C$", @"C:\")]
    [InlineData(@"\\mypc\D$\a", @"D:\a")]
    [InlineData(@"\\server\X$\dev", null)]
    [InlineData(@"\\localhost\share\x", null)]
    [InlineData(@"\\localhost\IPC$\x", null)]
    [InlineData(@"\\localhost\XY$\x", null)]
    [InlineData(@"X:\dev\x", null)]
    [InlineData(@"\\?\X:\dev", null)]
    [InlineData("", null)]
    public void ToLocalDriveMapsALoopbackAdminShareToItsDrive(string path, string? expected) =>
        Assert.Equal(expected, LoopbackShare.ToLocalDrive(path, LocalHosts));

    /// <summary>This machine's own hosts are in the set the mapping reads, whatever the case they are spelled in.</summary>
    [Fact]
    public void LocalHostsHoldTheMachineName()
    {
        IReadOnlySet<string> hosts = LoopbackShare.LocalHosts();

        Assert.Contains(Environment.MachineName, hosts, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("localhost", hosts, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolving a loopback admin share of an existing folder gives what resolving its drive path gives.</summary>
    [Fact]
    public void ResolveMapsALoopbackAdminShareToItsDrive()
    {
        string local = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string? unc = LoopbackSpelling(local);
        Assert.SkipWhen(unc is null || !Directory.Exists(unc), $"the loopback admin share of {local} is not reachable");

        Assert.Equal(PathResolver.Resolve(local), PathResolver.Resolve(unc!), ignoreCase: true);
    }

    /// <summary>
    /// A worktree git registered through the loopback spelling of a container path is a registered candidate of a scan rooted at the
    /// drive, and no orphan names its path. The share is written into git's config as a safe directory for the duration, because git
    /// refuses a repository whose spelling it reads as another owner's.
    /// </summary>
    [Fact]
    public void WorktreeRegisteredThroughALoopbackShareIsNotAnOrphan()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        string? uncRoot = LoopbackSpelling(fx.Root);
        Assert.SkipWhen(uncRoot is null || !Directory.Exists(uncRoot), $"the loopback admin share of {fx.Root} is not reachable");
        string uncWorktree = Path.Join(uncRoot, "repo.wt", "feat");
        using InheritedEnv trust = TrustEveryDirectory(fx);
        Fixture.Git(repo, ["worktree", "add", "-q", uncWorktree, "-b", "feat"]);

        DiscoveryResult found = Discoverer.Discover(fx.Root, new VolumeStalls());

        Assert.DoesNotContain(found.Orphans, orphan => Fixture.SamePath(orphan.Path, wt));
        _ = Assert.Single(found.Registered, pair => Fixture.SamePath(PathResolver.Resolve(pair.Record.Path), wt));
    }

    /// <summary>
    /// A registered candidate's path is the drive spelling of a loopback admin share git recorded, while its record keeps git's
    /// spelling; a share of another host is left as recorded.
    /// </summary>
    [Fact]
    public void RegisteredCandidatePathUsesTheLocalDrive()
    {
        RegisteredCandidate loopback = Registered(@"\\localhost\Q$\a\b");

        Assert.Equal(@"Q:\a\b", loopback.Path);
        Assert.Equal(@"\\localhost\Q$\a\b", loopback.Record.Path);
        Assert.Equal(@"\\otherhost\Q$\a\b", Registered(@"\\otherhost\Q$\a\b").Path);
    }

    /// <summary>
    /// Lifting and putting back the git lock of a worktree git registered through a loopback admin share reaches that worktree: the
    /// lock is gone after <see cref="CandidateRemover.GitUnlock"/> and back with its reason after <see cref="CandidateRemover.GitRelock"/>.
    /// </summary>
    [Fact]
    public void GitLockReachesAUncRegisteredWorktree()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        string? uncRoot = LoopbackSpelling(fx.Root);
        Assert.SkipWhen(uncRoot is null || !Directory.Exists(uncRoot), $"the loopback admin share of {fx.Root} is not reachable");
        string uncWorktree = Path.Join(uncRoot, "repo.wt", "feat");
        using InheritedEnv trust = TrustEveryDirectory(fx);
        Fixture.Git(repo, ["worktree", "add", "-q", uncWorktree, "-b", "feat"]);
        Fixture.Git(repo, ["worktree", "lock", "--reason", "in use", uncWorktree]);
        RegisteredCandidate candidate = Assert.Single(
            fx.Scan().Candidates.OfType<RegisteredCandidate>(),
            registered => Fixture.SamePath(PathResolver.Resolve(registered.Path), wt)
        );

        CandidateRemover.GitUnlock(candidate);
        string? afterUnlock = GitLock(repo, wt);
        CandidateRemover.GitRelock(candidate);

        Assert.Null(afterUnlock);
        Assert.Equal("in use", GitLock(repo, wt));
    }

    /// <summary>
    /// A symbolic link whose target is spelled as a loopback admin share resolves to the drive spelling of its target. A junction
    /// cannot target a share, so the link is a symbolic one, and the test is skipped where this process may not create one.
    /// </summary>
    [Fact]
    public void ResolveMapsALinkThatTargetsALoopbackShare()
    {
        using var fx = new Fixture();
        string target = fx.PathTo("target");
        Directory.CreateDirectory(target);
        string? uncTarget = LoopbackSpelling(target);
        Assert.SkipWhen(uncTarget is null || !Directory.Exists(uncTarget), $"the loopback admin share of {target} is not reachable");
        string link = fx.PathTo("link");
        try
        {
            _ = Directory.CreateSymbolicLink(link, uncTarget!);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"cannot create a symbolic link to {uncTarget}: {error.Message}");
        }

        Assert.Equal(PathResolver.Resolve(target), PathResolver.Resolve(link), ignoreCase: true);
    }

    /// <summary>A registered candidate whose record has <paramref name="path"/>, with signals no test here reads.</summary>
    /// <param name="path">The record's path.</param>
    /// <returns>The candidate.</returns>
    private static RegisteredCandidate Registered(string path) =>
        new()
        {
            Record = new WorktreeRecord { Path = path, Branch = "feat" },
            Repo = @"Q:\a",
            Signals = new WorktreeSignals
            {
                MergeState = MergeState.Ancestor,
                MergeStateAgainst = "main",
                Dirty = new Dirty(),
                Upstream = Upstream.None,
            },
        };

    /// <summary>
    /// Points git's global config at a file under the fixture that trusts every directory, until disposed: git refuses a repository
    /// whose loopback-share spelling it reads as another owner's.
    /// </summary>
    /// <param name="fx">The fixture.</param>
    /// <returns>The environment change.</returns>
    private static InheritedEnv TrustEveryDirectory(Fixture fx)
    {
        string config = Path.Join(fx.Root, "gitconfig");
        File.WriteAllText(config, "[safe]\n\tdirectory = *\n");
        return new InheritedEnv(new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = config });
    }

    /// <summary>The git lock reason <c>git worktree list --porcelain</c> shows for the worktree at <paramref name="wt"/>.</summary>
    /// <param name="repo">The repo.</param>
    /// <param name="wt">The worktree's drive path.</param>
    /// <returns>The reason, <c>""</c> when locked without one, or <see langword="null"/> when not locked.</returns>
    private static string? GitLock(string repo, string wt) =>
        Assert
            .Single(
                Discoverer.ParseWorktreePorcelain(Fixture.Git(repo, ["worktree", "list", "--porcelain"])),
                record => Fixture.SamePath(PathResolver.Resolve(record.Path), wt)
            )
            .Locked;

    /// <summary>The loopback admin-share spelling of a local path, or <see langword="null"/> when it has no drive letter.</summary>
    /// <param name="path">A local path.</param>
    /// <returns>The spelling, or <see langword="null"/>.</returns>
    private static string? LoopbackSpelling(string path)
    {
        string full = Path.GetFullPath(path);
        return full.Length > 1 && full[1] == ':' ? @"\\localhost\" + full[..1].ToUpperInvariant() + "$" + full[2..] : null;
    }
}
