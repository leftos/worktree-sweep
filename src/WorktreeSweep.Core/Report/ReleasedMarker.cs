using System.Diagnostics;
using System.Text.Json;
using WorktreeSweep.Discovery;

namespace WorktreeSweep.Report;

/// <summary>Finds a worktree's admin dir and reads the released marker in it.</summary>
/// <remarks>
/// A missing marker reads as <see langword="null"/>. So does a marker that cannot be read or does not parse (an unknown reason, a
/// missing field, a time outside what a timestamp can hold), with a <see cref="Trace.TraceWarning(string)"/>.
/// </remarks>
public static class ReleasedMarker
{
    /// <summary>The marker's file name in a worktree's admin dir (<c>&lt;repo&gt;\.git\worktrees\&lt;id&gt;</c>).</summary>
    public const string FileName = "worktree-sweep-released.json";

    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    private static readonly JsonSerializerOptions MarkerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
    };

    /// <summary>The released marker of a worktree: <see cref="AdminDir"/>, then <see cref="Read"/>.</summary>
    /// <param name="commonDir">The repo's common git dir; <see langword="null"/> when unknown.</param>
    /// <param name="worktree">The worktree folder, which may be gone.</param>
    /// <returns>The marker; <see langword="null"/> when there is no admin dir, no marker, or the marker is unreadable.</returns>
    public static Released? Find(string? commonDir, string worktree)
    {
        string? admin = AdminDir(commonDir, worktree);
        return admin is null ? null : Read(admin);
    }

    /// <summary>
    /// A worktree's admin dir: from its <c>.git</c> file, or, when the folder or that file is gone (a prunable registration), the
    /// entry under <c>&lt;commonDir&gt;\worktrees</c> whose <c>gitdir</c> file points at <c>&lt;worktree&gt;\.git</c>. A relative
    /// <c>gitdir</c> (<c>worktree.useRelativePaths</c>) is resolved against the entry.
    /// </summary>
    /// <param name="commonDir">The repo's common git dir (<c>git rev-parse --git-common-dir</c>); <see langword="null"/> when unknown.</param>
    /// <param name="worktree">
    /// The worktree folder; compared after <see cref="PathResolver.Resolve"/>, so any spelling of the folder (<c>.</c> and <c>..</c>
    /// folded, a subst drive, an 8.3 name, a <c>\\?\</c> prefix and a junction included) matches.
    /// </param>
    /// <returns>The admin dir; <see langword="null"/> when none is found.</returns>
    /// <exception cref="ArgumentException"><paramref name="commonDir"/> is not a full path, as git prints it relative to where it ran.</exception>
    public static string? AdminDir(string? commonDir, string worktree)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        if (commonDir is not null && !Path.IsPathFullyQualified(commonDir))
        {
            throw new ArgumentException($"the common git dir must be a full path, not \"{commonDir}\"", nameof(commonDir));
        }
        string? admin = Discoverer.ReadGitdirFile(worktree);
        if (admin is not null || commonDir is null)
        {
            return admin;
        }
        string wanted = Discoverer.PathKey(PathResolver.Resolve(worktree));
        return AdminEntries(Path.Join(commonDir, "worktrees")).FirstOrDefault(entry => PointsAt(entry, wanted));
    }

    /// <summary>Reads the marker in an admin dir.</summary>
    /// <param name="adminDir">The worktree's admin dir.</param>
    /// <returns>The marker; <see langword="null"/> when there is none, or, with a trace warning, when it cannot be read or parsed.</returns>
    public static Released? Read(string adminDir)
    {
        ArgumentNullException.ThrowIfNull(adminDir);
        string path = Path.Join(adminDir, FileName);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"ignoring the released marker: cannot read {path}: {error.Message}");
            return null;
        }
        try
        {
            Released released = JsonSerializer.Deserialize<Released>(text, MarkerOptions) ?? throw new JsonException("the marker is null");
            _ = DateTimeOffset.FromUnixTimeSeconds(released.ReleasedAtUnix);
            return released;
        }
        catch (Exception error) when (error is JsonException or ArgumentOutOfRangeException)
        {
            Trace.TraceWarning($"ignoring the released marker: {path} is not a released marker: {error.Message}");
            return null;
        }
    }

    private static IEnumerable<string> AdminEntries(string worktreesDir)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(worktreesDir, "*", AllEntries).Order(StringComparer.Ordinal)];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"cannot list {worktreesDir}: {error.Message}");
            return [];
        }
    }

    /// <summary>
    /// Whether the <c>gitdir</c> file of an admin entry points at the <c>.git</c> of the worktree keyed <paramref name="wanted"/>. The
    /// entry is a full path (the common dir is), so a relative target joined to it is one too, and <see cref="Path.GetFullPath(string)"/>
    /// only folds its <c>.</c> and <c>..</c>, never consulting the current directory.
    /// </summary>
    private static bool PointsAt(string entry, string wanted)
    {
        string gitdirFile = Path.Join(entry, "gitdir");
        string text;
        try
        {
            text = File.ReadAllText(gitdirFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"cannot read {gitdirFile}: {error.Message}");
            return false;
        }
        string target = Discoverer.FromGitPath(text.Trim());
        string dotGit = Path.GetFullPath(Path.IsPathFullyQualified(target) ? target : Path.Join(entry, target));
        string? worktree = Path.GetDirectoryName(dotGit);
        return worktree is not null && string.Equals(Discoverer.PathKey(PathResolver.Resolve(worktree)), wanted, StringComparison.Ordinal);
    }
}
