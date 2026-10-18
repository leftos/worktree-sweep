using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using WorktreeSweep.Scan;
using WorktreeSweep.Signals;

namespace WorktreeSweep.ViewModels;

/// <summary>
/// The Scanning screen: the scan's three steps, how far each has got, the overall percent and elapsed time, and the candidates being
/// read now. Changed only on the UI thread, from the snapshots the window's workers post.
/// </summary>
public sealed partial class ScanningViewModel : ObservableObject
{
    /// <summary>The first step's title.</summary>
    public const string FindTitle = "Find repos and worktree folders";

    /// <summary>The second step's title.</summary>
    public const string LookUpTitle = "Look up each repo's default branch";

    /// <summary>The third step's title.</summary>
    public const string ReadTitle = "Read each candidate's state";

    /// <summary>What the third step reads.</summary>
    public const string ReadDetail =
        "Merge state against the default branch, uncommitted files, unpushed commits, last activity and size. "
        + "Size walks every file, so the biggest folders take longest.";

    /// <summary>Initializes a new instance of the <see cref="ScanningViewModel"/> class, before the scan reports anything.</summary>
    /// <param name="root">The full path of the folder scanned.</param>
    public ScanningViewModel(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = root;
        Show(new ScanSnapshot());
    }

    /// <summary>Gets the full path of the folder scanned.</summary>
    public string Root { get; }

    /// <summary>Gets the step running: 1, 2 or 3.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepText))]
    public partial int Step { get; private set; }

    /// <summary>Gets or sets how long the scan has run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepText))]
    public partial TimeSpan Elapsed { get; set; }

    /// <summary>Gets the step and the elapsed whole seconds: <c>step 3 of 3 · 12 s</c>.</summary>
    public string StepText => string.Create(CultureInfo.InvariantCulture, $"step {Step} of 3 · {(long)Elapsed.TotalSeconds} s");

    /// <summary>Gets the share of the candidates read, from 0 to 100; 0 before the first is read.</summary>
    [ObservableProperty]
    public partial int Percent { get; private set; }

    /// <summary>Gets the line under the progress bar: what the scan is doing, and that it only reads.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; }

    /// <summary>Gets the first step: discovery.</summary>
    [ObservableProperty]
    public partial ScanStepRow Find { get; private set; }

    /// <summary>Gets the second step: the repos' default branches.</summary>
    [ObservableProperty]
    public partial ScanStepRow LookUp { get; private set; }

    /// <summary>Gets the third step: the candidates' reads.</summary>
    [ObservableProperty]
    public partial ScanStepRow Read { get; private set; }

    /// <summary>Gets the candidates read so far.</summary>
    [ObservableProperty]
    public partial int CandidatesDone { get; private set; }

    /// <summary>Gets the candidates to read.</summary>
    [ObservableProperty]
    public partial int CandidatesTotal { get; private set; }

    /// <summary>Gets the third step's count: <c>10 of 14</c>.</summary>
    [ObservableProperty]
    public partial string CandidatesText { get; private set; }

    /// <summary>Gets the candidates being read now.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<WorkingRow> Working { get; private set; }

    /// <summary>Shows how far the scan has got.</summary>
    /// <param name="snapshot">The scan's tally.</param>
    public void Show(ScanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ScanProgress.Discovered? discovered = snapshot.Discovered;
        int repos = discovered?.Repos ?? 0;
        Step =
            discovered is null ? 1
            : snapshot.ReposDone < repos ? 2
            : 3;
        Percent = snapshot.CandidatesTotal > 0 ? (int)(100L * snapshot.CandidatesDone / snapshot.CandidatesTotal) : 0;
        CandidatesDone = snapshot.CandidatesDone;
        CandidatesTotal = snapshot.CandidatesTotal;
        CandidatesText = Invariant($"{snapshot.CandidatesDone} of {snapshot.CandidatesTotal}");
        Find = new ScanStepRow(FindTitle, FindDetail(snapshot), Step == 1 ? StepState.Running : StepState.Done);
        LookUp = new ScanStepRow(LookUpTitle, discovered is null ? "" : Invariant($"{snapshot.ReposDone} of {repos} repos"), StateOf(2));
        Read = new ScanStepRow(ReadTitle, ReadDetail, StateOf(3));
        Status = StatusOf(snapshot, repos);
        WorkingRow[] working = [.. snapshot.Working.Select(step => new WorkingRow(Path.GetRelativePath(Root, step.Path), ActionOf(step)))];
        if (Working is null || !working.SequenceEqual(Working))
        {
            Working = working;
        }
    }

    /// <summary>What a candidate's read is doing.</summary>
    /// <param name="step">Its current step.</param>
    /// <returns>The text.</returns>
    public static string ActionOf(ScanProgress.CandidateStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Step switch
        {
            ScanStep.Merge => "git merge-tree",
            ScanStep.Status => "git status",
            ScanStep.Upstream => "upstream",
            ScanStep.Activity => "last activity",
            ScanStep.Size => Invariant($"measuring size · {step.Files:N0} files so far"),
            _ => step.Step.ToString(),
        };
    }

    /// <summary>The first step's detail: the folder being classified, then what discovery found.</summary>
    private static string FindDetail(ScanSnapshot snapshot)
    {
        if (snapshot.Discovered is not { } found)
        {
            return snapshot.Children > 0 ? Invariant($"folder {snapshot.Child} of {snapshot.Children}") : "";
        }
        int candidates = found.Worktrees + found.Orphans + found.Links;
        return $"{Count(found.Repos, "repo")}, {Count(found.Containers, "worktree folder")}, {Count(candidates, "candidate")}: "
            + $"{Count(found.Worktrees, "worktree")}, {Count(found.Orphans, "orphan")}, {Count(found.Links, "link")}";
    }

    /// <summary>What the scan is doing, and that it only reads.</summary>
    private string StatusOf(ScanSnapshot snapshot, int repos)
    {
        string doing = Step switch
        {
            1 => "Finding repos and worktree folders.",
            2 => Invariant($"Looking up repo {snapshot.ReposDone} of {repos}."),
            _ => Invariant($"Reading candidate {snapshot.CandidatesDone} of {snapshot.CandidatesTotal}."),
        };
        return $"{doing} The scan only reads: nothing under {Root} is written.";
    }

    /// <summary>The state of step <paramref name="step"/>, from the step running.</summary>
    private StepState StateOf(int step) =>
        Step < step ? StepState.Waiting
        : Step == step ? StepState.Running
        : StepState.Done;

    /// <summary><paramref name="count"/> and <paramref name="noun"/>, with an <c>s</c> unless it is one.</summary>
    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : Invariant($"{count} {noun}s");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
