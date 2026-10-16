using System.ComponentModel;
using WorktreeSweep.Report;

namespace WorktreeSweep.ViewModels;

/// <summary>
/// The order the List screen shows its rows in for a <see cref="SortKey"/> and direction. The sort is stable, so rows that tie keep
/// the order they came in, in either direction.
/// </summary>
internal static class RowOrder
{
    /// <summary><paramref name="rows"/> sorted by <paramref name="key"/>; in the order given when <paramref name="key"/> is null.</summary>
    /// <param name="rows">The rows, in table order.</param>
    /// <param name="key">The column to sort by; <see langword="null"/> for table order.</param>
    /// <param name="direction">The direction.</param>
    /// <returns>The sorted rows.</returns>
    public static IEnumerable<CandidateRowViewModel> Sort(IEnumerable<CandidateRowViewModel> rows, SortKey? key, ListSortDirection direction) =>
        key is { } sortKey ? rows.OrderBy(row => row, new RowComparer(sortKey, direction == ListSortDirection.Descending)) : rows;

    /// <summary>
    /// Compares two rows by one column. Text compares ordinally, ignoring case. A risk column puts the riskiest first, then compares
    /// its text. Active puts the most recent first and Size the smallest first; an unknown time or size goes last in either direction.
    /// </summary>
    /// <param name="key">The column.</param>
    /// <param name="descending">Whether the order is reversed.</param>
    private sealed class RowComparer(SortKey key, bool descending) : IComparer<CandidateRowViewModel>
    {
        /// <inheritdoc/>
        public int Compare(CandidateRowViewModel? x, CandidateRowViewModel? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);
            return key switch
            {
                SortKey.Path => Directed(Text(x.Path, y.Path)),
                SortKey.Kind => Directed(Text(x.Kind, y.Kind)),
                SortKey.Branch => Directed(Text(x.FullBranch, y.FullBranch)),
                SortKey.Merge => Directed(Risk(x.MergeRisk, x.Merge, y.MergeRisk, y.Merge)),
                SortKey.Dirty => Directed(Risk(x.DirtyRisk, x.Dirty, y.DirtyRisk, y.Dirty)),
                SortKey.Upstream => Directed(Risk(x.UpstreamRisk, x.Upstream, y.UpstreamRisk, y.Upstream)),
                SortKey.Active => UnknownLast(x.ActiveUnix, y.ActiveUnix, largestFirst: true),
                SortKey.Size => UnknownLast(x.SizeBytes, y.SizeBytes, largestFirst: false),
                SortKey.Flags => Directed(Text(x.Flags, y.Flags)),
                _ => throw new InvalidOperationException($"unknown sort key {key}"),
            };
        }

        /// <summary>Two cell texts, compared ordinally and ignoring case.</summary>
        private static int Text(string x, string y) => StringComparer.OrdinalIgnoreCase.Compare(x, y);

        /// <summary>Two risk cells: the riskier first, then by text.</summary>
        private static int Risk(CellRisk xRisk, string xText, CellRisk yRisk, string yText)
        {
            int byRisk = yRisk.CompareTo(xRisk);
            return byRisk != 0 ? byRisk : Text(xText, yText);
        }

        /// <summary>Two numbers, an unknown one last whatever the direction.</summary>
        /// <param name="x">The first row's number.</param>
        /// <param name="y">The second row's number.</param>
        /// <param name="largestFirst">Whether ascending puts the larger number first.</param>
        private int UnknownLast(long? x, long? y, bool largestFirst) =>
            (x, y) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                ({ } a, { } b) => Directed(largestFirst ? b.CompareTo(a) : a.CompareTo(b)),
            };

        /// <summary>An ascending comparison turned around when the order is descending.</summary>
        private int Directed(int ascending) => descending ? -ascending : ascending;
    }
}
