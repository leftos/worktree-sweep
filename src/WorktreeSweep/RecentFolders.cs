using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using WorktreeSweep.Report;

namespace WorktreeSweep;

/// <summary>
/// The folders scanned most recently, most recent first and at most <see cref="Cap"/>, kept in a JSON file. A file that cannot be
/// read is traced as a warning and read as empty; a write that fails is traced and not shown.
/// </summary>
public sealed class RecentFolders
{
    /// <summary>How many folders are kept.</summary>
    public const int Cap = 5;

    private const long Minute = 60;
    private const long Hour = 60 * Minute;
    private const long Day = 24 * Hour;

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>The file the folders are kept in.</summary>
    private readonly string path;

    private RecentFolders(string path, IReadOnlyList<RecentFolder> entries)
    {
        this.path = path;
        Entries = entries;
    }

    /// <summary>Gets the folders, most recent first.</summary>
    public IReadOnlyList<RecentFolder> Entries { get; private set; }

    /// <summary>Reads the folders kept in <paramref name="path"/>; none when the file does not exist.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The folders; none, with a trace warning, when the file cannot be read or is not a list of folders.</returns>
    public static RecentFolders Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new RecentFolders(path, Read(path));
    }

    /// <summary>
    /// Puts <paramref name="entry"/> first, its folder without a trailing separator (a drive root keeps its own), dropping an earlier
    /// entry for the same folder (compared case-insensitively) and any past <see cref="Cap"/>, then writes the file.
    /// </summary>
    /// <param name="entry">The completed scan.</param>
    public void Record(RecentFolder entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RecentFolder trimmed = entry with { Folder = Path.TrimEndingDirectorySeparator(entry.Folder) };
        Entries =
        [
            trimmed,
            .. Entries
                .Where(kept => !string.Equals(Path.TrimEndingDirectorySeparator(kept.Folder), trimmed.Folder, StringComparison.OrdinalIgnoreCase))
                .Take(Cap - 1),
        ];
        Write();
    }

    /// <summary>
    /// A recent folder's line: <c>{when} · {N} candidates · {size}</c>, or <c>{when} · nothing found</c> when the scan found none.
    /// </summary>
    /// <param name="entry">The folder.</param>
    /// <param name="nowUnix">The present, in Unix seconds.</param>
    /// <returns>The line.</returns>
    public static string Summary(RecentFolder entry, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string when = When(entry.ScannedUnix, nowUnix);
        if (entry.Candidates == 0)
        {
            return $"{when} · nothing found";
        }
        string candidates = entry.Candidates == 1 ? "1 candidate" : string.Create(CultureInfo.InvariantCulture, $"{entry.Candidates} candidates");
        return $"{when} · {candidates} · {ReportTable.HumanBytes(entry.Bytes)}";
    }

    /// <summary>
    /// How long ago <paramref name="thenUnix"/> was: <c>just now</c>, <c>{n} min ago</c>, <c>{n} h ago</c>, <c>yesterday</c>,
    /// <c>{n} days ago</c> under 14 days, or <c>{n} weeks ago</c>.
    /// </summary>
    /// <param name="thenUnix">The past time, in Unix seconds.</param>
    /// <param name="nowUnix">The present, in Unix seconds; a later <paramref name="thenUnix"/> reads as <c>just now</c>.</param>
    /// <returns>The text.</returns>
    public static string When(long thenUnix, long nowUnix)
    {
        long elapsed = Math.Max(0, nowUnix - thenUnix);
        return elapsed switch
        {
            < Minute => "just now",
            < Hour => string.Create(CultureInfo.InvariantCulture, $"{elapsed / Minute} min ago"),
            < Day => string.Create(CultureInfo.InvariantCulture, $"{elapsed / Hour} h ago"),
            < 2 * Day => "yesterday",
            < 14 * Day => string.Create(CultureInfo.InvariantCulture, $"{elapsed / Day} days ago"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{elapsed / (7 * Day)} weeks ago"),
        };
    }

    /// <summary>The folders in <paramref name="path"/>.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The folders; none when the file does not exist, and none with a trace warning when it cannot be read.</returns>
    private static IReadOnlyList<RecentFolder> Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }
            List<RecentFolder?> entries =
                JsonSerializer.Deserialize<List<RecentFolder?>>(File.ReadAllText(path), Options) ?? throw new JsonException("the list is null");
            if (entries.Any(entry => entry is null || string.IsNullOrEmpty(entry.Folder)))
            {
                throw new JsonException("an entry has no folder");
            }
            return [.. entries.OfType<RecentFolder>().Take(Cap)];
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"cannot read the recent folders from {path}, so none are listed: {error.Message}");
            return [];
        }
    }

    /// <summary>Writes the folders to the file, creating its folder; a failure is traced as a warning.</summary>
    private void Write()
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }
            File.WriteAllText(path, JsonSerializer.Serialize(Entries, Options) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"cannot save the recent folders to {path}: {error.Message}");
        }
    }
}
