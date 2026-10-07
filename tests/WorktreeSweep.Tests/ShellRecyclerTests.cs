using WorktreeSweep.Recycle;
using WorktreeSweep.Removal;

namespace WorktreeSweep.Tests;

/// <summary>Recycling through the Shell, on real folders the tests create.</summary>
public sealed class ShellRecyclerTests
{
    /// <summary><c>HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED)</c>, which is also <c>E_ACCESSDENIED</c>.</summary>
    private const int AccessDenied = unchecked((int)0x80070005);

    private const string Tree = @"D:\dev\tree";

    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(10);

    /// <summary>An access denied naming the folder as a Shell item is a plain failure that names the call and its HRESULT.</summary>
    [Fact]
    public void AccessDeniedNamingTheItemIsAPlainFailure()
    {
        IOException failure = ShellRecycler.Failure(ShellCall.SHCreateItemFromParsingName, AccessDenied, Tree);

        Assert.IsNotType<LockedException>(failure);
        Assert.Equal(AccessDenied, failure.HResult);
        Assert.Contains("SHCreateItemFromParsingName failed with 0x80070005", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A lock from <c>PerformOperations</c> keeps the Shell failure, with its call and HRESULT, as the inner exception.</summary>
    [Fact]
    public void LockKeepsTheShellFailure()
    {
        IOException failure = ShellRecycler.Failure(ShellCall.PerformOperations, AccessDenied, Tree);

        LockedException locked = Assert.IsType<LockedException>(failure);
        Assert.Equal(Tree, locked.Path);
        IOException cause = Assert.IsType<IOException>(locked.InnerException);
        Assert.Equal(AccessDenied, cause.HResult);
        Assert.Contains("PerformOperations failed with 0x80070005", cause.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A folder with a file held open with no sharing is reported as locked, without the Shell's "Folder In Use" dialog and without
    /// recycling any part of it. The recycle runs on another thread so that a dialog fails the test on the timeout instead of hanging
    /// it. The fixture is under <c>%TEMP%</c>, where a scanner can delay a tree but cannot turn a lock into a success.
    /// </summary>
    /// <returns>The test's task.</returns>
    [Fact]
    public async Task RecyclingALockedFolderIsClassifiedAsLocked()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using var fx = new Fixture();
        string tree = fx.PathTo("tree");
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        string held = Path.Combine(tree, "sub", "held.txt");
        File.WriteAllText(held, "held");
        string loose = Path.Combine(tree, "loose.txt");
        File.WriteAllText(loose, "loose");
        using var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);

        var recycling = Task.Run(() => ShellRecycler.Recycle(tree, ShellRecycler.NoOwner), cancel);
        Task finished = await Task.WhenAny(recycling, Task.Delay(DialogTimeout, cancel));

        Assert.True(finished == recycling, $"ShellRecycler.Recycle did not return within {DialogTimeout}: the Shell may be showing a dialog");
        LockedException locked = await Assert.ThrowsAsync<LockedException>(() => recycling);
        Assert.True(Fixture.SamePath(locked.Path, tree), $"locked path: {locked.Path}");
        Assert.True(File.Exists(held) && File.Exists(loose), $"part of {tree} was recycled");
    }

    /// <summary>
    /// A scratch folder under the repo's <c>.tmp</c> goes to the real Recycle Bin. Run by hand:
    /// <c>dotnet test WorktreeSweep.slnx -c Release --no-build -- --filter-method *RecycleMovesFolderToRecycleBin --explicit on</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public void RecycleMovesFolderToRecycleBin()
    {
        string scratch = Path.Combine(RepoRoot(), ".tmp", $"recycle-test-{Environment.ProcessId}");
        Directory.CreateDirectory(Path.Combine(scratch, "sub"));
        File.WriteAllText(Path.Combine(scratch, "sub", "file.txt"), "recycle me");

        ShellRecycler.Recycle(scratch, ShellRecycler.NoOwner);

        Assert.False(Directory.Exists(scratch), $"{scratch} still exists");
    }

    /// <summary>The repo's root: the nearest folder above the test binaries that holds <c>WorktreeSweep.slnx</c>.</summary>
    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WorktreeSweep.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException($"no WorktreeSweep.slnx above {AppContext.BaseDirectory}");
    }
}
