using System.Globalization;
using WorktreeSweep.Discovery;
using WorktreeSweep.Review;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Report;

/// <summary>The seven lines the picker's detail pane shows for the highlighted candidate, as plain text.</summary>
public static class DetailText
{
    /// <summary>What the pane says when removing the candidate loses nothing.</summary>
    private const string NothingIsLost = "Nothing is lost.";

    /// <summary>
    /// The seven lines for <paramref name="candidate"/>: its path, what it is, its state or notes, its last activity or write and
    /// size, its marks, what removing it loses, and any signals that could not be read. Every date is local to
    /// <paramref name="utcOffset"/> and its age is measured from <paramref name="nowUnix"/>.
    /// </summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="nowUnix">The time ages are measured from, in Unix seconds.</param>
    /// <param name="utcOffset">The local time zone's offset from UTC at a Unix time.</param>
    /// <returns>The seven lines, in order.</returns>
    public static IReadOnlyList<string> Lines(Candidate candidate, long nowUnix, Func<long, TimeSpan> utcOffset)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(utcOffset);
        string Date(long unix) => FormatLocal(unix, utcOffset(unix), nowUnix);
        (IReadOnlyList<string> Text, string Errors) detail = candidate switch
        {
            RegisteredCandidate registered => (RegisteredLines(registered, Date), string.Join("; ", registered.Signals.Errors)),
            OrphanCandidate orphan => (OrphanLines(orphan, Date), ""),
            _ => throw new ArgumentException($"unknown candidate type {candidate.GetType().Name}", nameof(candidate)),
        };
        return [.. detail.Text, LossText.For(candidate)?.Text ?? NothingIsLost, detail.Errors];
    }

    /// <summary>
    /// <paramref name="unix"/> in a time zone <paramref name="offset"/> east of UTC as <c>YYYY-MM-DD HH:MM ({age} ago)</c>, the age
    /// measured from <paramref name="nowUnix"/> and in the unit <see cref="ReportTable.Age"/> picks.
    /// </summary>
    /// <param name="unix">The time, in Unix seconds.</param>
    /// <param name="offset">The zone's offset from UTC.</param>
    /// <param name="nowUnix">The time ages are measured from, in Unix seconds.</param>
    /// <returns>The local date and time and the age.</returns>
    public static string FormatLocal(long unix, TimeSpan offset, long nowUnix)
    {
        DateTimeOffset local = DateTimeOffset.FromUnixTimeSeconds(unix).ToOffset(offset);
        return string.Create(CultureInfo.InvariantCulture, $"{local:yyyy-MM-dd HH:mm} ({ReportTable.Age(unix, nowUnix)} ago)");
    }

    /// <summary>The five lines a registered worktree's detail holds, before the loss and error lines.</summary>
    /// <param name="registered">The worktree.</param>
    /// <param name="date">A Unix time formatted as <see cref="FormatLocal"/> does.</param>
    /// <returns>The five lines.</returns>
    private static IReadOnlyList<string> RegisteredLines(RegisteredCandidate registered, Func<long, string> date)
    {
        WorktreeSignals signals = registered.Signals;
        string against = signals.MergeStateAgainst ?? "the default branch";
        var state = new List<string>();
        if (signals.MergeState is { } merge)
        {
            state.Add(MergeWords(merge, against));
        }
        if (signals.Dirty is { } dirty)
        {
            state.Add(
                dirty.Modified == 0 && dirty.Untracked == 0
                    ? "clean"
                    : string.Create(CultureInfo.InvariantCulture, $"{dirty.Modified} modified, {dirty.Untracked} untracked")
            );
        }
        if (signals.Upstream is { } upstream)
        {
            state.Add(UpstreamWords(upstream));
        }
        var activity = new List<string>();
        if (signals.LastActivityUnix is long then)
        {
            activity.Add(string.Create(CultureInfo.InvariantCulture, $"last activity {date(then)}"));
        }
        if (signals.Size is { } size)
        {
            activity.Add(SizeWords(size));
        }
        var marks = new List<string>();
        if (registered.Record.Locked is { } locked)
        {
            marks.Add(locked.Length == 0 ? "git-locked" : $"git-locked: {locked}");
        }
        if (registered.Record.Prunable is { } prunable)
        {
            marks.Add($"prunable: {prunable}");
        }
        if (registered.Released is { } released)
        {
            marks.Add(string.Create(CultureInfo.InvariantCulture, $"released {date(released.ReleasedAtUnix)}: {ReasonLabel(released.Reason)}"));
        }
        return
        [
            registered.Path,
            string.Create(CultureInfo.InvariantCulture, $"repo {registered.Repo}; {Branch(registered.Record)}"),
            string.Join("; ", state),
            string.Join("; ", activity),
            string.Join("; ", marks),
        ];
    }

    /// <summary>The five lines an orphan's detail holds, before the loss and error lines.</summary>
    /// <param name="candidate">The orphan.</param>
    /// <param name="date">A Unix time formatted as <see cref="FormatLocal"/> does.</param>
    /// <returns>The five lines.</returns>
    private static IReadOnlyList<string> OrphanLines(OrphanCandidate candidate, Func<long, string> date)
    {
        Orphan orphan = candidate.Orphan;
        bool isLink = orphan.Kind == OrphanKind.Link;
        var notes = new List<string>();
        if (orphan.LinkTarget is { } target)
        {
            notes.Add($"→ {target}, not touched");
        }
        if (orphan.LiveGitdir is { } gitdir)
        {
            notes.Add($"still registered in {LossText.RepoOfGitdir(gitdir)}");
        }
        if (orphan.StaleGitdir)
        {
            notes.Add("stale .git");
        }
        var activity = new List<string>();
        if (candidate.Size.LastWriteUnix is long then)
        {
            activity.Add(string.Create(CultureInfo.InvariantCulture, $"last write {date(then)}"));
        }
        if (!isLink)
        {
            activity.Add(SizeWords(candidate.Size));
        }
        return
        [
            orphan.Path,
            string.Create(CultureInfo.InvariantCulture, $"container {orphan.Container}; {(isLink ? "link" : "folder")}"),
            string.Join("; ", notes),
            string.Join("; ", activity),
            "",
        ];
    }

    /// <summary>The branch line's tail: <c>branch {b}</c>, <c>detached {first 7 of head}</c>, or <c>detached</c>.</summary>
    /// <param name="record">The worktree's record.</param>
    /// <returns>The text.</returns>
    private static string Branch(WorktreeRecord record) =>
        record.Branch is { } branch ? $"branch {branch}"
        : record.Head is { } head ? $"detached {(head.Length > 7 ? head[..7] : head)}"
        : "detached";

    /// <summary>The merge state as a phrase against <paramref name="against"/>, as the detail pane reads it.</summary>
    /// <param name="state">The merge state.</param>
    /// <param name="against">The branch the state was measured against.</param>
    /// <returns>The phrase.</returns>
    private static string MergeWords(MergeState state, string against) =>
        state.Kind switch
        {
            MergeStateKind.Ancestor => $"merged into {against}",
            MergeStateKind.NoCommits => $"no commits of its own (against {against})",
            MergeStateKind.PatchesApplied => $"cherry-picked into {against}",
            MergeStateKind.ContentContained => $"squashed into {against}",
            MergeStateKind.Unmerged => string.Create(
                CultureInfo.InvariantCulture,
                $"{state.Commits} {(state.Commits == 1 ? "commit" : "commits")} not on {against}"
            ),
            MergeStateKind.Detached => state.Contained ? $"detached, contained in {against}" : $"detached, not contained in {against}",
            _ => throw new ArgumentException($"unknown merge state kind {state.Kind}", nameof(state)),
        };

    /// <summary>The upstream as a phrase, as the detail pane reads it.</summary>
    /// <param name="upstream">The upstream.</param>
    /// <returns>The phrase.</returns>
    private static string UpstreamWords(Upstream upstream) =>
        upstream.Kind switch
        {
            UpstreamKind.None => "no upstream",
            UpstreamKind.Gone => "upstream gone",
            _ when upstream.Ahead == 0 => "pushed",
            _ => string.Create(CultureInfo.InvariantCulture, $"+{upstream.Ahead} not pushed"),
        };

    /// <summary>
    /// A size as <c>{bytes}, {files} files</c> and, when part of it could not be read, <c>, {n} unreadable</c>; the bytes read
    /// <c>at least</c> before a partly measured size, which is a lower bound.
    /// </summary>
    /// <param name="size">The size.</param>
    /// <returns>The words.</returns>
    private static string SizeWords(SizeInfo size)
    {
        string words = string.Create(CultureInfo.InvariantCulture, $"{ReportTable.SizeText(size.Bytes, size.Unreadable > 0)}, {size.Files} files");
        return size.Unreadable > 0 ? string.Create(CultureInfo.InvariantCulture, $"{words}, {size.Unreadable} unreadable") : words;
    }

    /// <summary>The label for a released reason, as the detail pane reads it.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>The label.</returns>
    private static string ReasonLabel(Reason reason) =>
        reason.Kind switch
        {
            ReasonKind.Locked => "locked",
            ReasonKind.MayHold => "may be held",
            ReasonKind.TooBigForRecycleBin => "too big for the Recycle Bin",
            ReasonKind.ShellTimeout => "the Shell timed out",
            ReasonKind.WouldLose => "would lose work",
            ReasonKind.CallerHolds => "held by the calling shell",
            ReasonKind.NotRemovable => "not removable",
            _ => throw new ArgumentException($"unknown reason kind {reason.Kind}", nameof(reason)),
        };
}
