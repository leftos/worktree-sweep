using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace WorktreeSweep.Discovery;

/// <summary>Resolves a path to the name the system gives it, so two spellings of one folder compare equal.</summary>
internal static class PathResolver
{
    /// <summary>The first path buffer size tried, in characters; a longer path grows it once.</summary>
    private const int PathBufferSize = 1024;

    /// <summary>
    /// The path fully resolved (junctions, symbolic links, subst drives and 8.3 names included) and without a <c>\\?\</c> prefix.
    /// </summary>
    /// <remarks>
    /// A missing path resolves its nearest existing ancestor and keeps the missing segments as spelled. When even that cannot be
    /// resolved, or the path is empty or invalid, a trace warning names the path and it is returned made absolute, as spelled, or as
    /// given when it cannot be made absolute. It never throws.
    /// </remarks>
    /// <param name="path">A path.</param>
    /// <returns>The resolved path.</returns>
    internal static string Resolve(string path)
    {
        try
        {
            string existing = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var missing = new Stack<string>();
            while (!Path.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
            {
                missing.Push(Path.GetFileName(existing));
                existing = parent;
            }
            string resolved = Discoverer.StripVerbatim(FinalPath(existing));
            foreach (string segment in missing)
            {
                resolved = Path.Join(resolved, segment);
            }
            return resolved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning($"cannot resolve {path}: {error.Message}; comparing it as spelled");
            return AsSpelled(path);
        }
    }

    /// <summary>The path made absolute, or as given when it cannot be; the caller has already traced why it was not resolved.</summary>
    private static string AsSpelled(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary><paramref name="path"/> with 8.3 names expanded, or as given when that fails.</summary>
    /// <param name="path">A path.</param>
    /// <returns>The long form.</returns>
    internal static string LongPath(string path)
    {
        char[] buffer = new char[PathBufferSize];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint length = PInvoke.GetLongPathName(path, buffer);
            if (length == 0)
            {
                break;
            }
            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }
            buffer = new char[length];
        }
        return path;
    }

    /// <summary>
    /// The folder fully resolved, junctions, symbolic links, subst drives and 8.3 names included, as the system names it: with a
    /// <c>\\?\</c> prefix.
    /// </summary>
    /// <param name="folder">An existing folder.</param>
    /// <returns>The resolved path.</returns>
    /// <exception cref="IOException">The folder cannot be opened or its final path read.</exception>
    internal static string FinalPath(string folder)
    {
        using SafeFileHandle handle = PInvoke.CreateFile(
            folder,
            0,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS,
            null
        );
        if (handle.IsInvalid)
        {
            throw new IOException($"cannot resolve {folder}", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        char[] buffer = new char[PathBufferSize];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint length = PInvoke.GetFinalPathNameByHandle(handle, buffer, GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED);
            if (length == 0)
            {
                throw new IOException($"cannot resolve {folder}", new Win32Exception(Marshal.GetLastPInvokeError()));
            }
            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }
            buffer = new char[length];
        }
        throw new IOException($"cannot resolve {folder}: its resolved path kept growing");
    }
}
