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
    /// <returns>
    /// The table, or a one-line message when there are no candidates, followed by one <c>discovery error: {path}: {message}</c> line
    /// per <see cref="ScanReport.DiscoveryErrors"/> entry, in its order and each on one line.
    /// </returns>
    public static string Render(ScanReport report, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(report);
        IReadOnlyList<Candidate> candidates = report.Ordered();
        string rootKey = Discoverer.PathKey(PathResolver.Resolve(report.Root));
        if (candidates.Count == 0)
        {
            return WithDiscoveryErrors($"No worktrees or orphan folders found under {report.Root}.\n", report, rootKey);
        }
        ReportRow[] rows = [.. RowsFor(candidates, rootKey, nowUnix)];
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
        return WithDiscoveryErrors(table.ToString(), report, rootKey);
    }

    /// <summary>
    /// The report's candidates as rows, in <see cref="ScanReport.Ordered"/> order: the cell texts <see cref="Render"/> prints for
    /// each one, the risk of its MERGE, DIRTY and UPSTREAM cells, and the candidate itself.
    /// </summary>
    /// <param name="report">The report.</param>
    /// <param name="nowUnix">The time ages are measured from, in Unix seconds.</param>
    /// <returns>One row per candidate.</returns>
    public static IReadOnlyList<ReportRow> Rows(ScanReport report, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(report);
        string rootKey = Discoverer.PathKey(PathResolver.Resolve(report.Root));
        return RowsFor(report.Ordered(), rootKey, nowUnix);
    }

    /// <summary>The rows for <paramref name="candidates"/>, in the given order, with the root's <see cref="Discoverer.PathKey"/> in hand.</summary>
    /// <param name="candidates">The candidates, in table order.</param>
    /// <param name="rootKey">The resolved root's <see cref="Discoverer.PathKey"/>.</param>
    /// <param name="nowUnix">The time ages are measured from, in Unix seconds.</param>
    /// <returns>One row per candidate.</returns>
    private static IReadOnlyList<ReportRow> RowsFor(IReadOnlyList<Candidate> candidates, string rootKey, long nowUnix) =>
        [.. candidates.Select(candidate => RowOf(candidate, rootKey, nowUnix))];

    /// <summary>Appends one <c>discovery error: {path}: {message}</c> line per <see cref="ScanReport.DiscoveryErrors"/> entry.</summary>
    /// <param name="text">What <see cref="Render"/> printed.</param>
    /// <param name="report">The report.</param>
    /// <param name="rootKey">The scanned root's resolved <see cref="Discoverer.PathKey"/>.</param>
    /// <returns>The text with the discovery error lines after it.</returns>
    private static string WithDiscoveryErrors(string text, ScanReport report, string rootKey)
    {
        var lines = new StringBuilder(text);
        foreach (DiscoveryError error in report.DiscoveryErrors)
        {
            lines
                .Append("discovery error: ")
                .Append(RelativePathToRootKey(error.Path, rootKey))
                .Append(": ")
                .Append(OneLine(error.Message))
                .Append('\n');
        }
        return lines.ToString();
    }

    /// <summary><paramref name="message"/> with its line breaks, <c>\r\n</c> then <c>\n</c>, replaced by <c>; </c>.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The message on one line.</returns>
    private static string OneLine(string message) =>
        message.Replace("\r\n", "; ", StringComparison.Ordinal).Replace("\n", "; ", StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="path"/> relative to <paramref name="root"/> when it lies under it, the two compared by
    /// <see cref="Discoverer.PathKey"/> over <see cref="PathResolver.Resolve"/>d spellings; else <paramref name="path"/> whole, in
    /// the same resolved-parent spelling. The path's own leaf is never resolved through, so a folder that is a junction shows its
    /// own name rather than its target's, and the relative part drops a trailing separator.
    /// </summary>
    /// <param name="path">The path to show.</param>
    /// <param name="root">The scanned root.</param>
    /// <returns>The path to show.</returns>
    public static string RelativePath(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return RelativePathToRootKey(path, Discoverer.PathKey(PathResolver.Resolve(root)));
    }

    /// <summary>
    /// <see cref="RelativePath(string, string)"/> with the resolved root's <see cref="Discoverer.PathKey"/> already in hand, so a
    /// table of many paths resolves the root once.
    /// </summary>
    /// <param name="path">The path to show.</param>
    /// <param name="rootKey">The resolved root's <see cref="Discoverer.PathKey"/>.</param>
    /// <returns>The path to show.</returns>
    internal static string RelativePathToRootKey(string path, string rootKey)
    {
        ArgumentNullException.ThrowIfNull(path);
        string prefix = rootKey.EndsWith('\\') ? rootKey : rootKey + '\\';
        string resolved = PathResolver.ResolveParent(path);
        string pathKey = Discoverer.PathKey(resolved);
        bool under = pathKey.Length > prefix.Length && pathKey.StartsWith(prefix, StringComparison.Ordinal);
        return under ? resolved[prefix.Length..pathKey.Length] : resolved;
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

    /// <summary>A byte count as the removal lines read it: <c>{text}</c>, or <c>at least {text}</c> when the real size may be more.</summary>
    /// <param name="bytes">The byte count; not negative.</param>
    /// <param name="atLeast">Whether the real size may be more than <paramref name="bytes"/>.</param>
    /// <returns>The text.</returns>
    public static string SizeText(long bytes, bool atLeast) =>
        atLeast ? string.Create(CultureInfo.InvariantCulture, $"at least {HumanBytes(bytes)}") : HumanBytes(bytes);

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

    private static ReportRow RowOf(Candidate candidate, string rootKey, long nowUnix) =>
        candidate switch
        {
            RegisteredCandidate registered => RegisteredRow(registered, rootKey, nowUnix),
            OrphanCandidate orphan => OrphanRow(orphan, rootKey, nowUnix),
            _ => throw new ArgumentException($"unknown candidate type {candidate.GetType().Name}", nameof(candidate)),
        };

    private static ReportRow RegisteredRow(RegisteredCandidate registered, string rootKey, long nowUnix)
    {
        WorktreeSignals signals = registered.Signals;
        string dirty = signals.Dirty is null ? "" : DirtyCell(signals.Dirty);
        string upstream = UpstreamCell(signals.Upstream);
        return new ReportRow
        {
            Path = RelativePathToRootKey(registered.Path, rootKey),
            Kind = "worktree",
            Branch = TruncateEnd(registered.Record.Branch ?? "", MaxBranchWidth),
            Merge = signals.MergeState is null ? "" : MergeWord(signals.MergeState),
            Dirty = dirty,
            Upstream = upstream,
            Active = signals.LastActivityUnix is long then ? Age(then, nowUnix) : "",
            Size = signals.Size is null ? "" : HumanBytes(signals.Size.Bytes),
            Flags = RegisteredFlags(registered),
            MergeRisk = MergeRiskOf(signals.MergeState),
            DirtyRisk = dirty.Length > 0 ? CellRisk.Caution : CellRisk.None,
            UpstreamRisk = upstream.StartsWith('+') ? CellRisk.Caution : CellRisk.None,
            Candidate = registered,
        };
    }

    /// <summary>The risk of a MERGE cell, from the merge state.</summary>
    /// <param name="state">The merge state; <see langword="null"/> when unknown.</param>
    /// <returns>The risk.</returns>
    private static CellRisk MergeRiskOf(MergeState? state) =>
        state switch
        {
            null => CellRisk.None,
            { Kind: MergeStateKind.Unmerged } or { Kind: MergeStateKind.Detached, Contained: false } => CellRisk.Danger,
            { Kind: MergeStateKind.NoCommits } => CellRisk.Caution,
            { Kind: MergeStateKind.Ancestor } or { Kind: MergeStateKind.PatchesApplied } or { Kind: MergeStateKind.ContentContained } =>
                CellRisk.Good,
            { Kind: MergeStateKind.Detached } => CellRisk.None,
            _ => throw new ArgumentException($"unknown merge state kind {state.Kind}", nameof(state)),
        };

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

    private static ReportRow OrphanRow(OrphanCandidate candidate, string rootKey, long nowUnix)
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
        return new ReportRow
        {
            Path = RelativePathToRootKey(orphan.Path, rootKey),
            Kind = isLink ? "link" : "orphan",
            Active = candidate.Size.LastWriteUnix is long then ? Age(then, nowUnix) : "",
            Size = isLink ? "" : HumanBytes(candidate.Size.Bytes),
            Flags = string.Join(", ", flags),
            Candidate = candidate,
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

    private sealed record Column(string Header, bool RightAligned, string[] Cells);
}
