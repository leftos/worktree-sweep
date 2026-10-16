namespace WorktreeSweep.Report;

/// <summary>
/// One row of the <c>--list</c> table: a candidate's nine cell texts, the risk of its MERGE, DIRTY and UPSTREAM cells, and the
/// candidate itself. <see cref="Path"/> is the PATH cell, the candidate's path relative to the scanned root.
/// </summary>
public sealed record ReportRow
{
    /// <summary>Gets the PATH cell.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the KIND cell: <c>worktree</c>, <c>link</c> or <c>orphan</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the BRANCH cell, or <c>""</c>.</summary>
    public string Branch { get; init; } = "";

    /// <summary>Gets the MERGE cell, or <c>""</c>.</summary>
    public string Merge { get; init; } = "";

    /// <summary>Gets the DIRTY cell, or <c>""</c>.</summary>
    public string Dirty { get; init; } = "";

    /// <summary>Gets the UPSTREAM cell, or <c>""</c>.</summary>
    public string Upstream { get; init; } = "";

    /// <summary>Gets the ACTIVE cell, or <c>""</c>.</summary>
    public string Active { get; init; } = "";

    /// <summary>Gets the SIZE cell, or <c>""</c>.</summary>
    public string Size { get; init; } = "";

    /// <summary>Gets the FLAGS cell, or <c>""</c>.</summary>
    public string Flags { get; init; } = "";

    /// <summary>Gets the risk of the MERGE cell.</summary>
    public CellRisk MergeRisk { get; init; }

    /// <summary>Gets the risk of the DIRTY cell.</summary>
    public CellRisk DirtyRisk { get; init; }

    /// <summary>Gets the risk of the UPSTREAM cell.</summary>
    public CellRisk UpstreamRisk { get; init; }

    /// <summary>Gets what the row is, the KIND cell as a value.</summary>
    public required RowKind RowKind { get; init; }

    /// <summary>Gets the last activity or write the ACTIVE cell ages, in Unix seconds; <see langword="null"/> when unknown.</summary>
    public long? ActiveUnix { get; init; }

    /// <summary>Gets the bytes the SIZE cell shows; <see langword="null"/> when the cell is empty.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Gets the candidate the row was built from.</summary>
    public required Candidate Candidate { get; init; }
}
