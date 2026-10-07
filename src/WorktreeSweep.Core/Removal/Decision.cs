using WorktreeSweep.Report;

namespace WorktreeSweep.Removal;

/// <summary>One pick with every answer about it: how to remove it (or why not) and what to do with its branch.</summary>
/// <param name="Candidate">The pick.</param>
/// <param name="Plan">Its action, or why it is skipped.</param>
/// <param name="Branch">Its branch, applied only when the removal succeeds.</param>
public sealed record Decision(Candidate Candidate, Plan Plan, BranchChoice Branch);
