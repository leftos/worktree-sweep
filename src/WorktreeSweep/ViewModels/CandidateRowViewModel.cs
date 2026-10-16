using CommunityToolkit.Mvvm.ComponentModel;
using WorktreeSweep.Report;
using WorktreeSweep.Review;

namespace WorktreeSweep.ViewModels;

/// <summary>One row of the List screen: a candidate's cell texts and risks, and whether it is ticked for removal.</summary>
public sealed partial class CandidateRowViewModel : ObservableObject
{
    /// <summary>The table row the cells come from.</summary>
    private readonly ReportRow row;

    /// <summary>Initializes a new instance of the <see cref="CandidateRowViewModel"/> class, ticked when it is a released worktree.</summary>
    /// <param name="row">The table row.</param>
    public CandidateRowViewModel(ReportRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        this.row = row;
        IsTicked = row.Candidate is RegisteredCandidate { Released: not null };
        LosesNothing = !HasUnknownSignals(row.Candidate) && LossText.For(row.Candidate) is null;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is a worktree whose signals were not all read: an error was recorded, or its merge state,
    /// uncommitted work or (on a branch) upstream is unknown. A detached worktree has no upstream, so its absence is not unknown, and a
    /// prunable registration has no signals by design, since its folder is gone.
    /// </summary>
    /// <param name="candidate">The candidate.</param>
    /// <returns><see langword="true"/> when what removing it loses cannot be known.</returns>
    private static bool HasUnknownSignals(Candidate candidate) =>
        candidate is RegisteredCandidate { Signals: var signals } registered
        && registered.Record.Prunable is null
        && (
            signals.Errors.Count > 0
            || signals.MergeState is null
            || signals.Dirty is null
            || (signals.Upstream is null && registered.Record.Branch is not null)
        );

    /// <summary>Gets the PATH cell, relative to the scanned root.</summary>
    public string Path => row.Path;

    /// <summary>Gets the KIND cell.</summary>
    public string Kind => row.Kind;

    /// <summary>Gets the BRANCH cell.</summary>
    public string Branch => row.Branch;

    /// <summary>Gets the MERGE cell.</summary>
    public string Merge => row.Merge;

    /// <summary>Gets the DIRTY cell.</summary>
    public string Dirty => row.Dirty;

    /// <summary>Gets the UPSTREAM cell.</summary>
    public string Upstream => row.Upstream;

    /// <summary>Gets the ACTIVE cell.</summary>
    public string Active => row.Active;

    /// <summary>Gets the SIZE cell.</summary>
    public string Size => row.Size;

    /// <summary>Gets the FLAGS cell.</summary>
    public string Flags => row.Flags;

    /// <summary>Gets the risk of the MERGE cell.</summary>
    public CellRisk MergeRisk => row.MergeRisk;

    /// <summary>Gets the risk of the DIRTY cell.</summary>
    public CellRisk DirtyRisk => row.DirtyRisk;

    /// <summary>Gets the risk of the UPSTREAM cell.</summary>
    public CellRisk UpstreamRisk => row.UpstreamRisk;

    /// <summary>Gets what the row is.</summary>
    public RowKind RowKind => row.RowKind;

    /// <summary>Gets the last activity or write the ACTIVE cell ages, in Unix seconds; <see langword="null"/> when unknown.</summary>
    public long? ActiveUnix => row.ActiveUnix;

    /// <summary>Gets the bytes the SIZE cell shows; <see langword="null"/> when the cell is empty.</summary>
    public long? SizeBytes => row.SizeBytes;

    /// <summary>Gets the full branch name, which the BRANCH cell may cut short; empty when there is none.</summary>
    public string FullBranch => row.Candidate is RegisteredCandidate { Record.Branch: { } branch } ? branch : "";

    /// <summary>Gets a value indicating whether removing the candidate loses nothing.</summary>
    public bool LosesNothing { get; }

    /// <summary>Gets the candidate the row shows.</summary>
    public Candidate Candidate => row.Candidate;

    /// <summary>Gets or sets a value indicating whether the row is ticked for removal.</summary>
    [ObservableProperty]
    public partial bool IsTicked { get; set; }
}
