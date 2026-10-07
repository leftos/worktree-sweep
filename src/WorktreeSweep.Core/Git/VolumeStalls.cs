using WorktreeSweep.Discovery;

namespace WorktreeSweep.Git;

/// <summary>
/// The volumes a git call has stalled, shared by one scan: a git call for a path on a stalled volume is never started, because it
/// would wait the full <see cref="GitRunner.CallTimeout"/> for a volume that has already proved it does not answer.
/// </summary>
/// <remarks>
/// <para>A stall belongs to a volume, not to a worktree: a hung network share stalls every repo and worktree on it, and each of them
/// would otherwise pay the whole time limit, plus the kill grace, before its own skip arms.</para>
/// <para>Only a <see cref="GitTimeoutException"/> marks a volume; any other git failure is that repo's own problem and leaves its
/// volume usable. A volume is keyed by its root, resolved first so a subst drive or an 8.3-spelled path names the volume it really
/// is: a drive (<c>D:\</c>) or a UNC share (<c>\\server\share</c>).</para>
/// <para>The scan's workers share one instance, so every member is thread-safe.</para>
/// </remarks>
public sealed class VolumeStalls
{
    private readonly HashSet<string> stalled = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    /// <summary>
    /// The volume a path lies on: its root (<c>D:\</c>, <c>\\server\share</c>), with the path resolved first, so a subst drive or a
    /// missing folder resolves to the volume it really is. A path with no root of its own is its own volume, as spelled.
    /// </summary>
    /// <param name="path">A path on the volume.</param>
    /// <returns>The volume's root, as the system spells it.</returns>
    public static string Volume(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string resolved = PathResolver.Resolve(path);
        return Path.GetPathRoot(resolved) is { Length: > 0 } root ? root : resolved;
    }

    /// <summary>The comparison key of a path's volume: <see cref="Volume"/> with its separators unified and its case folded.</summary>
    /// <param name="path">A path on the volume.</param>
    /// <returns>The key; equal keys mean one volume, however it is spelled.</returns>
    public static string Key(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Discoverer.PathKey(Volume(path));
    }

    /// <summary>Whether a path's volume is stalled, so git is not started for anything on it.</summary>
    /// <param name="path">A path on the volume.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool IsStalled(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Marked(Key(path));
    }

    /// <summary>Marks a path's volume as stalled, after a git call on it timed out.</summary>
    /// <param name="path">A path on the volume.</param>
    public void Mark(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string key = Key(path);
        lock (gate)
        {
            _ = stalled.Add(key);
        }
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
        string volume = Volume(path);
        if (Marked(Discoverer.PathKey(volume)))
        {
            throw new GitTimeoutException($"git was not started for {path}: the volume {volume} is stalled by an earlier timeout");
        }
    }

    private bool Marked(string key)
    {
        lock (gate)
        {
            return stalled.Contains(key);
        }
    }
}
