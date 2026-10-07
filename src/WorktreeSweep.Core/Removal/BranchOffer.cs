namespace WorktreeSweep.Removal;

/// <summary>An offer to delete a removed worktree's branch.</summary>
/// <param name="Branch">The branch.</param>
/// <param name="Force">Whether it needs <c>git branch -D</c> (git's <c>-d</c> refuses cherry-picked and squash-merged branches).</param>
/// <param name="Context">The branch and why it can go (<c>Branch &lt;name&gt;: &lt;reason&gt;</c>), shown before the question.</param>
/// <param name="Question">The fixed question, which names no branch.</param>
public sealed record BranchOffer(string Branch, bool Force, string Context, string Question);
