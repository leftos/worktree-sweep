using System.Globalization;
using System.Text;
using WorktreeSweep.Discovery;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Report;

/// <summary>The plain-text table <c>--list</c> prints.</summary>
/// <remarks>
/// Columns <c>#</c>, PATH, KIND, BRANCH, MERGE, DIRTY, UPSTREAM, ACTIVE, SIZE and FLAGS, two spaces apart; <c>#</c>, ACTIVE and SIZE
/// are right-aligned. Every column is as wide as its widest cell except PATH, which takes the room left of <see cref="Width"/> (at
/// least 24) and loses its start to <c>…</c> when longer. Widths count Unicode scalar values; numbers use the invariant culture.
/// </remarks>
public static class ReportTable
{
    /// <summary>The widest line the table is laid out for.</summary>
    public const int Width = 150;

    private const string Separator = "  ";
    private const int MinPathWidth = 24;
    private const int MaxBranchWidth = 28;
    private const int PathColumn = 1;
    private const long Hour = 3600;
    private const long Day = 24 * Hour;

    private static readonly string[] Units = ["KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Renders the report's candidates as a table no wider than <see cref="Width"/>, its rows in <see cref="ScanReport.Ordered"/>
    /// order and numbered from 1, each line ending in <c>\n</c>.
    /// </summary>
    /// <param name="report">The report.</param>
    /// <param name="nowUnix">The time ages are measured from, in Unix seconds.</param>
    /// <returns>The table, or a one-line message when there are no candidates.</returns>
    public static string Render(ScanReport report, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(report);
        IReadOnlyList<Candidate> candidates = report.Ordered();
        if (candidates.Count == 0)
        {
            return $"No worktrees or orphan folders found under {report.Root}.\n";
        }
        Row[] rows = [.. candidates.Select(candidate => RowOf(candidate, report.Root, nowUnix))];
        Column[] columns =
        [
            new("#", true, [.. Enumerable.Range(1, rows.Length).Select(index => index.ToString(CultureInfo.InvariantCulture))]),
            new("PATH", false, [.. rows.Select(row => row.Path)]),
            new("KIND", false, [.. rows.Select(row => row.Kind)]),
            new("BRANCH", false, [.. rows.Select(row => row.Branch)]),
            new("MERGE", false, [.. rows.Select(row => row.Merge)]),
            new("DIRTY", false, [.. rows.Select(row => row.Dirty)]),
            new("UPSTREAM", false, [.. rows.Select(row => row.Upstream)]),
            new("ACTIVE", true, [.. rows.Select(row => row.Active)]),
            new("SIZE", true, [.. rows.Select(row => row.Size)]),
            new("FLAGS", false, [.. rows.Select(row => row.Flags)]),
        ];
        var table = new StringBuilder();
        foreach (string line in Layout(columns, rows.Length))
        {
            table.Append(line).Append('\n');
        }
        return table.ToString();
    }

    /// <summary>
    /// <paramref name="path"/> relative to <paramref name="root"/> when it lies under it, the two compared by
    /// <see cref="Discoverer.PathKey"/>; else <paramref name="path"/> whole, as it is when it is the root itself. The relative part
    /// keeps the separators <paramref name="path"/> was written with, without a trailing one.
    /// </summary>
    /// <param name="path">The path to show.</param>
    /// <param name="root">The scanned root.</param>
    /// <returns>The path to show.</returns>
    public static string RelativePath(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);
        string rootKey = Discoverer.PathKey(root);
        string prefix = rootKey.EndsWith('\\') ? rootKey : rootKey + '\\';
        string pathKey = Discoverer.PathKey(path);
        bool under = pathKey.Length > prefix.Length && pathKey.StartsWith(prefix, StringComparison.Ordinal);
        return under ? path[prefix.Length..pathKey.Length] : path;
    }

    /// <summary>A byte count with one decimal in binary units (<c>512 B</c>, <c>1.5 KB</c>, <c>28.9 GB</c>), rounded half up.</summary>
    /// <param name="bytes">The byte count; not negative.</param>
    /// <returns>The text.</returns>
    public static string HumanBytes(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }
        var count = (UInt128)bytes;
        UInt128 unitSize = 1024;
        string unit = Units[0];
        for (int index = 1; index < Units.Length; index++)
        {
            UInt128 next = UInt128.One << (10 * (index + 1));
            if (count < next)
            {
                break;
            }
            unitSize = next;
            unit = Units[index];
        }
        UInt128 tenths = ((count * 10) + (unitSize / 2)) / unitSize;
        return string.Create(CultureInfo.InvariantCulture, $"{tenths / 10}.{tenths % 10} {unit}");
    }

    /// <summary>
    /// How long ago <paramref name="thenUnix"/> was, in the largest fitting unit: <c>5m</c>, <c>7h</c>, <c>3d</c>, <c>5w</c>, <c>2y</c>.
    /// </summary>
    /// <param name="thenUnix">The past time, in Unix seconds.</param>
    /// <param name="nowUnix">
    /// The present, in Unix seconds; a later <paramref name="thenUnix"/> reads as <c>0m</c>, and a gap wider than <see cref="long"/>
    /// holds as <see cref="long.MaxValue"/> seconds.
    /// </param>
    /// <returns>The age.</returns>
    public static string Age(long thenUnix, long nowUnix)
    {
        long elapsed = Elapsed(thenUnix, nowUnix);
        (long Count, char Unit) age =
            elapsed < Hour ? (elapsed / 60, 'm')
            : elapsed < 2 * Day ? (elapsed / Hour, 'h')
            : elapsed < 14 * Day ? (elapsed / Day, 'd')
            : elapsed < 365 * Day ? (elapsed / (7 * Day), 'w')
            : (elapsed / (365 * Day), 'y');
        return string.Create(CultureInfo.InvariantCulture, $"{age.Count}{age.Unit}");
    }

    /// <summary><c>nowUnix - thenUnix</c>, at least 0 and saturating at <see cref="long.MaxValue"/>.</summary>
    private static long Elapsed(long thenUnix, long nowUnix)
    {
        if (thenUnix >= nowUnix)
        {
            return 0;
        }
        long difference = unchecked(nowUnix - thenUnix);
        return difference < 0 ? long.MaxValue : difference;
    }

    private static Row RowOf(Candidate candidate, string root, long nowUnix) =>
        candidate switch
        {
            RegisteredCandidate registered => RegisteredRow(registered, root, nowUnix),
            OrphanCandidate orphan => OrphanRow(orphan, root, nowUnix),
            _ => throw new ArgumentException($"unknown candidate type {candidate.GetType().Name}", nameof(candidate)),
        };

    private static Row RegisteredRow(RegisteredCandidate registered, string root, long nowUnix)
    {
        WorktreeSignals signals = registered.Signals;
        return new Row
        {
            Path = RelativePath(registered.Path, root),
            Kind = "worktree",
            Branch = TruncateEnd(registered.Record.Branch ?? "", MaxBranchWidth),
            Merge = signals.MergeState is null ? "" : MergeWord(signals.MergeState),
            Dirty = signals.Dirty is null ? "" : DirtyCell(signals.Dirty),
            Upstream = UpstreamCell(signals.Upstream),
            Active = signals.LastActivityUnix is long then ? Age(then, nowUnix) : "",
            Size = signals.Size is null ? "" : HumanBytes(signals.Size.Bytes),
            Flags = RegisteredFlags(registered),
        };
    }

    private static string RegisteredFlags(RegisteredCandidate registered)
    {
        var flags = new List<string>();
        if (registered.Released is not null)
        {
            flags.Add("released");
        }
        if (registered.Record.Locked is not null)
        {
            flags.Add("git-locked");
        }
        if (registered.Record.Prunable is not null)
        {
            flags.Add("prunable");
        }
        return string.Join(", ", flags);
    }

    private static Row OrphanRow(OrphanCandidate candidate, string root, long nowUnix)
    {
        Orphan orphan = candidate.Orphan;
        bool isLink = orphan.Kind == OrphanKind.Link;
        var flags = new List<string>();
        if (orphan.StaleGitdir)
        {
            flags.Add("stale .git");
        }
        if (orphan.LiveGitdir is not null)
        {
            flags.Add("registered elsewhere");
        }
        return new Row
        {
            Path = RelativePath(orphan.Path, root),
            Kind = isLink ? "link" : "orphan",
            Active = candidate.Size.LastWriteUnix is long then ? Age(then, nowUnix) : "",
            Size = isLink ? "" : HumanBytes(candidate.Size.Bytes),
            Flags = string.Join(", ", flags),
        };
    }

    private static string MergeWord(MergeState state) =>
        state.Kind switch
        {
            MergeStateKind.Ancestor => "merged",
            MergeStateKind.NoCommits => "no commits",
            MergeStateKind.PatchesApplied => "cherry-picked",
            MergeStateKind.ContentContained => "squashed",
            MergeStateKind.Unmerged => string.Create(CultureInfo.InvariantCulture, $"unmerged {state.Commits}"),
            MergeStateKind.Detached => "detached",
            _ => throw new ArgumentException($"unknown merge state kind {state.Kind}", nameof(state)),
        };

    private static string DirtyCell(Dirty dirty)
    {
        var parts = new List<string>();
        if (dirty.Modified > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{dirty.Modified}M"));
        }
        if (dirty.Untracked > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{dirty.Untracked}?"));
        }
        return string.Join(' ', parts);
    }

    private static string UpstreamCell(Upstream? upstream) =>
        upstream switch
        {
            { Kind: UpstreamKind.Gone } => "gone",
            { Kind: UpstreamKind.Tracking, Ahead: > 0 } => string.Create(CultureInfo.InvariantCulture, $"+{upstream.Ahead}"),
            _ => "",
        };

    /// <summary>The header line, then one line per row, each column padded to its width; PATH is fitted into the room left.</summary>
    private static IEnumerable<string> Layout(Column[] columns, int rowCount)
    {
        int[] widths = [.. columns.Select(column => column.Cells.Select(CharLen).Append(CharLen(column.Header)).Max())];
        int others = widths.Where((_, index) => index != PathColumn).Sum() + (Separator.Length * (columns.Length - 1));
        int room = Math.Max(Width - others, MinPathWidth);
        widths[PathColumn] = Math.Min(widths[PathColumn], room);

        yield return JoinCells(columns, widths, [.. columns.Select(column => column.Header)]);
        for (int row = 0; row < rowCount; row++)
        {
            string[] cells =
            [
                .. columns.Select((column, index) => index == PathColumn ? TruncateStart(column.Cells[row], widths[index]) : column.Cells[row]),
            ];
            yield return JoinCells(columns, widths, cells);
        }
    }

    private static string JoinCells(Column[] columns, int[] widths, string[] cells)
    {
        var line = new StringBuilder();
        for (int index = 0; index < columns.Length; index++)
        {
            if (index > 0)
            {
                line.Append(Separator);
            }
            string padding = new(' ', Math.Max(widths[index] - CharLen(cells[index]), 0));
            line.Append(columns[index].RightAligned ? padding + cells[index] : cells[index] + padding);
        }
        return line.ToString().TrimEnd();
    }

    private static int CharLen(string text) => text.EnumerateRunes().Count();

    /// <summary>
    /// Keeps the last <c>width - 1</c> characters behind a <c>…</c> when <paramref name="text"/> is wider than <paramref name="width"/>.
    /// </summary>
    private static string TruncateStart(string text, int width)
    {
        int length = CharLen(text);
        return length <= width ? text : "…" + Concat(text.EnumerateRunes().Skip(length - Math.Max(width - 1, 0)));
    }

    /// <summary>
    /// Keeps the first <c>width - 1</c> characters before a <c>…</c> when <paramref name="text"/> is wider than <paramref name="width"/>.
    /// </summary>
    private static string TruncateEnd(string text, int width) =>
        CharLen(text) <= width ? text : Concat(text.EnumerateRunes().Take(Math.Max(width - 1, 0))) + "…";

    private static string Concat(IEnumerable<Rune> runes) => string.Concat(runes.Select(rune => rune.ToString()));

    /// <summary>The table cells of one candidate; a cell a candidate has no value for is empty.</summary>
    private sealed record Row
    {
        public required string Path { get; init; }

        public required string Kind { get; init; }

        public string Branch { get; init; } = "";

        public string Merge { get; init; } = "";

        public string Dirty { get; init; } = "";

        public string Upstream { get; init; } = "";

        public string Active { get; init; } = "";

        public string Size { get; init; } = "";

        public string Flags { get; init; } = "";
    }

    private sealed record Column(string Header, bool RightAligned, string[] Cells);
}
