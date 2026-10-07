using WorktreeSweep.Report;

namespace WorktreeSweep.Removal;

/// <summary>One pick and what happened to it.</summary>
/// <param name="Candidate">The pick.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Notes">What the follow-ups (prune, branch) did.</param>
public sealed record Swept(Candidate Candidate, Outcome Outcome, IReadOnlyList<string> Notes);
