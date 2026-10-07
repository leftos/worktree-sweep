using WorktreeSweep.Discovery;

namespace WorktreeSweep.Removal;

/// <summary>
/// Deletes a folder for good. The walk never enters a junction or symbolic link: each is deleted as a link, so its target survives.
/// Read-only entries, which git sets on its object files, are cleared first, and an entry that turns out to be gone is treated as
/// already deleted.
/// </summary>
public static class PermanentDelete
{
    /// <summary><c>HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION)</c>.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED)</c>, which is also <c>E_ACCESSDENIED</c>.</summary>
    private const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>What <see cref="FileSystemInfo.Attributes"/> returns when the path is not there.</summary>
    private const FileAttributes Missing = unchecked((FileAttributes)(-1));

    /// <summary>
    /// Called with each folder's path right after its children are listed and before any of them is deleted, so that a test can remove
    /// a child out from under the walk.
    /// </summary>
    internal static Action<string>? AfterListing { get; set; }

    /// <summary>
    /// Deletes <paramref name="path"/> for good: a link is removed as a link, a folder is emptied and removed, a file is deleted.
    /// </summary>
    /// <param name="path">The file, folder, junction or symbolic link to delete.</param>
    /// <exception cref="LockedException">
    /// A file or folder in the tree is in use. Its <see cref="LockedException.Path"/> is <paramref name="path"/> and its
    /// <see cref="LockedException.FirstLockedFile"/> is the entry that showed the lock, and nothing further is deleted.
    /// </exception>
    /// <exception cref="IOException">
    /// <paramref name="path"/> is not there, or reading, listing or deleting an entry failed for any other reason. An entry that is
    /// gone by the time it is cleared or removed counts as deleted.
    /// </exception>
    public static void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FileSystemInfo entry = Metadata(path, path);
        if (Discoverer.IsLink(entry))
        {
            Unlink(path, path, entry);
        }
        else if (entry.Attributes.HasFlag(FileAttributes.Directory))
        {
            DeleteTree(path, path, entry);
        }
        else
        {
            DeleteFile(path, path, entry);
        }
    }

    /// <summary>
    /// Deletes the junction or symbolic link at <paramref name="path"/>, never its target, after checking that it still is a link.
    /// </summary>
    /// <param name="path">The link to remove.</param>
    /// <exception cref="IOException">
    /// <paramref name="path"/> is not there, is not a link any more, or could not be deleted. A lock comes as a
    /// <see cref="LockedException"/>.
    /// </exception>
    public static void RemoveLink(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FileSystemInfo entry = Metadata(path, path);
        if (!Discoverer.IsLink(entry))
        {
            throw new IOException($"{path} is no longer a link; left in place");
        }
        Unlink(path, path, entry);
    }

    /// <summary>
    /// Fails when <paramref name="path"/>, a folder at scan time, is now a junction or symbolic link: recycling or deleting it as a
    /// folder would act on a link the user never picked.
    /// </summary>
    /// <param name="path">The folder about to be recycled or deleted.</param>
    /// <exception cref="LockedException">Reading it hit a sharing violation or was denied.</exception>
    /// <exception cref="IOException">
    /// It is a link now (<c>&lt;path&gt; became a link since the scan; left in place</c>), or it cannot be read.
    /// </exception>
    internal static void RejectLink(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (Discoverer.IsLink(Metadata(path, path)))
        {
            throw new IOException($"{path} became a link since the scan; left in place");
        }
    }

    /// <summary>Empties <paramref name="dir"/> child by child, then clears its read-only attribute and removes the folder itself.</summary>
    /// <param name="root">The path the caller asked to delete, named by a lock.</param>
    /// <param name="dir">The folder being emptied.</param>
    /// <param name="entry">The folder's metadata, read without following a link.</param>
    private static void DeleteTree(string root, string dir, FileSystemInfo entry)
    {
        List<FileSystemInfo> children = StepGone(root, dir, "cannot list", [], () => Children(dir));
        AfterListing?.Invoke(dir);
        foreach (FileSystemInfo child in children)
        {
            DeleteChild(root, child);
        }
        ClearReadOnly(root, dir, entry);
        StepGone(root, dir, "cannot delete folder", () => Directory.Delete(dir, recursive: false));
    }

    /// <summary>Removes one child of a folder: a link as a link, a folder by walking it, a file by deleting it.</summary>
    /// <param name="root">The path the caller asked to delete, named by a lock.</param>
    /// <param name="child">The child, as its folder's enumeration returned it.</param>
    private static void DeleteChild(string root, FileSystemInfo child)
    {
        if (Discoverer.IsLink(child))
        {
            Unlink(root, child.FullName, child);
        }
        else if (child.Attributes.HasFlag(FileAttributes.Directory))
        {
            DeleteTree(root, child.FullName, child);
        }
        else
        {
            DeleteFile(root, child.FullName, child);
        }
    }

    /// <summary>Clears the file's read-only attribute when it is set, then deletes the file.</summary>
    /// <param name="root">The path the caller asked to delete, named by a lock.</param>
    /// <param name="path">The file to delete.</param>
    /// <param name="entry">The file's metadata, read without following a link.</param>
    private static void DeleteFile(string root, string path, FileSystemInfo entry)
    {
        ClearReadOnly(root, path, entry);
        StepGone(root, path, "cannot delete", () => File.Delete(path));
    }

    /// <summary>
    /// Deletes a link itself: <see cref="Directory.Delete(string, bool)"/> without recursion for a junction or a directory link, which
    /// removes the reparse point and not its target, and <see cref="File.Delete(string)"/> for a file link. The link's own read-only
    /// attribute, which blocks removing it, is cleared first; a link is never cleared through to its target.
    /// </summary>
    /// <param name="root">The path the caller asked to delete, named by a lock.</param>
    /// <param name="path">The link to remove.</param>
    /// <param name="entry">The link's metadata, read without following it.</param>
    private static void Unlink(string root, string path, FileSystemInfo entry)
    {
        ClearReadOnly(root, path, entry);
        bool directory = entry.Attributes.HasFlag(FileAttributes.Directory);
        StepGone(
            root,
            path,
            "cannot delete link",
            () =>
            {
                if (directory)
                {
                    Directory.Delete(path, recursive: false);
                }
                else
                {
                    File.Delete(path);
                }
            }
        );
    }

    /// <summary>
    /// Clears only the read-only attribute, when set, so that git's read-only files and folders can be deleted. On a link the attribute
    /// belongs to the link itself, which is the one that blocks removing it.
    /// </summary>
    /// <param name="root">The path the caller asked to delete, named by a lock.</param>
    /// <param name="path">The entry to clear.</param>
    /// <param name="entry">The entry's metadata, read without following a link.</param>
    private static void ClearReadOnly(string root, string path, FileSystemInfo entry)
    {
        FileAttributes attributes = entry.Attributes;
        if (!attributes.HasFlag(FileAttributes.ReadOnly))
        {
            return;
        }
        StepGone(root, path, "cannot clear read-only on", () => File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly));
    }

    /// <summary>
    /// The folder's children, including hidden and system entries, materialized before any of them is deleted. A folder the process
    /// cannot list fails the step rather than looking empty.
    /// </summary>
    /// <param name="dir">The folder to list.</param>
    /// <returns>Its children, each with the attributes its enumeration read.</returns>
    private static List<FileSystemInfo> Children(string dir) => [.. new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Discoverer.AllEntries)];

    /// <summary>Reads the entry at <paramref name="path"/> without following a link.</summary>
    /// <param name="path">The entry's path.</param>
    /// <returns>A <see cref="DirectoryInfo"/> for a folder, a <see cref="FileInfo"/> for anything else.</returns>
    /// <exception cref="FileNotFoundException">Nothing is there to read.</exception>
    private static FileSystemInfo Read(string path)
    {
        var file = new FileInfo(path);
        FileAttributes attributes = file.Attributes;
        if (attributes == Missing)
        {
            throw new FileNotFoundException($"could not find file '{path}'", path);
        }
        return attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(path) : file;
    }

    /// <summary>Reads the entry's metadata without following a link, naming <paramref name="path"/> with "cannot read" when that fails.</summary>
    /// <param name="root">The path the caller asked to delete, named by a lock.</param>
    /// <param name="path">The entry's path.</param>
    /// <returns>The entry.</returns>
    private static FileSystemInfo Metadata(string root, string path)
    {
        try
        {
            return Read(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Map(root, path, "cannot read", error);
        }
    }

    /// <summary>
    /// Runs one step that clears or deletes an entry, treating an entry that is already gone as deleted, and turning its other failures
    /// into the exception <see cref="Map"/> picks.
    /// </summary>
    /// <typeparam name="T">What the step returns.</typeparam>
    /// <param name="root">The path the caller asked to delete, named by a lock.</param>
    /// <param name="path">The entry the step works on.</param>
    /// <param name="what">What the step attempts, for a failure that is not a lock.</param>
    /// <param name="gone">What to return when the entry is already gone.</param>
    /// <param name="operation">The step.</param>
    /// <returns>What the step returned.</returns>
    private static T StepGone<T>(string root, string path, string what, T gone, Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return gone;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Map(root, path, what, error);
        }
    }

    /// <summary>Runs one step that clears or deletes an entry, treating an entry that is already gone as deleted.</summary>
    /// <param name="root">The path the caller asked to delete, named by a lock.</param>
    /// <param name="path">The entry the step works on.</param>
    /// <param name="what">What the step attempts, for a failure that is not a lock.</param>
    /// <param name="operation">The step.</param>
    private static void StepGone(string root, string path, string what, Action operation) =>
        _ = StepGone(
            root,
            path,
            what,
            false,
            () =>
            {
                operation();
                return true;
            }
        );

    /// <summary>
    /// The failure for <paramref name="error"/> while removing <paramref name="root"/>: a <see cref="LockedException"/> when the entry
    /// at <paramref name="path"/> is held open or denied, otherwise an <see cref="IOException"/> that says what was attempted on
    /// which path.
    /// </summary>
    /// <param name="root">The path the caller asked to delete, named by a lock.</param>
    /// <param name="path">The entry the failed step worked on.</param>
    /// <param name="what">What the step attempted.</param>
    /// <param name="error">The failure itself.</param>
    /// <returns>The exception to throw.</returns>
    private static Exception Map(string root, string path, string what, Exception error) =>
        error.HResult is SharingViolation or AccessDenied
            ? new LockedException(root, path, error)
            : new IOException($"{what} {path}: {error.Message}", error);
}
