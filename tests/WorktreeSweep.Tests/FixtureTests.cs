using WorktreeSweep.Git;

namespace WorktreeSweep.Tests;

/// <summary>How the fixture compares paths, and how its git calls survive a file another process holds.</summary>
public sealed class FixtureTests
{
    /// <summary>
    /// A git call whose lock-file rename meets a file another process holds without delete sharing, as an on-access scanner holds a
    /// freshly written <c>.git\config</c> in <c>%TEMP%</c>, runs again once the holder lets go instead of failing with "Permission
    /// denied". The holder lets go only after git has written and rolled back <c>config.lock</c>, so the first attempt always meets
    /// it, however slowly either side runs.
    /// </summary>
    [Fact]
    public void GitRunsAgainWhenAnotherProcessHeldTheFileItRenamesOver()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string gitDir = Path.Join(repo, ".git");
        using var held = new FileStream(Path.Join(gitDir, "config"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var watcher = new FileSystemWatcher(gitDir, "config.lock")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        int written = 0;
        Exception? watcherError = null;
        watcher.Created += (_, _) => Interlocked.Exchange(ref written, 1);
        watcher.Changed += (_, _) => Interlocked.Exchange(ref written, 1);
        watcher.Deleted += (_, _) =>
        {
            if (Volatile.Read(ref written) == 1)
            {
                held.Dispose();
            }
        };
        watcher.Error += (_, e) =>
        {
            watcherError = e.GetException();
            held.Dispose();
        };
        watcher.EnableRaisingEvents = true;

        string output = Fixture.Git(repo, ["init"]);

        Assert.Null(watcherError);
        Assert.StartsWith("Reinitialized existing Git repository", output, StringComparison.Ordinal);
    }

    /// <summary>An unrelated failure throws at once, carrying no "earlier attempt" note from a retry that never happened.</summary>
    [Fact]
    public void GitFailsAtOnceOnAnUnrelatedError()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");

        GitException error = Assert.Throws<GitException>(() => Fixture.Git(repo, ["rev-parse", "--verify", "no-such-ref"]));

        Assert.DoesNotContain("earlier attempt", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a permission failure on a lock file or on the config git writes is a hold another process can explain; a folder it
    /// cannot create, or a rejected SSH key, is not.
    /// </summary>
    /// <param name="stderr">Git's standard error.</param>
    /// <param name="held">Whether another process's hold explains it.</param>
    [Theory]
    [InlineData("error: could not write config file C:/r/.git/config: Permission denied", true)]
    [InlineData("fatal: Unable to create 'C:/r/.git/index.lock': Permission denied", true)]
    [InlineData("fatal: cannot mkdir X:/dev/new: Permission denied", false)]
    [InlineData("git@host: Permission denied (publickey).", false)]
    public void OnlyAPermissionFailureOnAFileGitRenamesOverCountsAsAHold(string stderr, bool held) =>
        Assert.Equal(held, Fixture.HeldByAnotherProcess(new GitStatus(128, "", stderr)));

    /// <summary>An 8.3 short form of the fixture root, as a short <c>%TEMP%</c> gives, names the same folder as its long form.</summary>
    [Fact]
    public void SamePathMatchesTheShortFormOfTheRoot()
    {
        using var fx = new Fixture();
        string shortForm = NativeMethods.ShortPath(fx.Root);
        Assert.SkipWhen(
            string.Equals(shortForm, fx.Root, StringComparison.OrdinalIgnoreCase),
            $"8.3 short names are disabled on the volume of {fx.Root}"
        );

        Assert.True(Fixture.SamePath(shortForm, fx.Root), $"short {shortForm}, long {fx.Root}");
        Assert.True(Fixture.SamePath(shortForm.Replace('\\', '/'), fx.Root), $"short {shortForm}, long {fx.Root}");
    }
}
