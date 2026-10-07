using WorktreeSweep.Removal;

namespace WorktreeSweep.ViewModels;

/// <summary>The Results screen: one line per pick, the total, and the unlock banner when there was an unlock step.</summary>
public sealed class ResultsViewModel
{
    /// <summary>Initializes a new instance of the <see cref="ResultsViewModel"/> class.</summary>
    /// <param name="swept">What happened to each pick.</param>
    /// <param name="root">The scanned root; paths are shown relative to it.</param>
    /// <param name="banner">The unlock banner; <see langword="null"/> when there was no unlock step.</param>
    public ResultsViewModel(IReadOnlyList<Swept> swept, string root, string? banner)
    {
        IReadOnlyList<string> summary = SweepSummary.Lines(swept, root);
        Lines = [.. summary.Take(summary.Count - 1)];
        Total = summary[^1];
        Banner = banner;
    }

    /// <summary>Gets one line per pick, in decision order.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>Gets the total line.</summary>
    public string Total { get; }

    /// <summary>Gets the unlock banner, <c>Unlock: {outcome}</c>; <see langword="null"/> when there was no unlock step.</summary>
    public string? Banner { get; }
}
