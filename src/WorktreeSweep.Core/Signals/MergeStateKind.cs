namespace WorktreeSweep.Signals;

/// <summary>The kinds of <see cref="MergeState"/>, best first.</summary>
public enum MergeStateKind
{
    /// <summary>The branch tip is reachable from the default branch.</summary>
    Ancestor,

    /// <summary>
    /// The branch tip is reachable from the default branch and the branch's reflog records no commit made on it: new or empty work,
    /// not merged work.
    /// </summary>
    NoCommits,

    /// <summary>Every commit on the branch has a patch-equivalent commit on the default branch.</summary>
    PatchesApplied,

    /// <summary>Merging the branch into the default branch would change nothing.</summary>
    ContentContained,

    /// <summary>The branch has commits the default branch lacks.</summary>
    Unmerged,

    /// <summary>HEAD is detached.</summary>
    Detached,
}
