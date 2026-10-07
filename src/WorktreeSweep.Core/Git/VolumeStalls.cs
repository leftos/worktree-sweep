namespace WorktreeSweep.Git;

/// <summary>
/// The volumes a git call has stalled, shared by one scan: a git call for a path on a stalled volume is never started, because it
/// would wait the full <see cref="GitRunner.CallTimeout"/> for a volume that has already proved it does not answer.
/// </summary>
/// <remarks>
/// <para>A stall belongs to a volume, not to a worktree: a hung network share stalls every repo and worktree on it, and each of them
/// would otherwise pay the whole time limit, plus the kill grace, before its own skip arms.</para>
/// <para>Only a <see cref="GitTimeoutException"/> marks a volume; any other git failure is that repo's own problem and leaves its
/// volume usable.</para>
/// <para>A path is keyed by the root it is spelled with (<c>D:\</c>, <c>\\server\share</c>), case-insensitively and with any trailing
/// separator normalized, and nothing is opened to key it: resolving a path would open a handle on the very volume that may be hung.
/// A subst drive and its target are therefore separate keys, which costs a scan of both at most one extra timeout, while a volume
/// mounted into a folder shares its host drive's key and costs none.</para>
/// <para>The scan's workers share one instance, so every member is thread-safe.</para>
/// </remarks>
public sealed class VolumeStalls
{
    private readonly HashSet<string> stalled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();

    /// <summary>
    /// The volume a path lies on: the root of the path made fully qualified (<c>D:\</c> for <c>D:\wt</c>, <c>\\server\share</c> for a
    /// share), without a trailing separator except a drive root's. Nothing is opened, so keying a path on a hung volume cannot hang
    /// in turn, and a path Windows will not make fully qualified (an invalid character such as NUL) is its own volume, as spelled.
    /// </summary>
    /// <param name="path">A path on the volume.</param>
    /// <returns>The volume's root, as the path spells it.</returns>
    public static string Volume(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string full = FullPathOrSpelled(path);
        return Path.GetPathRoot(full) is { Length: > 0 } root ? Normalize(root) : full;
    }

    /// <summary>Whether a path's volume is stalled, so git is not started for anything on it.</summary>
    /// <param name="path">A path on the volume.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool IsStalled(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return IsVolumeStalled(Volume(path));
    }

    /// <summary>Marks a path's volume as stalled, after a git call on it timed out.</summary>
    /// <param name="path">A path on the volume.</param>
    public void Mark(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        MarkVolume(Volume(path));
    }

    /// <summary>
    /// Fails for a path whose volume is stalled, before any git call is started for it, so the caller takes the same path it takes
    /// for a timeout without waiting the time limit first.
    /// </summary>
    /// <param name="path">A path on the volume.</param>
    /// <exception cref="GitTimeoutException">The volume is stalled; the message names it.</exception>
    public void ThrowIfStalled(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (IsStalled(path))
        {
            throw new GitTimeoutException($"git was not started for {path}: the volume {Volume(path)} is stalled by an earlier timeout");
        }
    }

    /// <summary>Whether a volume, already keyed by <see cref="Volume"/>, is stalled.</summary>
    /// <param name="volume">The volume's key.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    internal bool IsVolumeStalled(string volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        lock (gate)
        {
            return stalled.Contains(volume);
        }
    }

    /// <summary>Marks a volume, already keyed by <see cref="Volume"/>, as stalled.</summary>
    /// <param name="volume">The volume's key.</param>
    internal void MarkVolume(string volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        lock (gate)
        {
            _ = stalled.Add(volume);
        }
    }

    /// <summary>The path made fully qualified, or as spelled when Windows rejects it (an invalid character such as NUL).</summary>
    /// <remarks>The reads that use such a path report the failure themselves, so keying it adds no second report of its own.</remarks>
    private static string FullPathOrSpelled(string path)
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

    /// <summary>A root without a trailing separator, except a drive root's, which keeps its (<c>D:\</c>).</summary>
    private static string Normalize(string root)
    {
        string trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.EndsWith(Path.VolumeSeparatorChar) ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }
}
