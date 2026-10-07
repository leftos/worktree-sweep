using System.Globalization;
using WorktreeSweep.Report;

namespace WorktreeSweep.Review;

/// <summary>What an answered <see cref="ReviewSession"/> would do.</summary>
public sealed record ReviewTotals
{
    /// <summary>Gets the folders going to the Recycle Bin.</summary>
    public int Recycle { get; init; }

    /// <summary>Gets their size; an unknown size counts 0.</summary>
    public long RecycleBytes { get; init; }

    /// <summary>Gets the folders deleted for good.</summary>
    public int Permanent { get; init; }

    /// <summary>Gets their size; an unknown size counts 0.</summary>
    public long PermanentBytes { get; init; }

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
    /// (Y), L links, U registrations pruned; B branches deleted. S skipped.</c>
    /// </summary>
    /// <returns>The sentence.</returns>
    public string FinalSentence()
    {
        int count = Recycle + Permanent + Links + Prunes;
        var parts = new List<string>();
        if (Recycle > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Recycle} to the Recycle Bin ({ReportTable.HumanBytes(RecycleBytes)})"));
        }
        if (Permanent > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Permanent} permanently ({ReportTable.HumanBytes(PermanentBytes)})"));
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
}
