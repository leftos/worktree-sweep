namespace WorktreeSweep.Signals;

/// <summary>Disk usage of a folder, measured without following links.</summary>
public sealed record SizeInfo
{
    /// <summary>Gets the total bytes of the files.</summary>
    public long Bytes { get; init; }

    /// <summary>Gets the number of files.</summary>
    public long Files { get; init; }

    /// <summary>
    /// Gets the entries that could not be read; their size is not counted. When it is above 0, <see cref="Bytes"/> and
    /// <see cref="Files"/> are a lower bound: a folder whose listing fails partway has the rest of its entries skipped.
    /// </summary>
    public int Unreadable { get; init; }

    /// <summary>Gets the newest modification time seen (the folder itself included), in Unix seconds.</summary>
    public long? LastWriteUnix { get; init; }
}
