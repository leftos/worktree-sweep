using System.Globalization;
using WorktreeSweep.Report;

namespace WorktreeSweep.Review;

/// <summary>What an answered <see cref="ReviewSession"/> would do.</summary>
public sealed record ReviewTotals
{
    /// <summary>Gets the folders going to the Recycle Bin.</summary>
    public int Recycle { get; init; }

    /// <summary>Gets their size; a size that is unknown or negative counts 0 and is counted in <see cref="RecycleUnknown"/>.</summary>
    public long RecycleBytes { get; init; }

    /// <summary>Gets how many of the recycled folders have an unknown size; a negative size counts as unknown.</summary>
    public int RecycleUnknown { get; init; }

    /// <summary>Gets how many of the recycled folders the scan could read only partly, so their size is a lower bound.</summary>
    public int RecyclePartial { get; init; }

    /// <summary>Gets the folders deleted for good.</summary>
    public int Permanent { get; init; }

    /// <summary>Gets their size; a size that is unknown or negative counts 0 and is counted in <see cref="PermanentUnknown"/>.</summary>
    public long PermanentBytes { get; init; }

    /// <summary>Gets how many of the permanently deleted folders have an unknown size; a negative size counts as unknown.</summary>
    public int PermanentUnknown { get; init; }

    /// <summary>Gets how many of the permanently deleted folders the scan could read only partly, so their size is a lower bound.</summary>
    public int PermanentPartial { get; init; }

    /// <summary>Gets the links deleted.</summary>
    public int Links { get; init; }

    /// <summary>Gets the registrations pruned.</summary>
    public int Prunes { get; init; }

    /// <summary>Gets the branches deleted.</summary>
    public int Branches { get; init; }

    /// <summary>Gets the picks left in place.</summary>
    public int Skipped { get; init; }

    /// <summary>
    /// The final confirmation's sentence, naming only the non-zero parts: <c>Remove N items: R to the Recycle Bin (X), P permanently
    /// (Y), L links, U registrations pruned; B branches deleted. S skipped.</c> A sized part that includes a folder of unknown or
    /// partly read size reads <c>at least X</c>, and names <c>K of unknown size</c> when any of its sizes is unknown.
    /// </summary>
    /// <returns>The sentence.</returns>
    public string FinalSentence()
    {
        int count = Recycle + Permanent + Links + Prunes;
        var parts = new List<string>();
        if (Recycle > 0)
        {
            parts.Add(SizedPart(Recycle, "to the Recycle Bin", RecycleBytes, RecycleUnknown, RecyclePartial));
        }
        if (Permanent > 0)
        {
            parts.Add(SizedPart(Permanent, "permanently", PermanentBytes, PermanentUnknown, PermanentPartial));
        }
        if (Links > 0)
        {
            parts.Add(LossText.Counted(Links, "link", "links"));
        }
        if (Prunes > 0)
        {
            parts.Add($"{LossText.Counted(Prunes, "registration", "registrations")} pruned");
        }
        string sentence = $"Remove {LossText.Counted(count, "item", "items")}";
        if (parts.Count > 0)
        {
            sentence += ": " + string.Join(", ", parts);
        }
        if (Branches > 0)
        {
            sentence += $"; {LossText.Counted(Branches, "branch", "branches")} deleted";
        }
        sentence += ".";
        return Skipped > 0 ? sentence + string.Create(CultureInfo.InvariantCulture, $" {Skipped} skipped.") : sentence;
    }

    /// <summary>One sized part of the sentence.</summary>
    /// <param name="count">The folders in the part.</param>
    /// <param name="what">What happens to them.</param>
    /// <param name="bytes">Their total size; a size that is unknown or negative counts 0.</param>
    /// <param name="unknown">How many of them have an unknown size.</param>
    /// <param name="partial">How many of them the scan could read only partly.</param>
    /// <returns>
    /// <c>{count} {what} ({bytes})</c>, with <c>at least</c> before the size when any of them is unknown or partial and
    /// <c>; {unknown} of unknown size</c> when any of them is unknown.
    /// </returns>
    private static string SizedPart(int count, string what, long bytes, int unknown, int partial)
    {
        string size = ReportTable.SizeText(bytes, unknown > 0 || partial > 0);
        string named = unknown > 0 ? string.Create(CultureInfo.InvariantCulture, $"{size}; {unknown} of unknown size") : size;
        return string.Create(CultureInfo.InvariantCulture, $"{count} {what} ({named})");
    }
}
