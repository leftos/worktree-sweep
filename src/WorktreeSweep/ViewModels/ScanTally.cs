using WorktreeSweep.Scan;

namespace WorktreeSweep.ViewModels;

/// <summary>
/// Adds up a scan's progress events into a <see cref="ScanSnapshot"/>: a candidate joins the working list on its first step and leaves
/// it on its read. Not thread-safe: the caller applies events under one lock.
/// </summary>
public sealed class ScanTally
{
    /// <summary>The candidates being read, in the order their reads started.</summary>
    private readonly List<ScanProgress.CandidateStep> working = [];

    /// <summary>Everything but the working list.</summary>
    private ScanSnapshot counts = new();

    /// <summary>Adds one progress event.</summary>
    /// <param name="progress">The event.</param>
    public void Apply(ScanProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        switch (progress)
        {
            case ScanProgress.Discovering discovering:
                counts = counts with { Child = discovering.Child, Children = discovering.Children };
                break;
            case ScanProgress.Discovered discovered:
                counts = counts with { Discovered = discovered, CandidatesTotal = discovered.Worktrees + discovered.Orphans + discovered.Links };
                break;
            case ScanProgress.RepoLookedUp looked:
                counts = counts with { ReposDone = looked.Done };
                break;
            case ScanProgress.CandidateStep step:
                Step(step);
                break;
            case ScanProgress.CandidateRead read:
                counts = counts with { CandidatesDone = read.Done, CandidatesTotal = read.Total };
                working.RemoveAll(item => item.Path == read.Path);
                break;
        }
    }

    /// <summary>A copy of the tally as it stands.</summary>
    /// <returns>The snapshot.</returns>
    public ScanSnapshot Snapshot() => counts with { Working = [.. working] };

    /// <summary>Records <paramref name="step"/> as its candidate's current step.</summary>
    /// <param name="step">The step.</param>
    private void Step(ScanProgress.CandidateStep step)
    {
        int index = working.FindIndex(item => item.Path == step.Path);
        if (index < 0)
        {
            working.Add(step);
        }
        else
        {
            working[index] = step;
        }
    }
}
