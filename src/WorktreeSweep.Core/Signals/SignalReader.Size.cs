using System.Diagnostics;
using WorktreeSweep.Discovery;

namespace WorktreeSweep.Signals;

/// <summary>The size half of <see cref="SignalReader"/>.</summary>
public static partial class SignalReader
{
    private static readonly EnumerationOptions DirectChildren = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
    };

    /// <summary>
    /// Measures a folder without following junctions or symbolic links: a link inside is never entered or counted, and a link given
    /// as <paramref name="root"/> measures as empty. Hidden and system entries are counted. An unreadable entry is counted in
    /// <see cref="SizeInfo.Unreadable"/>, not fatal; a root that is a file, or that cannot be read, counts as one.
    /// </summary>
    /// <param name="root">The folder to measure.</param>
    /// <returns>Its size.</returns>
    public static SizeInfo WalkSize(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        DirectoryInfo rootEntry;
        long? lastWrite;
        try
        {
            rootEntry = new DirectoryInfo(root);
            // GetLastWriteTimeUtc reports a missing path as 1601 instead of throwing; GetAttributes throws.
            _ = File.GetAttributes(root);
            lastWrite = UnixSeconds(File.GetLastWriteTimeUtc(root));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.WriteLine($"cannot read {root}: {error.Message}");
            return new SizeInfo { Unreadable = 1 };
        }
        if (Discoverer.IsLink(rootEntry))
        {
            return new SizeInfo { LastWriteUnix = lastWrite };
        }
        long bytes = 0;
        long files = 0;
        int unreadable = 0;
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out string? dir))
        {
            (long Bytes, long Files, bool Unreadable, long? LastWrite) folder = TallyChildren(dir, pending);
            bytes += folder.Bytes;
            files += folder.Files;
            unreadable += folder.Unreadable ? 1 : 0;
            lastWrite = Later(lastWrite, folder.LastWrite);
        }
        return new SizeInfo
        {
            Bytes = bytes,
            Files = files,
            Unreadable = unreadable,
            LastWriteUnix = lastWrite,
        };
    }

    /// <summary>
    /// Totals the files directly in <paramref name="dir"/> and queues its folders that are not links. A listing that fails, at the
    /// start or partway, is unreadable and keeps what it counted before the failure.
    /// </summary>
    private static (long Bytes, long Files, bool Unreadable, long? LastWrite) TallyChildren(string dir, Stack<string> pending)
    {
        long bytes = 0;
        long files = 0;
        long? lastWrite = null;
        try
        {
            foreach (FileSystemInfo entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", DirectChildren))
            {
                lastWrite = Later(lastWrite, ModifiedUnix(entry));
                if (Discoverer.IsLink(entry))
                {
                    continue;
                }
                if (entry is FileInfo file)
                {
                    files++;
                    bytes += file.Length;
                }
                else
                {
                    pending.Push(entry.FullName);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"cannot list {dir}: {error.Message}");
            return (bytes, files, true, lastWrite);
        }
        return (bytes, files, false, lastWrite);
    }
}
