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

    /// <summary>Seconds in a day.</summary>
    private const long DaySeconds = 86_400;

    /// <summary>Seconds in an hour.</summary>
    private const long HourSeconds = 3600;

    /// <summary>Seconds in a minute.</summary>
    private const long MinuteSeconds = 60;

    /// <summary>Characters a year is zero-padded to, the sign counting towards the width.</summary>
    private const int YearWidth = 4;

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
            RegisteredCandidate registered => (RegisteredLines(registered, Date), ErrorsLine(registered.Signals.Errors)),
            OrphanCandidate orphan => (OrphanLines(orphan, Date), ""),
            _ => throw new ArgumentException($"unknown candidate type {candidate.GetType().Name}", nameof(candidate)),
        };
        return [.. detail.Text, LossText.For(candidate)?.Text ?? NothingIsLost, detail.Errors];
    }

    /// <summary>
    /// <paramref name="unix"/> in a time zone <paramref name="offset"/> east of UTC as <c>YYYY-MM-DD HH:MM ({age} ago)</c>, the age
    /// measured from <paramref name="nowUnix"/> and in the unit <see cref="ReportTable.Age"/> picks. Plain arithmetic, so any
    /// time and any offset print rather than throw: the year is the proleptic Gregorian one, its digits zero-padded to four, and
    /// an offset of any sign and length is added to <paramref name="unix"/>.
    /// </summary>
    /// <param name="unix">The time, in Unix seconds.</param>
    /// <param name="offset">The zone's offset from UTC.</param>
    /// <param name="nowUnix">The time ages are measured from, in Unix seconds.</param>
    /// <returns>The local date and time and the age.</returns>
    public static string FormatLocal(long unix, TimeSpan offset, long nowUnix)
    {
        long local = unix + (offset.Ticks / TimeSpan.TicksPerSecond);
        (long Year, long Month, long Day) date = CivilFromDays(FloorDiv(local, DaySeconds));
        long seconds = FloorMod(local, DaySeconds);
        string day = string.Create(CultureInfo.InvariantCulture, $"{PaddedYear(date.Year)}-{date.Month:D2}-{date.Day:D2}");
        string time = string.Create(CultureInfo.InvariantCulture, $"{seconds / HourSeconds:D2}:{seconds % HourSeconds / MinuteSeconds:D2}");
        string age = ReportTable.Age(unix, nowUnix);
        return $"{day} {time} ({age} ago)";
    }

    /// <summary>The five lines a registered worktree's detail holds, before the loss and error lines.</summary>
    /// <param name="registered">The worktree.</param>
    /// <param name="date">A Unix time formatted as <see cref="FormatLocal"/> does.</param>
    /// <returns>The five lines.</returns>
    private static IReadOnlyList<string> RegisteredLines(RegisteredCandidate registered, Func<long, string> date) =>
        [
            registered.Path,
            string.Create(CultureInfo.InvariantCulture, $"repo {registered.Repo}; {Branch(registered.Record)}"),
            StateLine(registered.Signals),
            ActivityLine(registered.Signals, date),
            MarksLine(registered, date),
        ];

    /// <summary>The `<c>; </c>`-joined state: the merge words, the dirty counts and the upstream.</summary>
    /// <param name="signals">The worktree's signals.</param>
    /// <returns>The line.</returns>
    private static string StateLine(WorktreeSignals signals)
    {
        string against = signals.MergeStateAgainst ?? "the default branch";
        var state = new List<string>();
        if (signals.MergeState is { } merge)
        {
            state.Add(MergeWords(merge, against));
        }
        if (signals.Dirty is { } dirty)
        {
            state.Add(DirtyWords(dirty));
        }
        if (signals.Upstream is { } upstream)
        {
            state.Add(UpstreamWords(upstream));
        }
        return string.Join("; ", state);
    }

    /// <summary>The uncommitted work as a phrase, or <c>clean</c> when there is none.</summary>
    /// <param name="dirty">The dirty counts.</param>
    /// <returns>The phrase.</returns>
    private static string DirtyWords(Dirty dirty) =>
        dirty.Modified == 0 && dirty.Untracked == 0
            ? "clean"
            : string.Create(CultureInfo.InvariantCulture, $"{dirty.Modified} modified, {dirty.Untracked} untracked");

    /// <summary>The `<c>; </c>`-joined last activity and size.</summary>
    /// <param name="signals">The worktree's signals.</param>
    /// <param name="date">A Unix time formatted as <see cref="FormatLocal"/> does.</param>
    /// <returns>The line.</returns>
    private static string ActivityLine(WorktreeSignals signals, Func<long, string> date)
    {
        var activity = new List<string>();
        if (signals.LastActivityUnix is long then)
        {
            activity.Add(string.Create(CultureInfo.InvariantCulture, $"last activity {date(then)}"));
        }
        if (signals.Size is { } size)
        {
            activity.Add(SizeWords(size));
        }
        return string.Join("; ", activity);
    }

    /// <summary>The `<c>; </c>`-joined marks: the git lock, why a registration is prunable, and the release.</summary>
    /// <param name="registered">The worktree.</param>
    /// <param name="date">A Unix time formatted as <see cref="FormatLocal"/> does.</param>
    /// <returns>The line.</returns>
    private static string MarksLine(RegisteredCandidate registered, Func<long, string> date)
    {
        var marks = new List<string>();
        if (registered.Record.Locked is { } locked)
        {
            marks.Add(LockedWord(locked));
        }
        if (registered.Record.Prunable is { } prunable)
        {
            marks.Add($"prunable: {prunable}");
        }
        if (registered.Released is { } released)
        {
            marks.Add(string.Create(CultureInfo.InvariantCulture, $"released {date(released.ReleasedAtUnix)}: {ReasonLabel(released.Reason)}"));
        }
        return string.Join("; ", marks);
    }

    /// <summary>The git lock as a phrase.</summary>
    /// <param name="reason">The lock reason; empty when none was given.</param>
    /// <returns>The phrase.</returns>
    private static string LockedWord(string reason) => reason.Length == 0 ? "git-locked" : $"git-locked: {reason}";

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
            MergeStateKind.Unmerged => UnmergedWords(state.Commits, against),
            MergeStateKind.Detached => DetachedWords(state.Contained, against),
            _ => throw new ArgumentException($"unknown merge state kind {state.Kind}", nameof(state)),
        };

    /// <summary>The commits a branch has that the default branch lacks.</summary>
    /// <param name="commits">The commit count.</param>
    /// <param name="against">The branch the state was measured against.</param>
    /// <returns>The phrase.</returns>
    private static string UnmergedWords(int commits, string against) =>
        string.Create(CultureInfo.InvariantCulture, $"{commits} {(commits == 1 ? "commit" : "commits")} not on {against}");

    /// <summary>A detached HEAD, and whether it is contained in <paramref name="against"/>.</summary>
    /// <param name="contained">Whether HEAD is reachable from <paramref name="against"/>.</param>
    /// <param name="against">The branch the state was measured against.</param>
    /// <returns>The phrase.</returns>
    private static string DetachedWords(bool contained, string against) =>
        contained ? $"detached, contained in {against}" : $"detached, not contained in {against}";

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

    /// <summary>The signal errors flattened to one line each and joined with <c>; </c>.</summary>
    /// <param name="errors">The messages.</param>
    /// <returns>The line; empty when there are none.</returns>
    private static string ErrorsLine(IReadOnlyList<string> errors) => string.Join("; ", errors.Select(ReportTable.OneLine));

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

    /// <summary>The proleptic Gregorian <c>(year, month, day)</c> of the day <paramref name="days"/> after 1970-01-01.</summary>
    /// <param name="days">The day count, negative before the epoch.</param>
    /// <returns>The date.</returns>
    private static (long Year, long Month, long Day) CivilFromDays(long days)
    {
        long shifted = days + 719_468;
        long era = FloorDiv(shifted, 146_097);
        long dayOfEra = FloorMod(shifted, 146_097);
        long yearOfEra = (dayOfEra - (dayOfEra / 1460) + (dayOfEra / 36_524) - (dayOfEra / 146_096)) / 365;
        long dayOfYear = dayOfEra - ((365 * yearOfEra) + (yearOfEra / 4) - (yearOfEra / 100));
        long monthFromMarch = ((5 * dayOfYear) + 2) / 153;
        long day = dayOfYear - (((153 * monthFromMarch) + 2) / 5) + 1;
        long month = monthFromMarch < 10 ? monthFromMarch + 3 : monthFromMarch - 9;
        long year = yearOfEra + (era * 400);
        return (month <= 2 ? year + 1 : year, month, day);
    }

    /// <summary>A year zero-padded to four characters, the sign counting towards the width.</summary>
    /// <param name="year">The year; either sign.</param>
    /// <returns>The text.</returns>
    private static string PaddedYear(long year)
    {
        string text = year.ToString(CultureInfo.InvariantCulture);
        if (text.Length >= YearWidth)
        {
            return text;
        }
        return text[0] == '-' ? "-" + text[1..].PadLeft(YearWidth - 1, '0') : text.PadLeft(YearWidth, '0');
    }

    /// <summary><paramref name="value"/> divided by the positive <paramref name="divisor"/>, rounded towards negative infinity.</summary>
    /// <param name="value">The dividend.</param>
    /// <param name="divisor">The divisor; positive.</param>
    /// <returns>The quotient.</returns>
    private static long FloorDiv(long value, long divisor)
    {
        long quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    /// <summary><paramref name="value"/> modulo the positive <paramref name="divisor"/>, always in <c>[0, divisor)</c>.</summary>
    /// <param name="value">The dividend.</param>
    /// <param name="divisor">The divisor; positive.</param>
    /// <returns>The remainder.</returns>
    private static long FloorMod(long value, long divisor)
    {
        long remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }
}
