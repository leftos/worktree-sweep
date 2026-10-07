using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using WorktreeSweep.Removal;

namespace WorktreeSweep.Tests;

/// <summary>Permanent deletion: read-only clearing, link safety, and the lock classification, on real folders the tests create.</summary>
public sealed class PermanentDeleteTests
{
    /// <summary>A read-only file deep in the tree is cleared before it is deleted.</summary>
    [Fact]
    public void DeleteClearsReadOnlyFiles()
    {
        using var fx = new Fixture();
        string tree = fx.PathTo("tree");
        string nested = Path.Combine(tree, "objects", "ab");
        Directory.CreateDirectory(nested);
        string blob = Path.Combine(nested, "cdef");
        File.WriteAllText(blob, "blob");
        File.SetAttributes(blob, File.GetAttributes(blob) | FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(tree, "plain.txt"), "plain");

        PermanentDelete.Delete(tree);

        Assert.False(Directory.Exists(tree), $"{tree} still exists");
    }

    /// <summary>A read-only folder's own attribute is cleared before the folder itself is removed.</summary>
    [Fact]
    public void DeleteClearsReadOnlyDirectory()
    {
        using var fx = new Fixture();
        string tree = fx.PathTo("tree");
        string sub = Path.Combine(tree, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "file.txt"), "x");
        File.SetAttributes(sub, File.GetAttributes(sub) | FileAttributes.ReadOnly);

        PermanentDelete.Delete(tree);

        Assert.False(Directory.Exists(tree), $"{tree} still exists");
    }

    /// <summary>A junction inside the tree is removed as a link: its target and the target's file survive.</summary>
    [Fact]
    public void DeleteDoesNotFollowJunctions()
    {
        using var fx = new Fixture();
        string outside = fx.PathTo("outside");
        Directory.CreateDirectory(outside);
        string kept = Path.Combine(outside, "keep.txt");
        File.WriteAllText(kept, "keep");
        string tree = fx.PathTo("tree");
        string link = Path.Combine(tree, "sub", "link");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Assert.SkipUnless(Fixture.MakeJunction(link, outside), "mklink /J is unavailable");

        PermanentDelete.Delete(tree);

        Assert.False(Directory.Exists(tree), $"{tree} still exists");
        Assert.True(File.Exists(kept), $"the junction's target lost {kept}");
        Assert.Equal("keep", File.ReadAllText(kept));
    }

    /// <summary>Deleting a junction removes the link itself and nothing else.</summary>
    [Fact]
    public void DeleteOfAJunctionRemovesOnlyTheLink()
    {
        using var fx = new Fixture();
        string outside = fx.PathTo("outside");
        Directory.CreateDirectory(outside);
        string kept = Path.Combine(outside, "keep.txt");
        File.WriteAllText(kept, "keep");
        string link = fx.PathTo("link");
        Assert.SkipUnless(Fixture.MakeJunction(link, outside), "mklink /J is unavailable");

        PermanentDelete.Delete(link);

        Assert.False(Directory.Exists(link), $"{link} still exists");
        Assert.True(File.Exists(kept), $"the junction's target lost {kept}");
    }

    /// <summary>
    /// A file held open with no sharing is reported as locked, naming the folder being deleted and the file that showed it, and nothing
    /// is deleted. The delete runs on another thread so that a hang fails the test on the timeout instead of hanging it.
    /// </summary>
    /// <returns>The test's task.</returns>
    [Fact]
    public async Task LockedFileIsClassifiedAsLocked()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using var fx = new Fixture();
        string tree = fx.PathTo("tree");
        Directory.CreateDirectory(tree);
        string held = Path.Combine(tree, "held.txt");
        File.WriteAllText(held, "held");
        using var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);

        var deleting = Task.Run(() => PermanentDelete.Delete(tree), cancel);
        Task finished = await Task.WhenAny(deleting, Task.Delay(TimeSpan.FromSeconds(10), cancel));

        Assert.True(finished == deleting, "PermanentDelete.Delete did not return within 10 s");
        LockedException locked = await Assert.ThrowsAsync<LockedException>(() => deleting);
        Assert.True(Fixture.SamePath(locked.Path, tree), $"locked path: {locked.Path}");
        Assert.True(Fixture.SamePath(locked.FirstLockedFile!, held), $"first locked file: {locked.FirstLockedFile}");
        Assert.True(File.Exists(held), $"{held} was deleted");
    }

    /// <summary>A junction is removed as a link, leaving its target and the target's file where they are.</summary>
    [Fact]
    public void RemoveLinkRemovesJunctionAndKeepsTarget()
    {
        using var fx = new Fixture();
        string repo = fx.PathTo("live-repo");
        Directory.CreateDirectory(repo);
        string kept = Path.Combine(repo, "file.txt");
        File.WriteAllText(kept, "keep");
        Directory.CreateDirectory(fx.PathTo("x.wt"));
        string link = fx.PathTo("x.wt/yaat");
        Assert.SkipUnless(Fixture.MakeJunction(link, repo), "mklink /J is unavailable");

        PermanentDelete.RemoveLink(link);

        Assert.False(Directory.Exists(link), $"{link} still exists");
        Assert.True(File.Exists(kept), $"the link's target lost {kept}");
    }

    /// <summary>A plain folder is refused: it is no longer a link, and nothing is deleted.</summary>
    [Fact]
    public void RemoveLinkRefusesAPlainFolder()
    {
        using var fx = new Fixture();
        string folder = fx.PathTo("plain");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "file.txt");
        File.WriteAllText(file, "x");

        // Assert.Throws matches the exact type, so a LockedException (an IOException itself) would fail this test.
        IOException error = Assert.Throws<IOException>(() => PermanentDelete.RemoveLink(folder));

        Assert.Contains("no longer a link; left in place", error.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(folder) && File.Exists(file), $"{folder} was partly deleted");
    }

    /// <summary>A path that is not there fails as a "cannot read" failure, not as a lock.</summary>
    [Fact]
    public void DeleteMissingPathThrowsWithContext()
    {
        using var fx = new Fixture();
        string missing = fx.PathTo("missing");

        IOException error = Assert.Throws<IOException>(() => PermanentDelete.Delete(missing));

        Assert.StartsWith("cannot read", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A plain file deleted directly is removed.</summary>
    [Fact]
    public void DeleteRemovesASingleFile()
    {
        using var fx = new Fixture();
        string file = fx.PathTo("file.txt");
        File.WriteAllText(file, "x");

        PermanentDelete.Delete(file);

        Assert.False(File.Exists(file), $"{file} still exists");
    }

    /// <summary>A folder whose listing is denied is a lock that names that folder, not a generic "not empty" failure.</summary>
    [Fact]
    public void DeniedListingIsClassifiedAsLocked()
    {
        using var fx = new Fixture();
        string tree = fx.PathTo("tree");
        string sub = Path.Combine(tree, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "file.txt"), "x");
        var info = new DirectoryInfo(sub);
        DirectorySecurity open = info.GetAccessControl();
        try
        {
            DirectorySecurity denied = info.GetAccessControl();
            denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().Name, FileSystemRights.ListDirectory, AccessControlType.Deny));
            info.SetAccessControl(denied);

            LockedException locked = Assert.Throws<LockedException>(() => PermanentDelete.Delete(tree));

            Assert.True(Fixture.SamePath(locked.Path, tree), $"locked path: {locked.Path}");
            Assert.True(Fixture.SamePath(locked.FirstLockedFile!, sub), $"first locked file: {locked.FirstLockedFile}");
        }
        finally
        {
            info.SetAccessControl(open);
        }
        Assert.True(Directory.Exists(sub), $"{sub} was deleted");
    }

    /// <summary>
    /// A junction that is itself read-only is still removed as a link: the read-only bit is cleared on the link and not on its target.
    /// The target is read-only too, so clearing the bit through the link instead of on it would fail the attributes assertion.
    /// </summary>
    [Fact]
    public void ReadOnlyJunctionIsRemovedAsALink()
    {
        using var fx = new Fixture();
        string target = fx.PathTo("outside");
        Directory.CreateDirectory(target);
        string kept = Path.Combine(target, "keep.txt");
        File.WriteAllText(kept, "keep");
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        string link = fx.PathTo("link");
        Assert.SkipUnless(Fixture.MakeJunction(link, target), "mklink /J is unavailable");
        Assert.SkipUnless(MakeReadOnlyLink(link), "attrib is unavailable");
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReadOnly), $"{link} is not read-only");
        FileAttributes targetBefore = File.GetAttributes(target);

        PermanentDelete.RemoveLink(link);

        Assert.False(Directory.Exists(link), $"{link} still exists");
        Assert.True(File.Exists(kept), $"the junction's target lost {kept}");
        Assert.Equal(targetBefore, File.GetAttributes(target));
    }

    /// <summary>Removing a link that is not there is a read failure, not a "left in place" refusal.</summary>
    [Fact]
    public void RemoveLinkOnAMissingPathCannotRead()
    {
        using var fx = new Fixture();
        string missing = fx.PathTo("missing");

        IOException error = Assert.Throws<IOException>(() => PermanentDelete.RemoveLink(missing));

        Assert.StartsWith("cannot read", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A read-only file and a folder that are deleted between their folder's listing and their own delete are treated as already gone:
    /// clearing the read-only attribute of the file and removing the folder both find nothing.
    /// </summary>
    [Fact]
    public void AChildDeletedAfterTheListingIsAlreadyGone()
    {
        using var fx = new Fixture();
        string tree = fx.PathTo("tree");
        string sub = Path.Combine(tree, "sub");
        Directory.CreateDirectory(sub);
        string doomedFile = Path.Combine(sub, "doomed.txt");
        File.WriteAllText(doomedFile, "doomed");
        File.SetAttributes(doomedFile, File.GetAttributes(doomedFile) | FileAttributes.ReadOnly);
        string doomedDir = Path.Combine(sub, "doomed-dir");
        Directory.CreateDirectory(doomedDir);
        File.WriteAllText(Path.Combine(doomedDir, "file.txt"), "x");
        File.WriteAllText(Path.Combine(sub, "keep.txt"), "keep");
        PermanentDelete.AfterListing = dir =>
        {
            if (Fixture.SamePath(dir, sub))
            {
                Directory.Delete(doomedDir, recursive: true);
                File.SetAttributes(doomedFile, File.GetAttributes(doomedFile) & ~FileAttributes.ReadOnly);
                File.Delete(doomedFile);
            }
        };
        try
        {
            PermanentDelete.Delete(tree);
        }
        finally
        {
            PermanentDelete.AfterListing = null;
        }

        Assert.False(Directory.Exists(tree), $"{tree} still exists");
    }

    /// <summary>Sets the read-only attribute on the link itself with <c>attrib +r /L</c>, never on its target.</summary>
    /// <param name="link">The junction or symbolic link.</param>
    /// <returns><see langword="true"/> when attrib ran and succeeded.</returns>
    private static bool MakeReadOnlyLink(string link)
    {
        var startInfo = new ProcessStartInfo("cmd")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string[] args = ["/c", "attrib", "+r", "/L", link];
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("cmd did not start");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            string stderr = process.StandardError.ReadToEnd();
            _ = stdout.GetAwaiter().GetResult();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return true;
            }
            Console.Error.WriteLine($"skipping: attrib +r /L failed: {stderr.Trim()}");
            return false;
        }
        catch (Win32Exception error)
        {
            Console.Error.WriteLine($"skipping: cannot run cmd /c attrib: {error.Message}");
            return false;
        }
    }
}
