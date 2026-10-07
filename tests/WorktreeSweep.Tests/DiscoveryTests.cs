using System.Diagnostics;
using WorktreeSweep.Discovery;
using WorktreeSweep.Git;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>What discovery finds under a root: repos, registered worktrees, container dirs and orphans.</summary>
public sealed class DiscoveryTests
{
    private readonly VolumeStalls stalls = new();

    /// <summary>A linked worktree is registered; the repo's main worktree never is.</summary>
    [Fact]
    public void MainWorktreeIsNeverRegistered()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        List<string> paths = [.. found.Registered.Select(pair => pair.Record.Path)];
        Assert.Contains(paths, path => Fixture.SamePath(path, wt));
        Assert.DoesNotContain(paths, path => Fixture.SamePath(path, repo));
        Assert.Single(found.Repos);
        Assert.Empty(found.Errors);
    }

    /// <summary>Worktrees of two repos nested one level inside a container folder are registered, not orphans.</summary>
    [Fact]
    public void NestedRegisteredWorktreeInContainerIsNotAnOrphan()
    {
        using var fx = new Fixture();
        string a = fx.Repo("a");
        string b = fx.Repo("b");
        string wtA = fx.PathTo("x.wt/eram-am/a");
        string wtB = fx.PathTo("x.wt/eram-am/b");
        Fixture.AddWorktree(a, wtA, "eram-am");
        Fixture.AddWorktree(b, wtB, "eram-am");

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        _ = RegisteredAt(found, wtA);
        _ = RegisteredAt(found, wtB);
        Assert.Empty(found.Orphans);
    }

    /// <summary>An unregistered folder beside a registered worktree's parent is one orphan, at the container's child.</summary>
    [Fact]
    public void UnregisteredSiblingFolderIsAnOrphan()
    {
        using var fx = new Fixture();
        string a = fx.Repo("a");
        Fixture.AddWorktree(a, fx.PathTo("x.wt/eram-am/a"), "eram-am");
        string leftover = fx.PathTo("x.wt/eram-qx/b");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Join(leftover, "file.txt"), "left behind\n");

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Orphan orphan = Assert.Single(found.Orphans);
        Assert.True(Fixture.SamePath(orphan.Path, fx.PathTo("x.wt/eram-qx")), orphan.ToString());
        Assert.Equal(OrphanKind.Folder, orphan.Kind);
    }

    /// <summary>An empty folder in a container is an orphan with no git markers.</summary>
    [Fact]
    public void EmptyContainerChildIsAnOrphan()
    {
        using var fx = new Fixture();
        string empty = fx.PathTo("x.wt/empty");
        Directory.CreateDirectory(empty);

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Orphan orphan = Assert.Single(found.Orphans);
        Assert.True(Fixture.SamePath(orphan.Path, empty), orphan.ToString());
        Assert.Equal(OrphanKind.Folder, orphan.Kind);
        Assert.False(orphan.HasGitDir || orphan.StaleGitdir || orphan.LiveGitdir is not null, orphan.ToString());
        SizeInfo size = SignalReader.WalkSize(orphan.Path);
        Assert.True(size is { Files: 0, Bytes: 0 }, size.ToString());
    }

    /// <summary>An orphan whose <c>.git</c> file points at a missing git dir is flagged stale; a plain folder is not.</summary>
    [Fact]
    public void OrphanWithMissingGitdirIsFlagged()
    {
        using var fx = new Fixture();
        string stale = fx.PathTo("x.wt/stale");
        Directory.CreateDirectory(stale);
        string missing = fx.PathTo("gone/.git/worktrees/stale");
        File.WriteAllText(Path.Join(stale, ".git"), $"gitdir: {missing.Replace('\\', '/')}\n");
        string plain = fx.PathTo("x.wt/plain");
        Directory.CreateDirectory(plain);

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Orphan staleOrphan = Assert.Single(found.Orphans, orphan => Fixture.SamePath(orphan.Path, stale));
        Assert.True(staleOrphan.StaleGitdir, staleOrphan.ToString());
        Assert.False(staleOrphan.HasGitDir, staleOrphan.ToString());
        Orphan plainOrphan = Assert.Single(found.Orphans, orphan => Fixture.SamePath(orphan.Path, plain));
        Assert.False(plainOrphan.StaleGitdir, plainOrphan.ToString());
    }

    /// <summary>A registration whose folder is gone is still registered, marked prunable, and leaves no orphan.</summary>
    [Fact]
    public void PrunableRegistrationIsRegistered()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/gone");
        Fixture.AddWorktree(repo, wt, "gone");
        Directory.Delete(wt, recursive: true);

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        WorktreeRecord record = RegisteredAt(found, wt);
        Assert.NotNull(record.Prunable);
        Assert.Empty(found.Orphans);
    }

    /// <summary>
    /// A pin: a root given by its 8.3 short name still matches the worktree git registered under the long name. It held before paths
    /// were resolved too, because <see cref="Path.GetFullPath(string)"/> already expands a short root.
    /// </summary>
    [Fact]
    public void RegisteredWorktreeIsNotAnOrphanWhenRootIsShortNameSpelled()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        string shortRoot = NativeMethods.ShortPath(fx.Root);
        Assert.SkipWhen(shortRoot.Equals(fx.Root, StringComparison.OrdinalIgnoreCase), $"{fx.Root} has no 8.3 name");

        DiscoveryResult found = Discoverer.Discover(shortRoot, stalls);

        Assert.Empty(found.Orphans);
        _ = RegisteredAt(found, wt);
    }

    /// <summary>A root given with a <c>\\?\</c> prefix still matches the worktree git registered without one.</summary>
    [Fact]
    public void RegisteredWorktreeIsNotAnOrphanWhenRootHasVerbatimPrefix()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");

        DiscoveryResult found = Discoverer.Discover(@"\\?\" + fx.Root, stalls);

        Assert.Empty(found.Orphans);
        _ = RegisteredAt(found, wt);
    }

    /// <summary>A junction in a container pointing at a registered worktree is a link orphan: it never matches through its target.</summary>
    [Fact]
    public void JunctionToARegisteredWorktreeStaysALinkOrphan()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        string link = fx.PathTo("repo.wt/link");
        Assert.SkipUnless(Fixture.MakeJunction(link, wt), "mklink /J is unavailable");

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Orphan orphan = Assert.Single(found.Orphans);
        Assert.True(Fixture.SamePath(orphan.Path, link), orphan.ToString());
        Assert.Equal(OrphanKind.Link, orphan.Kind);
        _ = RegisteredAt(found, wt);
    }

    /// <summary>
    /// A worktree registered at a path that is now a junction (its folder moved elsewhere and linked back) stays matched: the
    /// registered path is not resolved through its own last segment, so the walk skips the junction instead of calling it an orphan.
    /// </summary>
    [Fact]
    public void RegisteredWorktreeThatIsAJunctionStaysRegistered()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        string moved = fx.PathTo("moved");
        Directory.Move(wt, moved);
        Assert.SkipUnless(Fixture.MakeJunction(wt, moved), "mklink /J is unavailable");

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Assert.Empty(found.Orphans);
        _ = RegisteredAt(found, wt);
    }

    /// <summary>
    /// When the root resolves to another spelling (as a subst drive does) but a record's parent cannot be resolved, the record still
    /// matches the walked folder by its spelling as given: a failed resolution never makes an orphan.
    /// </summary>
    [Fact]
    public void RecordWhoseParentCannotBeResolvedIsStillMatched()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        const string RootElsewhere = @"Q:\as-if-substed";

        DiscoveryResult found = Discoverer.Discover(
            fx.Root,
            path => Fixture.SamePath(path, fx.Root) ? RootElsewhere : Path.GetFullPath(path),
            stalls
        );

        Assert.Empty(found.Orphans);
        _ = RegisteredAt(found, wt);
    }

    /// <summary>
    /// A pin: a worktree added through its 8.3 short name matches the walked folder's long name. Git records the long name, so this
    /// held before the registered name was expanded too.
    /// </summary>
    [Fact]
    public void WorktreeRegisteredByItsShortNameIsNotAnOrphan()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feature-branch");
        Directory.CreateDirectory(wt);
        string shortWt = NativeMethods.ShortPath(wt);
        Assert.SkipWhen(Path.GetFileName(shortWt).Equals("feature-branch", StringComparison.OrdinalIgnoreCase), $"{wt} has no 8.3 name");
        Fixture.AddWorktree(repo, shortWt, "feat");

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Assert.Empty(found.Orphans);
        _ = RegisteredAt(found, wt);
    }

    /// <summary>
    /// A registration whose folder is gone, scanned through a short-named root, stays prunable and leaves no orphan; its path resolves
    /// to the resolved nearest existing folder plus the missing segments.
    /// </summary>
    [Fact]
    public void PrunableRegistrationWithMissingFolderIsNotAnOrphan()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/gone");
        Fixture.AddWorktree(repo, wt, "gone");
        Directory.Delete(wt, recursive: true);
        string shortRoot = NativeMethods.ShortPath(fx.Root);
        Assert.SkipWhen(shortRoot.Equals(fx.Root, StringComparison.OrdinalIgnoreCase), $"{fx.Root} has no 8.3 name");

        DiscoveryResult found = Discoverer.Discover(shortRoot, stalls);

        Assert.NotNull(RegisteredAt(found, wt).Prunable);
        Assert.Empty(found.Orphans);
        string ancestor = Discoverer.StripVerbatim(PathResolver.FinalPath(fx.PathTo("repo.wt")));
        Assert.Equal(Path.Join(ancestor, "gone", "deeper"), PathResolver.Resolve(Path.Join(shortRoot, "repo.wt", "gone", "deeper")));
    }

    /// <summary>An existing folder resolves to its own path as the system names it, case included, without a <c>\\?\</c> prefix.</summary>
    [Fact]
    public void ResolveGivesAnExistingFolderAsTheSystemNamesIt()
    {
        using var fx = new Fixture();
        string dir = fx.PathTo("Some Dir");
        Directory.CreateDirectory(dir);

        string resolved = PathResolver.Resolve(dir.ToUpperInvariant());

        Assert.Equal(dir, resolved, ignoreCase: true);
        Assert.EndsWith(@"\Some Dir", resolved, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\\?\", resolved, StringComparison.Ordinal);
    }

    /// <summary>A <c>\\?\</c>-prefixed spelling resolves to the same path as the plain one.</summary>
    [Fact]
    public void ResolveRemovesTheVerbatimPrefix()
    {
        using var fx = new Fixture();
        string dir = fx.PathTo("dir");
        Directory.CreateDirectory(dir);

        Assert.Equal(PathResolver.Resolve(dir), PathResolver.Resolve(@"\\?\" + dir));
    }

    /// <summary>A missing path resolves its nearest existing folder and keeps the missing segments as spelled.</summary>
    [Fact]
    public void ResolveKeepsAMissingPathsTail()
    {
        using var fx = new Fixture();
        string dir = fx.PathTo("dir");
        Directory.CreateDirectory(dir);

        string resolved = PathResolver.Resolve(Path.Join(dir, "Missing", "deeper"));

        Assert.Equal(Path.Join(PathResolver.Resolve(dir), "Missing", "deeper"), resolved);
    }

    /// <summary>A repo's <c>.claude\worktrees</c> folder is a container, and an unregistered folder in it an orphan.</summary>
    [Fact]
    public void ClaudeWorktreesDirIsAContainer()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string agent = Path.Join(repo, ".claude", "worktrees", "agent-1");
        Directory.CreateDirectory(agent);

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Orphan orphan = Assert.Single(found.Orphans);
        Assert.True(Fixture.SamePath(orphan.Path, agent), orphan.ToString());
    }

    /// <summary>A junction in a container is one link orphan pointing at its target, and the target's folders are never walked.</summary>
    [Fact]
    public void JunctionInContainerIsNotFollowed()
    {
        using var fx = new Fixture();
        string target = fx.PathTo("target");
        Directory.CreateDirectory(Path.Join(target, "inner"));
        File.WriteAllBytes(Path.Join(target, "inner", "big.bin"), new byte[100]);
        string container = fx.PathTo("x.wt");
        Directory.CreateDirectory(container);
        string link = Path.Join(container, "link");
        Assert.SkipUnless(Fixture.MakeJunction(link, target), "mklink /J is unavailable");

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Orphan orphan = Assert.Single(found.Orphans);
        Assert.True(Fixture.SamePath(orphan.Path, link), orphan.ToString());
        Assert.Equal(OrphanKind.Link, orphan.Kind);
        SizeInfo size = SignalReader.WalkSize(orphan.Path);
        Assert.True(size is { Files: 0, Bytes: 0 }, $"target was walked: {size}");
        Assert.NotNull(orphan.LinkTarget);
        Assert.True(Fixture.SamePath(orphan.LinkTarget, target), orphan.ToString());
    }

    /// <summary>A bare record, and a detached one, parse with their flags and no branch.</summary>
    [Fact]
    public void ParsesBareAndDetachedRecords()
    {
        string text = "worktree C:/bare\nbare\n\nworktree C:/repo.wt/detached\nHEAD 1111\ndetached\n";

        IReadOnlyList<WorktreeRecord> records = Discoverer.ParseWorktreePorcelain(text);

        WorktreeRecord[] expected =
        [
            new WorktreeRecord { Path = @"C:\bare", Bare = true },
            new WorktreeRecord
            {
                Path = @"C:\repo.wt\detached",
                Head = "1111",
                Detached = true,
            },
        ];
        Assert.Equal(expected, records);
    }

    /// <summary>A branch loses its <c>refs/heads/</c> prefix; <c>locked</c> keeps its reason, or <c>""</c> without one.</summary>
    [Fact]
    public void ParsesBranchAndLockedRecords()
    {
        string text =
            "worktree C:/repo\r\nHEAD 2222\r\nbranch refs/heads/main\r\n\r\n"
            + "worktree C:/repo.wt/held\nHEAD 3333\nbranch refs/heads/feat/x\nlocked agent at work\n\n"
            + "worktree C:/repo.wt/bare-lock\nHEAD 4444\nbranch refs/heads/y\nlocked";

        IReadOnlyList<WorktreeRecord> records = Discoverer.ParseWorktreePorcelain(text);

        WorktreeRecord[] expected =
        [
            new WorktreeRecord
            {
                Path = @"C:\repo",
                Head = "2222",
                Branch = "main",
            },
            new WorktreeRecord
            {
                Path = @"C:\repo.wt\held",
                Head = "3333",
                Branch = "feat/x",
                Locked = "agent at work",
            },
            new WorktreeRecord
            {
                Path = @"C:\repo.wt\bare-lock",
                Head = "4444",
                Branch = "y",
                Locked = "",
            },
        ];
        Assert.Equal(expected, records);
    }

    /// <summary>A prunable record keeps git's reason; a line before the first record and an unknown key are skipped.</summary>
    [Fact]
    public void ParsesPrunableRecordAndSkipsStrayLines()
    {
        string text =
            "HEAD 0000\nworktree C:/repo.wt/gone\nHEAD 5555\nbranch refs/heads/gone\nfuture-key x\n"
            + "prunable gitdir file points to non-existent location\n";

        IReadOnlyList<WorktreeRecord> records = Discoverer.ParseWorktreePorcelain(text);

        WorktreeRecord record = Assert.Single(records);
        Assert.Equal(
            new WorktreeRecord
            {
                Path = @"C:\repo.wt\gone",
                Head = "5555",
                Branch = "gone",
                Prunable = "gitdir file points to non-existent location",
            },
            record
        );
    }

    /// <summary>Path keys unify separators, fold case and drop trailing separators, except a drive root's.</summary>
    /// <param name="path">The path.</param>
    /// <param name="key">Its expected key.</param>
    [Theory]
    [InlineData("C:/Users/Me/Repo.WT/", @"c:\users\me\repo.wt")]
    [InlineData(@"D:\X.wt\\", @"d:\x.wt")]
    [InlineData(@"C:\", @"c:\")]
    [InlineData("C:/", @"c:\")]
    [InlineData(@"\", @"\")]
    [InlineData("", "")]
    public void PathKeyFoldsCaseAndSeparators(string path, string key) => Assert.Equal(key, Discoverer.PathKey(path));

    /// <summary>A path git printed gets backslash separators; one that has them already is unchanged.</summary>
    /// <param name="text">The path as git printed it.</param>
    /// <param name="path">The expected path.</param>
    [Theory]
    [InlineData("C:/Users/me/repo.wt/feat", @"C:\Users\me\repo.wt\feat")]
    [InlineData(@"C:\already\native", @"C:\already\native")]
    [InlineData("//server/share/x", @"\\server\share\x")]
    [InlineData("", "")]
    public void FromGitPathUsesBackslashes(string text, string path) => Assert.Equal(path, Discoverer.FromGitPath(text));

    /// <summary>A repo whose <c>.claude</c> is a junction does not make the junction's <c>worktrees</c> folder a container.</summary>
    [Fact]
    public void JunctionedClaudeDirIsNotAContainer()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string elsewhere = fx.PathTo("elsewhere");
        Directory.CreateDirectory(Path.Join(elsewhere, "worktrees", "x"));
        Assert.SkipUnless(Fixture.MakeJunction(Path.Join(repo, ".claude"), elsewhere), "mklink /J is unavailable");

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Assert.Empty(found.Orphans);
        Assert.Empty(found.Containers);
    }

    /// <summary>An orphan whose <c>.git</c> file points at a junction whose target was deleted is flagged stale.</summary>
    [Fact]
    public void OrphanWithDanglingLinkGitdirIsFlagged()
    {
        using var fx = new Fixture();
        string target = fx.PathTo("gitdir-target");
        Directory.CreateDirectory(target);
        string link = fx.PathTo("gitdir-link");
        Assert.SkipUnless(Fixture.MakeJunction(link, target), "mklink /J is unavailable");
        Directory.Delete(target);
        string orphanPath = fx.PathTo("x.wt/dangling");
        Directory.CreateDirectory(orphanPath);
        File.WriteAllText(Path.Join(orphanPath, ".git"), $"gitdir: {link.Replace('\\', '/')}\n");

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Orphan orphan = Assert.Single(found.Orphans);
        Assert.True(orphan.StaleGitdir, orphan.ToString());
        Assert.Null(orphan.LiveGitdir);
    }

    /// <summary>
    /// A <c>gitdir:</c> value no path can hold (a NUL) does not stop discovery: the gitdir cannot be checked, so it is treated as
    /// still registered.
    /// </summary>
    [Fact]
    public void OrphanWithInvalidGitdirPathIsTreatedAsRegistered()
    {
        using var fx = new Fixture();
        string orphanPath = fx.PathTo("x.wt/invalid");
        Directory.CreateDirectory(orphanPath);
        File.WriteAllText(Path.Join(orphanPath, ".git"), "gitdir: foo\0bar\n");

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Orphan orphan = Assert.Single(found.Orphans);
        Assert.False(orphan.StaleGitdir, orphan.ToString());
        Assert.NotNull(orphan.LiveGitdir);
    }

    /// <summary>An orphan whose <c>.git</c> file points at a git dir that exists carries that git dir and is not stale.</summary>
    [Fact]
    public void OrphanWithExistingGitdirIsLive()
    {
        using var fx = new Fixture();
        string gitdir = fx.PathTo("outside/.git/worktrees/live");
        Directory.CreateDirectory(gitdir);
        string orphanPath = fx.PathTo("x.wt/live");
        Directory.CreateDirectory(orphanPath);
        File.WriteAllText(Path.Join(orphanPath, ".git"), $"gitdir: {gitdir.Replace('\\', '/')}\n");

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Orphan orphan = Assert.Single(found.Orphans);
        Assert.False(orphan.StaleGitdir, orphan.ToString());
        Assert.NotNull(orphan.LiveGitdir);
        Assert.True(Fixture.SamePath(orphan.LiveGitdir, gitdir), orphan.ToString());
    }

    /// <summary>An orphan folder holding a <c>.git</c> directory (a clone, not a worktree) is flagged as having one.</summary>
    [Fact]
    public void OrphanWithGitDirectoryHasGitDir()
    {
        using var fx = new Fixture();
        string orphanPath = fx.PathTo("x.wt/clone");
        Directory.CreateDirectory(Path.Join(orphanPath, ".git"));

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Orphan orphan = Assert.Single(found.Orphans);
        Assert.True(orphan.HasGitDir, orphan.ToString());
        Assert.False(orphan.StaleGitdir || orphan.LiveGitdir is not null, orphan.ToString());
    }

    /// <summary>A repo git cannot list (its config does not parse) is skipped; the others are still found.</summary>
    [Fact]
    public void RepoGitCannotListIsSkipped()
    {
        using var fx = new Fixture();
        string broken = fx.Repo("broken");
        File.WriteAllText(Path.Join(broken, ".git", "config"), "[core\n");
        string good = fx.Repo("good");

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Repo repo = Assert.Single(found.Repos);
        Assert.True(Fixture.SamePath(repo.Path, good), repo.ToString());
    }

    /// <summary>A repo git cannot list is skipped and reported with git's own message for a config that does not parse.</summary>
    [Fact]
    public void RepoGitCannotListIsReportedWithGitsError()
    {
        using var fx = new Fixture();
        string broken = fx.Repo("broken");
        File.WriteAllText(Path.Join(broken, ".git", "config"), "[core\n bad");

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Assert.Empty(found.Repos);
        DiscoveryError error = Assert.Single(found.Errors);
        Assert.True(Fixture.SamePath(error.Repo, broken), error.ToString());
        Assert.True(Fixture.SamePath(error.Path, broken), error.ToString());
        Assert.StartsWith("git worktree list failed:", error.Message, StringComparison.Ordinal);
        Assert.Contains("bad config", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A linked worktree git's list leaves out (its <c>gitdir</c> file is empty) is not registered, and is reported by its
    /// <c>gitdir</c> file; the repo stays with what git listed.
    /// </summary>
    [Fact]
    public void WorktreeGitLeavesOutIsReported()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        string id = Assert.Single(Directory.GetDirectories(Path.Join(repo, ".git", "worktrees")));
        string gitdir = Path.Join(id, "gitdir");
        File.WriteAllText(gitdir, "");

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        _ = Assert.Single(found.Repos);
        Assert.DoesNotContain(found.Registered, pair => Fixture.SamePath(pair.Record.Path, wt));
        DiscoveryError error = Assert.Single(found.Errors);
        Assert.True(Fixture.SamePath(error.Repo, repo), error.ToString());
        Assert.True(Fixture.SamePath(error.Path, gitdir), error.ToString());
        Assert.Equal("git's worktree list leaves this worktree out: its gitdir file is empty", error.Message);
    }

    /// <summary>A worktree whose <c>gitdir</c> file was deleted is reported, with the hint that prune clears the record.</summary>
    [Fact]
    public void MissingGitdirIsReportedWithPruneHint()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        string id = Assert.Single(Directory.GetDirectories(Path.Join(repo, ".git", "worktrees")));
        string gitdir = Path.Join(id, "gitdir");
        File.Delete(gitdir);

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        _ = Assert.Single(found.Repos);
        DiscoveryError error = Assert.Single(found.Errors);
        Assert.True(Fixture.SamePath(error.Repo, repo), error.ToString());
        Assert.True(Fixture.SamePath(error.Path, gitdir), error.ToString());
        Assert.Contains("git worktree prune", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A <c>gitdir</c> file naming a worktree git's list does not hold is reported by the comparison alone.</summary>
    [Fact]
    public void GitdirNamingAnUnlistedWorktreeIsReported()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.AddWorktree(repo, wt, "feat");
        IReadOnlyList<WorktreeRecord> listed = Discoverer.ListWorktrees(repo);
        string id = Assert.Single(Directory.GetDirectories(Path.Join(repo, ".git", "worktrees")));
        string gitdir = Path.Join(id, "gitdir");
        string elsewhere = fx.PathTo("elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(gitdir, Path.Join(elsewhere, ".git"));

        List<DiscoveryError> errors = Discoverer.LeftOutWorktrees(repo, listed);

        DiscoveryError error = Assert.Single(errors);
        Assert.True(Fixture.SamePath(error.Repo, repo), error.ToString());
        Assert.True(Fixture.SamePath(error.Path, gitdir), error.ToString());
        Assert.Equal($"git's worktree list leaves out the worktree its gitdir file names: {elsewhere}", error.Message);
    }

    /// <summary>A worktree git registered with relative paths (git 2.48+) is matched, not reported as left out.</summary>
    [Fact]
    public void RelativeGitdirIsMatched()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo("repo.wt/feat");
        Fixture.Git(repo, ["-c", "worktree.useRelativePaths=true", "worktree", "add", "-q", "-b", "feat", wt]);

        DiscoveryResult found = Discoverer.Discover(fx.Root, stalls);

        Assert.Empty(found.Errors);
        _ = RegisteredAt(found, wt);
    }

    /// <summary>A <c>\\?\</c> or <c>\??\</c> prefix is removed, except before <c>UNC\</c>; a plain path is unchanged.</summary>
    /// <param name="path">The link target.</param>
    /// <param name="expected">The expected result.</param>
    [Theory]
    [InlineData(@"\\?\C:\x", @"C:\x")]
    [InlineData(@"\??\C:\x", @"C:\x")]
    [InlineData(@"\\?\UNC\srv\share\x", @"\\?\UNC\srv\share\x")]
    [InlineData(@"C:\plain", @"C:\plain")]
    public void StripVerbatimRemovesTheDevicePrefix(string path, string expected) => Assert.Equal(expected, Discoverer.StripVerbatim(path));

    private static WorktreeRecord RegisteredAt(DiscoveryResult found, string path) =>
        Assert.Single(found.Registered, pair => Fixture.SamePath(pair.Record.Path, path)).Record;
}

/// <summary>
/// What path resolution writes to <see cref="Trace"/>. The test adds a listener to the process-wide trace
/// listeners, so it runs in the collection no other test runs beside.
/// </summary>
[Collection(ProcessEnvironment.Name)]
public sealed class PathResolverTraceTests
{
    /// <summary>A path on a drive letter nothing is mapped to is returned made absolute, as spelled, with a trace warning.</summary>
    [Fact]
    public void UnresolvablePathIsComparedAsSpelledWithAWarning()
    {
        HashSet<char> mapped = [.. DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0]))];
        char[] free = [.. "QRSTUVWXYZ".Where(letter => !mapped.Contains(letter))];
        Assert.SkipWhen(free.Length == 0, "every drive letter from Q to Z is mapped");
        string path = $@"{free[0]}:\nothing\here";

        (string resolved, string trace) = ResolveTraced(path);

        Assert.Equal(Path.GetFullPath(path), resolved);
        Assert.Contains($"cannot resolve {path}: ", trace, StringComparison.Ordinal);
        Assert.Contains("; comparing it as spelled", trace, StringComparison.Ordinal);
    }

    /// <summary>An empty path, which cannot be made absolute, is returned as given with a trace warning instead of throwing.</summary>
    [Fact]
    public void EmptyPathIsComparedAsSpelledWithAWarning()
    {
        (string resolved, string trace) = ResolveTraced("");

        Assert.Equal("", resolved);
        Assert.Contains("cannot resolve : ", trace, StringComparison.Ordinal);
        Assert.Contains("; comparing it as spelled", trace, StringComparison.Ordinal);
    }

    private static (string Resolved, string Trace) ResolveTraced(string path)
    {
        using var text = new StringWriter();
        using var listener = new TextWriterTraceListener(text);
        _ = Trace.Listeners.Add(listener);
        string resolved;
        try
        {
            resolved = PathResolver.Resolve(path);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
        listener.Flush();
        return (resolved, text.ToString());
    }
}
