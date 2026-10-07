using System.Diagnostics;
using WorktreeSweep.Git;

namespace WorktreeSweep.Signals;

/// <summary>
/// A temporary object directory that borrows a repo's objects as an alternate, so a command that writes objects leaves the repo
/// untouched; deleted on dispose.
/// </summary>
/// <param name="dir">The scratch object directory.</param>
/// <param name="alternate">The repo's own object directory.</param>
internal sealed class ScratchObjects(string dir, string alternate) : IDisposable
{
    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    /// <summary>Gets the scratch object directory, for <c>GIT_OBJECT_DIRECTORY</c>.</summary>
    public string Dir { get; } = dir;

    /// <summary>Gets the repo's own object directory, for <c>GIT_ALTERNATE_OBJECT_DIRECTORIES</c>.</summary>
    public string Alternate { get; } = alternate;

    /// <summary>Creates a scratch object directory under the temp folder for the repo <paramref name="worktree"/> belongs to.</summary>
    /// <param name="worktree">A worktree of the repo.</param>
    /// <returns>The scratch directory.</returns>
    /// <exception cref="GitException">Git cannot find the repo's common dir.</exception>
    /// <exception cref="IOException">The scratch directory cannot be created; the message says so.</exception>
    public static ScratchObjects Create(string worktree)
    {
        string alternate = Path.Join(GitRunner.CommonDir(worktree), "objects");
        try
        {
            return new ScratchObjects(Directory.CreateTempSubdirectory("worktree-sweep-objects-").FullName, alternate);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"cannot create a scratch object directory: {error.Message}", error);
        }
    }

    /// <summary>Deletes the scratch directory; a failure is traced as a warning naming the directory, and the directory is left.</summary>
    public void Dispose()
    {
        try
        {
            DeleteTree(new DirectoryInfo(Dir));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"cannot remove scratch object directory {Dir}: {error.Message}");
        }
    }

    /// <summary>Deletes a folder, clearing the read-only attribute git puts on objects; a link inside is removed as a link, never walked.</summary>
    private static void DeleteTree(DirectoryInfo dir)
    {
        foreach (FileSystemInfo entry in dir.EnumerateFileSystemInfos("*", AllEntries))
        {
            if (entry is DirectoryInfo child && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                DeleteTree(child);
                continue;
            }
            entry.Attributes &= ~FileAttributes.ReadOnly;
            entry.Delete();
        }
        dir.Delete();
    }
}
