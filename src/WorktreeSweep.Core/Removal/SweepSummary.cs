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
    /// <c>; </c>, then a total: <c>&lt;n&gt; removed, &lt;n&gt; skipped, &lt;n&gt; failed; &lt;bytes&gt; freed</c>, with <c>at least</c>
    /// before the bytes when any removed folder's size is unknown or only partly read. The unknown ones and the Recycle Bin share
    /// follow in one parenthetical, each only when it applies:
    /// <c> (&lt;k&gt; of unknown size; &lt;bytes&gt; of it in the Recycle Bin)</c>, the share with <c>at least</c> of its own when a
    /// recycled folder's size is unknown or partial.
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
        private int approximate;
        private int recycledApproximate;

        /// <summary>Counts one outcome and describes it.</summary>
        /// <param name="outcome">What happened to the pick.</param>
        /// <returns>The words after the pick's path.</returns>
        public string Count(Outcome outcome)
        {
            switch (outcome)
            {
                case Outcome.Recycled recycledOutcome:
                    removed++;
                    if (recycledOutcome.Size is { } recycledSize)
                    {
                        freed += recycledSize.Bytes;
                        recycled += recycledSize.Bytes;
                        if (recycledSize.Partial)
                        {
                            approximate++;
                            recycledApproximate++;
                        }
                    }
                    else
                    {
                        unknown++;
                        approximate++;
                        recycledApproximate++;
                    }
                    return "removed (recycled)";
                case Outcome.Permanent permanent:
                    removed++;
                    if (permanent.Size is { } permanentSize)
                    {
                        freed += permanentSize.Bytes;
                        if (permanentSize.Partial)
                        {
                            approximate++;
                        }
                    }
                    else
                    {
                        unknown++;
                        approximate++;
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
        /// <returns>The counts, the bytes freed and what their sum leaves out.</returns>
        public string Total()
        {
            string total = string.Create(
                CultureInfo.InvariantCulture,
                $"{removed} removed, {skipped} skipped, {failed} failed; {ReportTable.SizeText(freed, approximate > 0)} freed"
            );
            var notes = new List<string>(2);
            if (unknown > 0)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"{unknown} of unknown size"));
            }
            if (recycled > 0 || recycledApproximate > 0)
            {
                notes.Add(
                    string.Create(CultureInfo.InvariantCulture, $"{ReportTable.SizeText(recycled, recycledApproximate > 0)} of it in the Recycle Bin")
                );
            }
            return notes.Count == 0 ? total : $"{total} ({string.Join("; ", notes)})";
        }
    }
}
