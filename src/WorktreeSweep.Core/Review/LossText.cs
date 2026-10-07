using System.Diagnostics;
using System.Globalization;
using WorktreeSweep.Discovery;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Review;

/// <summary>
/// The text saying what removing a pick would lose, and the question confirming it. A confirmation is asked for uncommitted files,
/// commits not on the default branch, a branch with no commits of its own, a detached HEAD not on it, unpushed commits, a git lock,
/// an orphan another repo still registers, and a link (whose target is kept).
/// </summary>
public static class LossText
{
    /// <summary>The question for every loss but a link's.</summary>
    private const string RemoveAnyway = "Remove anyway?";

    /// <summary>
    /// The confirmation for a pick as a context naming the pick's path (relative to <paramref name="root"/>) and what removing it
    /// loses, and a short question naming no path.
    /// </summary>
    /// <param name="candidate">The pick.</param>
    /// <param name="root">The scanned root.</param>
    /// <returns>The context and question; <see langword="null"/> when nothing is lost.</returns>
    public static (string Context, string Question)? Sentence(Candidate candidate, string root)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(root);
        if (Raw(candidate) is not { } raw)
        {
            return null;
        }
        return ($"{ReportTable.RelativePath(candidate.Path, root)}: {raw.Text}", raw.Question);
    }

    /// <summary>The loss <see cref="Sentence"/> describes, without the pick's path and with a capital first letter.</summary>
    /// <param name="candidate">The pick.</param>
    /// <returns>The loss; <see langword="null"/> when nothing is lost.</returns>
    public static Loss? For(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Raw(candidate) is { } raw ? raw with { Text = Capitalise(raw.Text) } : null;
    }

    /// <summary>
    /// The repo a worktree's git dir (<c>{repo}\.git\worktrees\{id}</c>) belongs to; the common dir itself for a bare repo
    /// (<c>{common}\worktrees\{id}</c>), and the git dir itself when it has neither shape. The git dir is first made absolute
    /// with its <c>.</c> and <c>..</c> segments removed (<see cref="Path.GetFullPath(string)"/>; nothing on disk is read), so a git
    /// dir written relative to its worktree yields a plain path. Folder names compare case-insensitively.
    /// </summary>
    /// <param name="gitdir">The worktree's git dir.</param>
    /// <returns>The repo folder.</returns>
    public static string RepoOfGitdir(string gitdir)
    {
        ArgumentNullException.ThrowIfNull(gitdir);
        string full = Path.GetFullPath(gitdir);
        string? worktrees = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full));
        if (worktrees is null || !NameIs(worktrees, "worktrees"))
        {
            return full;
        }
        string? common = Path.GetDirectoryName(worktrees);
        if (common is null)
        {
            return full;
        }
        return NameIs(common, ".git") ? Path.GetDirectoryName(common) ?? common : common;
    }

    /// <summary>The loss with its text uncapitalised, as it follows the path in <see cref="Sentence"/>.</summary>
    /// <param name="candidate">The pick.</param>
    /// <returns>The loss; <see langword="null"/> when nothing is lost.</returns>
    private static Loss? Raw(Candidate candidate) =>
        candidate switch
        {
            RegisteredCandidate registered => RegisteredLoss(registered) is { } text ? new Loss(IsLink: false, text, RemoveAnyway) : null,
            OrphanCandidate orphan => OrphanNotice(orphan.Orphan),
            _ => throw new UnreachableException($"unknown candidate {candidate}"),
        };

    /// <summary>What removing a registered worktree loses: its work as one sentence, then its git lock.</summary>
    /// <param name="registered">The worktree.</param>
    /// <returns>The text; <see langword="null"/> when nothing is lost.</returns>
    private static string? RegisteredLoss(RegisteredCandidate registered)
    {
        string[] sentences = [.. new[] { LostWork(registered), LockSentence(registered.Record.Locked) }.OfType<string>()];
        return sentences.Length > 0 ? string.Join(' ', sentences) : null;
    }

    /// <summary>The uncommitted files and the commits removing a registered worktree loses, as one sentence.</summary>
    /// <param name="registered">The worktree.</param>
    /// <returns>The sentence; <see langword="null"/> when no work is lost.</returns>
    private static string? LostWork(RegisteredCandidate registered)
    {
        WorktreeSignals signals = registered.Signals;
        string against = signals.MergeStateAgainst ?? "the default branch";
        var lost = new List<string>();
        if (signals.Dirty is { } dirty && DirtyPhrase(dirty) is { } phrase)
        {
            lost.Add(phrase);
        }
        if (MergeLoss(signals.MergeState, registered.Record.Head, against) is { } merge)
        {
            lost.Add(merge);
        }
        if (signals.Upstream is { Kind: UpstreamKind.Tracking, Ahead: > 0 } upstream)
        {
            lost.Add($"{Counted(upstream.Ahead, "commit", "commits")} not pushed");
        }
        return lost.Count > 0 ? $"{JoinAnd(lost)} will be lost." : null;
    }

    /// <summary>The commits a merge state says would be lost.</summary>
    /// <param name="state">The merge state; <see langword="null"/> when unknown.</param>
    /// <param name="head">The commit checked out, when git reported one.</param>
    /// <param name="against">The default branch the state was measured against.</param>
    /// <returns>The phrase; <see langword="null"/> when the state loses no commits.</returns>
    private static string? MergeLoss(MergeState? state, string? head, string against) =>
        state switch
        {
            { Kind: MergeStateKind.Unmerged, Commits: var commits } => $"{Counted(commits, "commit", "commits")} not on {against}",
            { Kind: MergeStateKind.NoCommits } => "a branch with no commits of its own (it may be new work in progress)",
            { Kind: MergeStateKind.Detached, Contained: false } => $"detached HEAD {ShortHead(head)} and its commits not on {against}",
            _ => null,
        };

    /// <summary>The first seven characters of <paramref name="head"/>, all of it when shorter, <c>HEAD</c> when unknown.</summary>
    /// <param name="head">The commit checked out.</param>
    /// <returns>The short form.</returns>
    private static string ShortHead(string? head)
    {
        string full = head ?? "HEAD";
        return full.Length > 7 ? full[..7] : full;
    }

    /// <summary>The sentence for a git lock, with the reason's trailing dots dropped.</summary>
    /// <param name="locked">The lock reason; empty when none was given, <see langword="null"/> when not locked.</param>
    /// <returns>The sentence; <see langword="null"/> when not locked.</returns>
    private static string? LockSentence(string? locked)
    {
        if (locked is null)
        {
            return null;
        }
        string reason = locked.Trim().TrimEnd('.');
        return reason.Length == 0 ? "It is git-locked (no reason given)." : $"It is git-locked: {reason}.";
    }

    /// <summary>What removing an orphan loses: only the link for a link, else a registration left behind in a live repo.</summary>
    /// <param name="orphan">The orphan.</param>
    /// <returns>The loss; <see langword="null"/> for a folder no repo still registers.</returns>
    private static Loss? OrphanNotice(Orphan orphan)
    {
        if (orphan.Kind == OrphanKind.Link)
        {
            string target = orphan.LinkTarget ?? "its target";
            return new Loss(IsLink: true, $"remove the link only; {target} is not touched.", "Remove the link?");
        }
        if (orphan.LiveGitdir is not { } gitdir)
        {
            return null;
        }
        return new Loss(IsLink: false, $"still registered in {RepoOfGitdir(gitdir)}; removing leaves a prunable registration there.", RemoveAnyway);
    }

    /// <summary>The uncommitted files: <c>1 modified file</c>, <c>3 modified, 2 untracked files</c>.</summary>
    /// <param name="dirty">The dirty counts.</param>
    /// <returns>The phrase; <see langword="null"/> when nothing is uncommitted.</returns>
    private static string? DirtyPhrase(Dirty dirty) =>
        (dirty.Modified, dirty.Untracked) switch
        {
            (0, 0) => null,
            (var modified, 0) => string.Create(CultureInfo.InvariantCulture, $"{modified} modified {Files(modified)}"),
            (0, var untracked) => string.Create(CultureInfo.InvariantCulture, $"{untracked} untracked {Files(untracked)}"),
            (var modified, var untracked) => string.Create(
                CultureInfo.InvariantCulture,
                $"{modified} modified, {untracked} untracked {Files(untracked)}"
            ),
        };

    /// <summary>The noun for <paramref name="count"/> files.</summary>
    /// <param name="count">The count.</param>
    /// <returns><c>file</c> or <c>files</c>.</returns>
    private static string Files(int count) => count == 1 ? "file" : "files";

    /// <summary>A count and its noun: <c>1 commit</c>, <c>4 commits</c>.</summary>
    /// <param name="count">The count.</param>
    /// <param name="one">The noun for one.</param>
    /// <param name="many">The noun for any other count.</param>
    /// <returns>The text.</returns>
    internal static string Counted(int count, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");

    /// <summary>The parts joined as <c>a</c>, <c>a and b</c>, <c>a, b and c</c>.</summary>
    /// <param name="parts">The parts; at least one.</param>
    /// <returns>The text.</returns>
    private static string JoinAnd(List<string> parts) =>
        parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}";

    /// <summary><paramref name="text"/> with its first letter upper-cased.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The capitalised text.</returns>
    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Whether the last name of <paramref name="path"/> is <paramref name="name"/>, ignoring case.</summary>
    /// <param name="path">The path.</param>
    /// <param name="name">The folder name.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    private static bool NameIs(string path, string name) => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase);
}
