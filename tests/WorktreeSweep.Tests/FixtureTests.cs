using WorktreeSweep.Git;

namespace WorktreeSweep.Tests;

/// <summary>How the fixture compares paths, and how its git calls survive a file another process holds.</summary>
public sealed class FixtureTests
{
    /// <summary>
    /// A product git call whose lock-file rename meets a file another process holds without delete sharing asks the hook the fixture
    /// names in <c>GIT_ASK_YESNO</c>, and succeeds once the holder lets go. Without the hook git gives up after a short retry of its
    /// own and fails with "Permission denied", which this test pins.
    /// </summary>
    [Fact]
    public async Task ProductGitCallRetriesARenameOverAHeldConfig()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string lockPath = $"{Path.GetFileName(fx.Root)}/repo/.git/config.lock";
        using var held = new FileStream(Path.Join(repo, ".git", "config"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Task<GitStatus> call = Task.Run(() => GitRunner.RunStatus(repo, ["config", "wts.probe", "yes"], TestContext.Current.CancellationToken));

        Fixture.WaitUntil(() => call.IsCompleted || AskedAbout(Fixture.GitAsksLog, lockPath), $"git to ask about {lockPath}, or the call to finish");
        if (call.IsCompleted)
        {
            GitStatus failed = await call;
            Assert.Fail($"git exited with code {failed.Code} before asking about {lockPath}: {failed.Stderr}");
        }

        held.Dispose();
        GitStatus status = await call;

        Assert.True(status.Success, $"git exited with code {status.Code}: {status.Stderr}");
        Assert.Equal("yes", GitRunner.Run(repo, ["config", "--get", "wts.probe"], TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Whether the hook's log holds a line naming <paramref name="lockPath"/>, compared case-insensitively because git may print a
    /// short or differently cased form of the temp path, and read while git may be appending to it.
    /// </summary>
    /// <param name="log">The hook's log.</param>
    /// <param name="lockPath">A suffix of the lock file the question names.</param>
    /// <returns><see langword="true"/> when the question has been asked.</returns>
    private static bool AskedAbout(string log, string lockPath)
    {
        try
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains(lockPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

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
