using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;

namespace WorktreeSweep.ViewModels;

/// <summary>One runnable pick on the Removing screen: its path, its size, what its plan will do to it, and how far it is.</summary>
public sealed partial class RemovalRowViewModel : ObservableObject
{
    /// <summary>The status before the pick's removal starts.</summary>
    public const string Pending = "pending";

    /// <summary>The status while the pick is being removed.</summary>
    public const string Removing = "removing…";

    /// <summary>The status once the pick is finished, removed or failed.</summary>
    public const string Done = "done";

    /// <summary>What became of the pick; <see langword="null"/> until its removal finishes.</summary>
    private Outcome? outcome;

    /// <summary>Initializes a new instance of the <see cref="RemovalRowViewModel"/> class, pending.</summary>
    /// <param name="index">The pick's index among the decisions.</param>
    /// <param name="path">The pick's path, relative to the scanned root.</param>
    /// <param name="decision">The pick's decision, whose plan says how it is removed and whose candidate's size the row shows.</param>
    /// <exception cref="ArgumentException">The decision is not one the removal runs.</exception>
    public RemovalRowViewModel(int index, string path, Decision decision)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Plan is not Plan.Run run)
        {
            throw new ArgumentException($"the decision is not runnable: {decision.Plan}", nameof(decision));
        }
        Index = index;
        Path = path;
        Action = run.Action;
        Size = decision.Candidate.KnownSize?.Bytes;
        Status = Pending;
        ActionText = PendingText;
    }

    /// <summary>Gets the pick's index among the decisions, as <see cref="Removal.Progress"/> names it.</summary>
    public int Index { get; }

    /// <summary>Gets the pick's path, relative to the scanned root.</summary>
    public string Path { get; }

    /// <summary>Gets how the pick's plan removes it.</summary>
    public RemoveAction Action { get; }

    /// <summary>Gets the size the scan read; <see langword="null"/> when it read none, which counts as no bytes.</summary>
    public long? Size { get; }

    /// <summary>Gets the pick's size as <see cref="ReportTable.HumanBytes"/> writes it; empty when the size is unknown.</summary>
    public string SizeText => Size is { } bytes ? ReportTable.HumanBytes(bytes) : "";

    /// <summary>Gets whether the pick was removed, as opposed to failing or being skipped.</summary>
    public bool Succeeded => outcome is Outcome.Recycled or Outcome.Permanent or Outcome.LinkRemoved or Outcome.Pruned;

    /// <summary>Gets the status: <see cref="Pending"/>, <see cref="Removing"/> or <see cref="Done"/>.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; }

    /// <summary>Gets the line the Status column shows: what the pick waits for, what it is doing, or what it came to.</summary>
    [ObservableProperty]
    public partial string ActionText { get; private set; }

    /// <summary>The line before the pick's removal starts.</summary>
    private string PendingText =>
        Action switch
        {
            RemoveAction.Delete { Method: DeleteMethod.Recycle } => "Waiting · will recycle",
            RemoveAction.Delete { Method: DeleteMethod.Permanent } => "Waiting · will delete permanently",
            RemoveAction.RemoveLink => "Waiting · will remove the link",
            RemoveAction.PruneRegistration => "Waiting · will prune the registration",
            _ => throw new UnreachableException($"unknown remove action {Action}"),
        };

    /// <summary>The line while the pick is being removed.</summary>
    private string RemovingText =>
        Action switch
        {
            RemoveAction.Delete { Method: DeleteMethod.Recycle } => "Recycling…",
            RemoveAction.Delete { Method: DeleteMethod.Permanent } => "Deleting permanently…",
            RemoveAction.RemoveLink => "Removing the link…",
            RemoveAction.PruneRegistration => "Pruning the registration…",
            _ => throw new UnreachableException($"unknown remove action {Action}"),
        };

    /// <summary>Shows that the pick's removal started, which is also what a retry of a failed pick reports.</summary>
    internal void Started()
    {
        outcome = null;
        Status = Removing;
        ActionText = RemovingText;
    }

    /// <summary>Shows that the pick's removal finished.</summary>
    /// <param name="result">What became of it.</param>
    internal void Removed(Outcome result)
    {
        ArgumentNullException.ThrowIfNull(result);
        outcome = result;
        Status = Done;
        ActionText = DoneText(result);
    }

    /// <summary>The line once the pick is finished: what its plan did, or the words the Results screen uses for its outcome.</summary>
    /// <param name="result">What became of the pick.</param>
    /// <returns>The line.</returns>
    private static string DoneText(Outcome result) =>
        result switch
        {
            Outcome.Recycled => "Recycled",
            Outcome.Permanent => "Deleted permanently",
            Outcome.LinkRemoved => "Link removed",
            Outcome.Pruned => "Registration pruned",
            Outcome.Failed failure => $"failed: {failure.Reason}",
            Outcome.Skipped skip => $"skipped ({skip.Reason})",
            _ => throw new UnreachableException($"unknown outcome {result}"),
        };
}
