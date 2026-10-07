using CommunityToolkit.Mvvm.ComponentModel;
using WorktreeSweep.Report;

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
    }

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

    /// <summary>Gets the candidate the row shows.</summary>
    public Candidate Candidate => row.Candidate;

    /// <summary>Gets or sets a value indicating whether the row is ticked for removal.</summary>
    [ObservableProperty]
    public partial bool IsTicked { get; set; }
}
