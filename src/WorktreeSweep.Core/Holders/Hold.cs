namespace WorktreeSweep.Holders;

/// <summary>One thing a process holds inside the folder.</summary>
/// <param name="Path">The held folder or file.</param>
public abstract record Hold(string Path)
{
    /// <summary>The process's current folder is the folder or lies under it.</summary>
    /// <param name="Path">The current folder, with 8.3 names expanded.</param>
    public sealed record CurrentFolder(string Path) : Hold(Path);

    /// <summary>The process has a file or folder open there.</summary>
    /// <param name="Path">The open file or folder.</param>
    public sealed record OpenHandle(string Path) : Hold(Path);
}
