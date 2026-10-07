using WorktreeSweep.Discovery;
using WorktreeSweep.Recycle;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;
using WorktreeSweep.ViewModels;

namespace WorktreeSweep.Tests.ViewModels;

/// <summary>Candidates and view models in a known state for the view-model tests; no git runs and nothing is removed.</summary>
internal static class ViewModelSamples
{
    /// <summary>A mebibyte (1024 × 1024 bytes).</summary>
    public const long Mb = 1024 * 1024;

    /// <summary>A bin holding 1 GB.</summary>
    public static readonly BinCapacity Roomy = new(1024, NukeOnDelete: false);

    /// <summary>A bin holding 1 MB.</summary>
    public static readonly BinCapacity Small = new(1, NukeOnDelete: false);

    /// <summary>A clean, pushed worktree <c>D:\repo.wt\{name}</c> of <c>D:\repo</c> on the branch <paramref name="name"/>.</summary>
    /// <param name="name">The folder and branch name.</param>
    /// <param name="mergeState">Its merge state against <c>main</c>.</param>
    /// <param name="bytes">Its size.</param>
    /// <returns>The candidate.</returns>
    public static RegisteredCandidate Worktree(string name, MergeState mergeState, long bytes) =>
        ReportSamples.Registered(
            $@"repo.wt\{name}",
            name,
            new WorktreeSignals
            {
                MergeState = mergeState,
                MergeStateAgainst = "main",
                Dirty = new Dirty(),
                Upstream = Upstream.Tracking(0),
                Size = new SizeInfo { Bytes = bytes },
            }
        ) with
        {
            Repo = ReportSamples.Under("repo"),
        };

    /// <summary>The link orphan <c>D:\repo.wt\{name}</c>, pointing to <c>D:\target</c>.</summary>
    /// <param name="name">The link's name.</param>
    /// <returns>The candidate.</returns>
    public static OrphanCandidate Link(string name)
    {
        OrphanCandidate orphan = ReportSamples.Orphan($@"repo.wt\{name}", OrphanKind.Link, new SizeInfo());
        return orphan with { Orphan = orphan.Orphan with { LinkTarget = ReportSamples.Under("target") } };
    }

    /// <summary>A window whose scan found <paramref name="candidates"/>, on the List screen.</summary>
    /// <param name="candidates">The candidates.</param>
    /// <returns>The window's view model.</returns>
    public static MainViewModel Listed(params Candidate[] candidates)
    {
        var main = new MainViewModel(_ => TimeSpan.Zero);
        main.ScanCompleted(ReportSamples.ReportOf(candidates), ReportSamples.Now);
        return main;
    }

    /// <summary>The List screen's view model.</summary>
    /// <param name="main">The window.</param>
    /// <returns>The list.</returns>
    public static ListViewModel ListOf(MainViewModel main) => main.List ?? throw new InvalidOperationException("the scan has not completed");

    /// <summary>Ticks every row, enters Review and reads <paramref name="capacity"/> for every capacity path.</summary>
    /// <param name="main">The window, on the List screen.</param>
    /// <param name="capacity">The bin every volume has.</param>
    /// <returns>The review, past Preparing.</returns>
    public static ReviewViewModel Reviewing(MainViewModel main, BinCapacity? capacity)
    {
        ListViewModel list = ListOf(main);
        list.TickAllCommand.Execute(null);
        return Reenter(main, capacity);
    }

    /// <summary>Enters Review with the rows ticked now and reads <paramref name="capacity"/> for every capacity path.</summary>
    /// <param name="main">The window, on the List screen.</param>
    /// <param name="capacity">The bin every volume has.</param>
    /// <returns>The review, past Preparing.</returns>
    public static ReviewViewModel Reenter(MainViewModel main, BinCapacity? capacity)
    {
        ListOf(main).ReviewCommand.Execute(null);
        ReviewViewModel review = main.Review ?? throw new InvalidOperationException("Review was not entered");
        ReadCapacities(review, capacity);
        return review;
    }

    /// <summary>Answers the capacity request with <paramref name="capacity"/> for every path it names.</summary>
    /// <param name="review">The review, Preparing.</param>
    /// <param name="capacity">The bin every volume has.</param>
    public static void ReadCapacities(ReviewViewModel review, BinCapacity? capacity) =>
        review.CapacitiesRead(review.CapacityPaths.ToDictionary(path => path, _ => capacity));
}
