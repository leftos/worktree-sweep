using WorktreeSweep.Signals;

namespace WorktreeSweep.Scan;

/// <summary>How far a scan has got, as <see cref="Scanner.Scan"/> reports it.</summary>
public abstract record ScanProgress
{
    private protected ScanProgress() { }

    /// <summary>Discovery is classifying a child folder of the root.</summary>
    /// <param name="Child">The child's position, from 1.</param>
    /// <param name="Children">How many children the root has.</param>
    public sealed record Discovering(int Child, int Children) : ScanProgress;

    /// <summary>Discovery is done.</summary>
    /// <param name="Repos">The repos found.</param>
    /// <param name="Containers">The container folders found.</param>
    /// <param name="Worktrees">The registered linked worktrees, each repo's main worktree left out.</param>
    /// <param name="Orphans">The orphan folders, links left out.</param>
    /// <param name="Links">The orphans that are junctions or symbolic links.</param>
    public sealed record Discovered(int Repos, int Containers, int Worktrees, int Orphans, int Links) : ScanProgress;

    /// <summary>One more repo's default branches and common git dir are looked up.</summary>
    /// <param name="Done">The repos looked up so far.</param>
    /// <param name="Total">The repos to look up.</param>
    public sealed record RepoLookedUp(int Done, int Total) : ScanProgress;

    /// <summary>
    /// A candidate's read is starting a step, or, for <see cref="ScanStep.Size"/>, has counted more files.
    /// </summary>
    /// <param name="Path">The candidate's path.</param>
    /// <param name="Step">The step.</param>
    /// <param name="Files">The files the size walk has counted so far; 0 for the other steps.</param>
    public sealed record CandidateStep(string Path, ScanStep Step, long Files) : ScanProgress;

    /// <summary>One more candidate is read.</summary>
    /// <param name="Path">The path of the candidate just read, as its <see cref="CandidateStep"/>s name it.</param>
    /// <param name="Done">The candidates read so far.</param>
    /// <param name="Total">The candidates to read.</param>
    public sealed record CandidateRead(string Path, int Done, int Total) : ScanProgress;
}
