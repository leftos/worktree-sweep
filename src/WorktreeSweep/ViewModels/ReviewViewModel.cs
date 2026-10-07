using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorktreeSweep.Recycle;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Review;

namespace WorktreeSweep.ViewModels;

/// <summary>
/// The Review screen: waits for the Recycle Bin capacities of the ticked folders, asks the <see cref="ReviewSession"/>'s questions one
/// at a time, then shows the final sentence. Answers are kept per ticked set: entering Review again with the same ticks replays them
/// while each question matches the one answered, and other ticks clear them.
/// </summary>
public sealed partial class ReviewViewModel : ObservableObject
{
    /// <summary>The scan the picks come from.</summary>
    private readonly ScanReport report;

    /// <summary>Returns to the List.</summary>
    private readonly Action back;

    /// <summary>Starts removing the decisions.</summary>
    private readonly Action<IReadOnlyList<Decision>> startRemoval;

    /// <summary>The answers given for <see cref="answeredFor"/>, in the order asked.</summary>
    private readonly List<StoredAnswer> answers = [];

    /// <summary>The ticked rows' indexes the answers were given for.</summary>
    private int[] answeredFor = [];

    /// <summary>The ticked candidates, in table order.</summary>
    private IReadOnlyList<Candidate> picks = [];

    /// <summary>The questions, once the capacities are read.</summary>
    private ReviewSession? session;

    /// <summary>The decisions awaiting Confirm.</summary>
    private IReadOnlyList<Decision>? decisions;

    /// <summary>Whether the screen is showing, so a capacity reply is still wanted.</summary>
    private bool open;

    /// <summary>Initializes a new instance of the <see cref="ReviewViewModel"/> class.</summary>
    /// <param name="report">The scan the picks come from.</param>
    /// <param name="back">Returns to the List.</param>
    /// <param name="startRemoval">Starts removing the decisions.</param>
    public ReviewViewModel(ScanReport report, Action back, Action<IReadOnlyList<Decision>> startRemoval)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(back);
        ArgumentNullException.ThrowIfNull(startRemoval);
        this.report = report;
        this.back = back;
        this.startRemoval = startRemoval;
        CapacityPaths = [];
        Title = "";
        PickPath = "";
        Body = "";
        Question = "";
        YesLabel = "";
        NoLabel = "";
        FinalSentence = "";
    }

    /// <summary>Gets where the screen is.</summary>
    [ObservableProperty]
    public partial ReviewState State { get; private set; }

    /// <summary>Gets the ticked candidates' paths whose Recycle Bin capacity <see cref="CapacitiesRead"/> must bring.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> CapacityPaths { get; private set; }

    /// <summary>Gets the question dialog's title.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; }

    /// <summary>Gets the asked-about pick's path, relative to the scanned root.</summary>
    [ObservableProperty]
    public partial string PickPath { get; private set; }

    /// <summary>Gets what the question is about.</summary>
    [ObservableProperty]
    public partial string Body { get; private set; }

    /// <summary>Gets the question.</summary>
    [ObservableProperty]
    public partial string Question { get; private set; }

    /// <summary>Gets the yes answer's label.</summary>
    [ObservableProperty]
    public partial string YesLabel { get; private set; }

    /// <summary>Gets the no answer's label.</summary>
    [ObservableProperty]
    public partial string NoLabel { get; private set; }

    /// <summary>Gets the answer chosen when the user just accepts: the question's default, or Cancel (no) on the final confirmation.</summary>
    [ObservableProperty]
    public partial bool DefaultAnswer { get; private set; }

    /// <summary>Gets the final confirmation's sentence.</summary>
    [ObservableProperty]
    public partial string FinalSentence { get; private set; }

    /// <summary>
    /// Builds the questions from the capacities read for <see cref="CapacityPaths"/>, replays the kept answers and shows the next
    /// question. A reply that comes after the screen was left, or that does not answer exactly <see cref="CapacityPaths"/>, is stale
    /// and ignored.
    /// </summary>
    /// <param name="capacities">The Recycle Bin settings per path; <see langword="null"/> when unknown.</param>
    public void CapacitiesRead(IReadOnlyDictionary<string, BinCapacity?> capacities)
    {
        ArgumentNullException.ThrowIfNull(capacities);
        if (!open || State != ReviewState.Preparing || capacities.Count != CapacityPaths.Count || !CapacityPaths.All(capacities.ContainsKey))
        {
            return;
        }
        var review = new ReviewSession(picks, report.Root, path => capacities[path], report.DiscoveryErrors);
        int replayed = 0;
        foreach (StoredAnswer answer in answers)
        {
            if (review.Current is not { } step || step.Pick != answer.Pick || step.Kind != answer.Kind)
            {
                break;
            }
            review.Answer(answer.Yes);
            replayed++;
        }
        answers.RemoveRange(replayed, answers.Count - replayed);
        session = review;
        Advance(review);
    }

    /// <summary>Answers the current question and moves to the next one, or past the last one. Does nothing unless asking.</summary>
    /// <param name="yes">The answer.</param>
    [RelayCommand]
    public void Answer(bool yes)
    {
        if (State != ReviewState.Asking || session?.Current is not { } step)
        {
            return;
        }
        answers.Add(new StoredAnswer(step.Pick, step.Kind, yes));
        session.Answer(yes);
        Advance(session);
    }

    /// <summary>Starts waiting for the capacities of <paramref name="tickedPicks"/>, clearing the kept answers when the ticks changed.</summary>
    /// <param name="tickedRows">The ticked rows' indexes, in table order.</param>
    /// <param name="tickedPicks">The ticked candidates, in the same order.</param>
    internal void Enter(IReadOnlyList<int> tickedRows, IReadOnlyList<Candidate> tickedPicks)
    {
        if (!tickedRows.SequenceEqual(answeredFor))
        {
            answers.Clear();
            answeredFor = [.. tickedRows];
        }
        picks = tickedPicks;
        session = null;
        decisions = null;
        open = true;
        CapacityPaths = [.. tickedPicks.Where(RemovalPlanner.NeedsCapacity).Select(pick => pick.Path)];
        State = ReviewState.Preparing;
    }

    /// <summary>The dialog title and the yes and no labels of a kind of question.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The title and labels.</returns>
    private static (string Title, string Yes, string No) Labels(StepKind kind) =>
        kind switch
        {
            StepKind.Loss => ("Would lose work", "Remove anyway", "Keep"),
            StepKind.Link => ("Link", "Remove the link", "Keep"),
            StepKind.Permanent => ("Permanent delete", "Delete permanently", "Skip it"),
            StepKind.Branch => ("Delete branch", "Delete branch after removing", "Keep branch"),
            _ => throw new UnreachableException($"unknown step kind {kind}"),
        };

    /// <summary>Returns to the List, keeping the ticks and the answers.</summary>
    [RelayCommand]
    private void Back()
    {
        open = false;
        back();
    }

    /// <summary>Starts the removal the final sentence describes. Does nothing unless confirming.</summary>
    [RelayCommand]
    private void Confirm()
    {
        if (State != ReviewState.Confirming || decisions is null)
        {
            return;
        }
        open = false;
        startRemoval(decisions);
    }

    /// <summary>Declines the final confirmation: returns to the List, keeping the ticks and the answers.</summary>
    [RelayCommand]
    private void Cancel() => Back();

    /// <summary>
    /// Shows <paramref name="review"/>'s next question, or once it is answered the final confirmation; with nothing to remove, starts
    /// the removal at once.
    /// </summary>
    /// <param name="review">The questions.</param>
    private void Advance(ReviewSession review)
    {
        if (review.Current is { } step)
        {
            (string Title, string Yes, string No) labels = Labels(step.Kind);
            Title = labels.Title;
            YesLabel = labels.Yes;
            NoLabel = labels.No;
            PickPath = ReportTable.RelativePath(picks[step.Pick].Path, report.Root);
            Body = step.Body;
            Question = step.Question;
            DefaultAnswer = step.DefaultAnswer;
            State = ReviewState.Asking;
            return;
        }
        ReviewTotals totals = review.Totals() ?? throw new UnreachableException("an answered review has totals");
        IReadOnlyList<Decision> answered = review.Decisions() ?? throw new UnreachableException("an answered review has decisions");
        if (totals.Recycle + totals.Permanent + totals.Links + totals.Prunes == 0)
        {
            open = false;
            startRemoval(answered);
            return;
        }
        decisions = answered;
        FinalSentence = totals.FinalSentence();
        DefaultAnswer = false;
        State = ReviewState.Confirming;
    }

    /// <summary>One answer given, kept for replay.</summary>
    /// <param name="Pick">The pick's index among the ticked candidates.</param>
    /// <param name="Kind">The kind of question.</param>
    /// <param name="Yes">The answer.</param>
    private sealed record StoredAnswer(int Pick, StepKind Kind, bool Yes);
}
