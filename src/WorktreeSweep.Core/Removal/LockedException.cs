namespace WorktreeSweep.Removal;

/// <summary>
/// A removal failed because another process holds a file or folder in the tree open: a sharing violation or access denied.
/// </summary>
/// <param name="path">The folder being removed.</param>
/// <param name="firstLockedFile">The first file found locked, when known.</param>
/// <param name="innerException">The failure that showed the lock, such as the Shell call and its HRESULT; <see langword="null"/> when none.</param>
public sealed class LockedException(string path, string? firstLockedFile, Exception? innerException)
    : IOException(Describe(path, firstLockedFile), innerException)
{
    /// <summary>Initializes a new instance of the <see cref="LockedException"/> class with no inner exception.</summary>
    /// <param name="path">The folder being removed.</param>
    /// <param name="firstLockedFile">The first file found locked, when known.</param>
    public LockedException(string path, string? firstLockedFile)
        : this(path, firstLockedFile, innerException: null) { }

    /// <summary>Gets the folder being removed.</summary>
    public string Path { get; } = path;

    /// <summary>Gets the first file found locked; <see langword="null"/> when unknown.</summary>
    public string? FirstLockedFile { get; } = firstLockedFile;

    private static string Describe(string path, string? firstLockedFile) =>
        firstLockedFile is null ? $"{path} is locked by another process" : $"{path} is locked by another process ({firstLockedFile})";
}
