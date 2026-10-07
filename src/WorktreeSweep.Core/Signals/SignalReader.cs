using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using WorktreeSweep.Discovery;
using WorktreeSweep.Git;

namespace WorktreeSweep.Signals;

/// <summary>
/// Reads the signals shown for each candidate: merge state, dirty counts, upstream, last activity and size.
/// </summary>
/// <remarks>
/// <para>Reading writes nothing: every git call goes through <see cref="GitRunner"/>, and <c>git merge-tree --write-tree</c> writes
/// its objects to a scratch object directory that borrows the repo's objects as an alternate.</para>
/// <para>A junction or symbolic link is a link, never a tree: it is never sized, walked or followed.</para>
/// <para>Diagnostics go to <see cref="Trace"/>: a signal that could not be read as a warning, a skipped entry as a plain line.</para>
/// </remarks>
public static partial class SignalReader
{
    /// <summary>PLINQ's ceiling on <see cref="ParallelEnumerable.WithDegreeOfParallelism{TSource}"/>.</summary>
    private const int MaxDegreeOfParallelism = 512;

    /// <summary>
    /// Finds a repo's default branches: <c>refs/remotes/origin/HEAD</c> for the origin default; for the local default, the local
    /// branch of the same name, else <c>main</c>, else <c>master</c>. An <c>origin/HEAD</c> that points to a branch that does not
    /// exist is dropped, with a traced warning, and the local default falls back to <c>main</c>, else <c>master</c>.
    /// </summary>
    /// <param name="repo">A folder inside the repo.</param>
    /// <param name="stalls">The volumes an earlier git call has stalled; a repo on one of them is not asked for anything.</param>
    /// <returns>The defaults that exist.</returns>
    /// <exception cref="GitTimeoutException">The repo's volume is stalled, so git is not started; the message names the volume.</exception>
    /// <exception cref="GitException">Git cannot be started or fails unexpectedly.</exception>
    public static DefaultBranches ReadDefaultBranches(string repo, VolumeStalls stalls)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(stalls);
        stalls.ThrowIfStalled(repo);
        GitStatus symref = GitRunner.RunStatus(repo, ["symbolic-ref", "--quiet", "refs/remotes/origin/HEAD"]);
        string? origin = symref.Success ? StripPrefix(symref.Stdout.Trim(), "refs/remotes/") : null;
        if (origin is not null && !RefExists(repo, "refs/remotes/" + origin))
        {
            Trace.TraceWarning($"repo {repo}: origin/HEAD points to {origin}, which does not exist; fix it with git remote set-head origin --auto");
            origin = null;
        }
        var names = new List<string>();
        if (origin is not null && StripPrefix(origin, "origin/") is { } originName)
        {
            names.Add(originName);
        }
        names.AddRange(["main", "master"]);
        string? local = names.FirstOrDefault(name => RefExists(repo, $"refs/heads/{name}"));
        return new DefaultBranches { Local = local, Origin = origin };
    }

    /// <summary>
    /// Reads every signal of a registered worktree. A signal that fails is left <see langword="null"/> and its error recorded in
    /// <see cref="WorktreeSignals.Errors"/> and traced, so one broken worktree never hides the others. A git read that times out
    /// (<see cref="GitTimeoutException"/>) means the worktree's volume is stalled, so the volume is marked in
    /// <paramref name="stalls"/> and the worktree's remaining signals are skipped, the size walk included, with one notice naming the
    /// volume recorded after the timeout's message. A worktree whose volume another worktree has already stalled starts no git call
    /// at all.
    /// </summary>
    /// <param name="defaults">The default branches of the worktree's repo.</param>
    /// <param name="record">The worktree's record.</param>
    /// <param name="stalls">The volumes an earlier git call has stalled, shared by the scan's worktrees.</param>
    /// <returns>The signals; all <see langword="null"/> for a prunable registration.</returns>
    public static WorktreeSignals ReadWorktreeSignals(DefaultBranches defaults, WorktreeRecord record, VolumeStalls stalls)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(stalls);
        if (record.Prunable is not null)
        {
            return new WorktreeSignals();
        }
        string dir = record.Path;
        var reads = new SignalReads(stalls, dir);
        (MergeState State, string Against)? merge = reads.Read(() => ReadMergeState(dir, defaults, record));
        Dirty? dirty = reads.Read(() => ReadDirty(dir));
        Upstream? upstream = record.Branch is { } branch ? reads.Read(() => ReadUpstream(dir, branch)) : null;
        long? lastActivity = reads.Read(() => LastActivity(dir));
        SizeInfo? size = reads.Read(() => WalkSize(dir));
        foreach (string error in reads.Errors)
        {
            Trace.TraceWarning($"worktree {dir}: {error}");
        }
        return new WorktreeSignals
        {
            MergeState = merge?.State,
            MergeStateAgainst = merge?.Against,
            Dirty = dirty,
            Upstream = upstream,
            LastActivityUnix = lastActivity,
            Size = size,
            Errors = reads.Errors,
        };
    }

    /// <summary>Counts modified and untracked entries with <c>git status --porcelain=v1 -z</c>.</summary>
    /// <param name="dir">The worktree.</param>
    /// <returns>The counts.</returns>
    /// <exception cref="GitException">Git fails.</exception>
    public static Dirty ReadDirty(string dir)
    {
        ArgumentNullException.ThrowIfNull(dir);
        return ParseStatusZ(GitRunner.RunRaw(dir, ["status", "--porcelain=v1", "-z", "--untracked-files=normal"]));
    }

    /// <summary>Parses <c>git status --porcelain=v1 -z</c> output into dirty counts; ignored entries (<c>!!</c>) are not counted.</summary>
    /// <param name="text">The NUL-separated output.</param>
    /// <returns>The counts.</returns>
    public static Dirty ParseStatusZ(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] fields = text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        int modified = 0;
        int untracked = 0;
        for (int index = 0; index < fields.Length; index++)
        {
            string entry = fields[index];
            string code = entry.Length >= 2 ? entry[..2] : entry;
            if (code == "??")
            {
                untracked++;
            }
            else if (code != "!!")
            {
                modified++;
                // A rename or copy is followed by a field holding its source path.
                if (code.AsSpan().IndexOfAny('R', 'C') >= 0)
                {
                    index++;
                }
            }
        }
        return new Dirty { Modified = modified, Untracked = untracked };
    }

    /// <summary>The upstream state of a local branch.</summary>
    /// <param name="dir">A worktree of the branch's repo.</param>
    /// <param name="branch">The branch, without <c>refs/heads/</c>.</param>
    /// <returns>The state.</returns>
    /// <exception cref="GitException">Git fails unexpectedly.</exception>
    public static Upstream ReadUpstream(string dir, string branch)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(branch);
        string spec = $"{branch}@{{upstream}}";
        if (GitRunner.RunStatus(dir, ["rev-parse", "--abbrev-ref", spec]).Success)
        {
            return Upstream.Tracking(Count(dir, $"{spec}..refs/heads/{branch}"));
        }
        return GitRunner.RunStatus(dir, ["config", "--get", $"branch.{branch}.merge"]).Success ? Upstream.Gone : Upstream.None;
    }

    /// <summary>The later of the HEAD commit time and the modification time of the worktree's index, in Unix seconds.</summary>
    /// <param name="dir">The worktree.</param>
    /// <returns>The time; <see langword="null"/> when neither can be read.</returns>
    /// <exception cref="GitException">Only when git cannot be started, does not exit within <see cref="GitRunner.CallTimeout"/>, or
    /// leaves its output open.</exception>
    public static long? LastActivity(string dir)
    {
        ArgumentNullException.ThrowIfNull(dir);
        GitStatus log = GitRunner.RunStatus(dir, ["log", "-1", "--format=%ct"]);
        long? commitTime =
            log.Success && long.TryParse(log.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) ? seconds : null;
        long? indexTime = Discoverer.ReadGitdirFile(dir) is { } gitdir ? ModifiedUnix(new FileInfo(Path.Join(gitdir, "index"))) : null;
        return Later(commitTime, indexTime);
    }

    /// <summary>Maps <paramref name="items"/> on at most one worker per processor, keeping the input order.</summary>
    /// <typeparam name="TItem">The item type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="items">The items.</param>
    /// <param name="map">The work for one item; called concurrently.</param>
    /// <returns>One result per item, in the order of <paramref name="items"/>.</returns>
    /// <exception cref="Exception">Whatever <paramref name="map"/> threw first, unwrapped, with its original stack trace.</exception>
    public static IReadOnlyList<TResult> ParallelMap<TItem, TResult>(IReadOnlyList<TItem> items, Func<TItem, TResult> map)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(map);
        int workers = Math.Clamp(items.Count, 1, Math.Min(Environment.ProcessorCount, MaxDegreeOfParallelism));
        try
        {
            return [.. items.AsParallel().AsOrdered().WithDegreeOfParallelism(workers).Select(map)];
        }
        catch (AggregateException error) when (error.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();
            throw;
        }
    }

    private static bool RefExists(string dir, string refName) => ExitIsAnswer(dir, ["rev-parse", "--verify", "--quiet", refName]);

    /// <summary>Runs a git command whose exit code 0 means yes and 1 means no.</summary>
    /// <exception cref="GitException">Git exits with any other code, cannot be started, does not exit within
    /// <see cref="GitRunner.CallTimeout"/>, or leaves its output open.</exception>
    private static bool ExitIsAnswer(string dir, IReadOnlyList<string> args)
    {
        GitStatus status = GitRunner.RunStatus(dir, args);
        return status.Code switch
        {
            0 => true,
            1 => false,
            _ => throw GitRunner.Failure(dir, args, status),
        };
    }

    private static int Count(string dir, string range)
    {
        string text = GitRunner.Run(dir, ["rev-list", "--count", range]);
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            ? count
            : throw new GitException($"`git rev-list --count {range}` in {dir} printed \"{text}\"");
    }

    private static string? StripPrefix(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : null;

    /// <summary>The lines of <paramref name="text"/>, without their line ends; none for empty text.</summary>
    private static IEnumerable<string> Lines(string text)
    {
        using var reader = new StringReader(text);
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            yield return line;
        }
    }

    private static long? Later(long? a, long? b) =>
        a is null ? b
        : b is null ? a
        : Math.Max(a.Value, b.Value);

    /// <summary>An entry's modification time in Unix seconds; <see langword="null"/> when it is missing or before 1970.</summary>
    private static long? ModifiedUnix(FileSystemInfo entry) => entry.Exists ? UnixSeconds(entry.LastWriteTimeUtc) : null;

    /// <summary>A UTC time in Unix seconds; <see langword="null"/> before 1970.</summary>
    private static long? UnixSeconds(DateTime utc)
    {
        long seconds = new DateTimeOffset(utc).ToUnixTimeSeconds();
        return seconds >= 0 ? seconds : null;
    }
}

/// <summary>
/// Reads one worktree's signals in order, recording each failure's message. A read that fails with
/// <see cref="GitTimeoutException"/> means the worktree's volume is stalled, where every later git call would wait the full time
/// limit too: the volume is marked in <paramref name="stalls"/>, so no later call on it is started, and this worktree's remaining
/// reads are skipped, each reporting <see langword="default"/>, with one notice naming the volume recorded after the timeout's own
/// message. A worktree whose volume another worktree has already stalled makes no git call and records the same notice alone.
/// </summary>
/// <param name="stalls">The volumes an earlier git call has stalled, shared by the scan's worktrees.</param>
/// <param name="path">The worktree whose signals are read.</param>
internal sealed class SignalReads(VolumeStalls stalls, string path)
{
    private readonly List<string> _errors = [];
    private bool _timedOut;

    /// <summary>The volume the worktree's git calls run on.</summary>
    public string Volume { get; } = VolumeStalls.Volume(path);

    /// <summary>The notice recorded once when a read times out or is skipped for a stalled volume.</summary>
    public string TimeoutNotice => $"git timed out on {Volume}; remaining signals skipped";

    /// <summary>The failures' messages, in order, plus the one skip notice a timeout or a stalled volume adds.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Runs <paramref name="read"/> and returns its result.</summary>
    /// <typeparam name="T">The signal's type.</typeparam>
    /// <param name="read">The read; not called once an earlier read has timed out or the volume is stalled.</param>
    /// <returns>The signal; <see langword="default"/> when the read fails or is skipped after a timeout.</returns>
    public T? Read<T>(Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (Skipped())
        {
            return default;
        }
        try
        {
            return read();
        }
        catch (GitTimeoutException error)
        {
            stalls.Mark(path);
            _errors.Add(error.Message);
            _errors.Add(TimeoutNotice);
            _timedOut = true;
            return default;
        }
#pragma warning disable CA1031 // Every failure of one signal is recorded, so one broken worktree never hides the others.
        catch (Exception error)
#pragma warning restore CA1031
        {
            _errors.Add(error.Message);
            return default;
        }
    }

    /// <summary>Whether this read is not run: an earlier read timed out, or another worktree's read stalled the volume.</summary>
    /// <returns><see langword="true"/> when the read is skipped, with the notice recorded the first time.</returns>
    private bool Skipped()
    {
        if (_timedOut)
        {
            return true;
        }
        if (!stalls.IsStalled(path))
        {
            return false;
        }
        _errors.Add(TimeoutNotice);
        _timedOut = true;
        return true;
    }
}
