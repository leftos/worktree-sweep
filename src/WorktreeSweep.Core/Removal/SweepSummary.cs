using System.Diagnostics;
using System.Globalization;
using WorktreeSweep.Report;

namespace WorktreeSweep.Removal;

/// <summary>The text that reports what a removal did, one line per pick and a total.</summary>
public static class SweepSummary
{
    /// <summary>
    /// One line per pick (<c>&lt;path&gt;: removed (recycled)</c>, <c>removed (permanent)</c>, <c>removed (link only)</c>,
    /// <c>removed (registration pruned)</c>, <c>skipped (&lt;why&gt;)</c>, <c>failed: &lt;why&gt;</c>), its follow-up notes each after
    /// <c>; </c>, then a total: <c>&lt;n&gt; removed, &lt;n&gt; skipped, &lt;n&gt; failed; &lt;bytes&gt; freed</c>, which reads
    /// <c>at least &lt;bytes&gt; freed (&lt;n&gt; of unknown size)</c> when any removed folder's size is unknown, and ends with
    /// <c> (&lt;bytes&gt; of it in the Recycle Bin)</c> when anything was recycled, <c>at least</c> inside it when a recycled folder's
    /// size is unknown.
    /// </summary>
    /// <param name="swept">What happened to each pick.</param>
    /// <param name="root">The scanned root; each path is shown relative to it.</param>
    /// <returns>The lines, the total last.</returns>
    public static IReadOnlyList<string> Lines(IReadOnlyList<Swept> swept, string root)
    {
        ArgumentNullException.ThrowIfNull(swept);
        ArgumentNullException.ThrowIfNull(root);
        List<string> lines = new(swept.Count + 1);
        var tally = new Tally();
        foreach (Swept entry in swept)
        {
            string what = tally.Count(entry.Outcome);
            string notes = string.Concat(entry.Notes.Select(note => "; " + note));
            lines.Add($"{ReportTable.RelativePath(entry.Candidate.Path, root)}: {what}{notes}");
        }
        lines.Add(tally.Total());
        return lines;
    }

    /// <summary>The running counts behind the total line.</summary>
    private sealed class Tally
    {
        private int removed;
        private int skipped;
        private int failed;
        private long freed;
        private long recycled;
        private int unknown;
        private int recycledUnknown;

        /// <summary>Counts one outcome and describes it.</summary>
        /// <param name="outcome">What happened to the pick.</param>
        /// <returns>The words after the pick's path.</returns>
        public string Count(Outcome outcome)
        {
            switch (outcome)
            {
                case Outcome.Recycled recycledOutcome:
                    removed++;
                    if (recycledOutcome.Bytes is { } recycledBytes)
                    {
                        freed += recycledBytes;
                        recycled += recycledBytes;
                    }
                    else
                    {
                        unknown++;
                        recycledUnknown++;
                    }
                    return "removed (recycled)";
                case Outcome.Permanent permanent:
                    removed++;
                    if (permanent.Bytes is { } permanentBytes)
                    {
                        freed += permanentBytes;
                    }
                    else
                    {
                        unknown++;
                    }
                    return "removed (permanent)";
                case Outcome.LinkRemoved:
                    removed++;
                    return "removed (link only)";
                case Outcome.Pruned:
                    removed++;
                    return "removed (registration pruned)";
                case Outcome.Skipped skip:
                    skipped++;
                    return $"skipped ({skip.Reason})";
                case Outcome.Failed failure:
                    failed++;
                    return $"failed: {failure.Reason}";
                default:
                    throw new UnreachableException($"unknown outcome {outcome}");
            }
        }

        /// <summary>The total line.</summary>
        /// <returns>The counts, the bytes freed and any unknown size.</returns>
        public string Total()
        {
            string freedText =
                unknown > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"at least {ReportTable.HumanBytes(freed)} freed ({unknown} of unknown size)")
                    : string.Create(CultureInfo.InvariantCulture, $"{ReportTable.HumanBytes(freed)} freed");
            string total = string.Create(CultureInfo.InvariantCulture, $"{removed} removed, {skipped} skipped, {failed} failed; {freedText}");
            return recycled == 0 && recycledUnknown == 0 ? total : $"{total} ({BinShare()})";
        }

        /// <summary>The Recycle Bin share that ends the total line.</summary>
        /// <returns>The share, <c>at least</c> first when any recycled folder's size is unknown.</returns>
        private string BinShare() =>
            recycledUnknown > 0
                ? string.Create(CultureInfo.InvariantCulture, $"at least {ReportTable.HumanBytes(recycled)} of it in the Recycle Bin")
                : string.Create(CultureInfo.InvariantCulture, $"{ReportTable.HumanBytes(recycled)} of it in the Recycle Bin");
    }
}
