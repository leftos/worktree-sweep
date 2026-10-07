using System.Diagnostics;
using WorktreeSweep.Discovery;
using WorktreeSweep.Git;
using WorktreeSweep.Report;
using WorktreeSweep.Scan;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Agent;

/// <summary>Resolves a path an agent asks to remove to the registered linked worktree whose root it is.</summary>
public static class PathResolution
{
    /// <summary>The name endings of a container folder beside its repo, tried in order: <c>x.wt</c> sits beside <c>x</c>.</summary>
    private static readonly string[] ContainerSuffixes = [".wt", "-wt", ".worktrees", "-worktrees", "worktrees"];

    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0 };

    /// <summary>
    /// Resolves <paramref name="path"/> to the registered linked worktree whose root it is. A registered record whose folder is gone
    /// resolves as prunable, with the merge state of its branch read from the main worktree.
    /// </summary>
    /// <remarks>
    /// <para>A path with nothing on disk is looked up in the repo holding its nearest existing ancestor, in the repo a container
    /// ancestor (<c>{repo}.wt</c>, <c>{repo}-wt</c>, <c>{repo}.worktrees</c>, <c>{repo}-worktrees</c>, <c>{repo}worktrees</c>) sits
    /// beside, and in the ancestor's direct child repos; a record registered there resolves as prunable, anything else is
    /// <see cref="RefusalReason.NotFound"/>.</para>
    /// <para>A junction or symbolic link is <see cref="RefusalReason.Link"/>, a file <see cref="RefusalReason.NotAWorktree"/>. A folder
    /// git finds no repo for is <see cref="RefusalReason.Orphan"/> when it has a <c>.git</c> file, else
    /// <see cref="RefusalReason.NotAWorktree"/>. A folder some repo registers as prunable is <see cref="RefusalReason.Orphan"/>; a bare
    /// repo is <see cref="RefusalReason.BareRepo"/> and a main worktree <see cref="RefusalReason.MainWorktree"/>. A folder no record
    /// matches is <see cref="RefusalReason.Orphan"/> with a <c>.git</c> file, <see cref="RefusalReason.Subfolder"/> inside a record,
    /// else <see cref="RefusalReason.NotAWorktree"/>.</para>
    /// <para>One <see cref="VolumeStalls"/> serves the whole call, so a volume one git read times out on is not waited on again.</para>
    /// </remarks>
    /// <param name="path">The path given for removal; relative paths are made absolute against the current folder.</param>
    /// <returns>The resolved worktree, or the refusal.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or not a valid path.</exception>
    /// <exception cref="GitException">Git cannot be started, times out, or fails listing a repo's worktrees.</exception>
    /// <exception cref="IOException">The path's attributes cannot be read for a reason other than its absence.</exception>
    /// <exception cref="UnauthorizedAccessException">The path's attributes cannot be read for lack of access.</exception>
    public static Resolution ResolveOne(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var stalls = new VolumeStalls();
        string absolute = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(absolute);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return ResolveMissing(absolute, stalls);
        }
        bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
        FileSystemInfo info = isDirectory ? new DirectoryInfo(absolute) : new FileInfo(absolute);
        if (Discoverer.IsLink(info))
        {
            return Refuse(RefusalReason.Link, null);
        }
        return isDirectory ? ResolveFolder(PathResolver.Resolve(absolute), stalls) : Refuse(RefusalReason.NotAWorktree, null);
    }

    /// <summary>Resolves an existing folder that is not a link, already resolved to the name the system gives it.</summary>
    private static Resolution ResolveFolder(string target, VolumeStalls stalls)
    {
        bool hasGitFile = File.Exists(Path.Join(target, ".git"));
        if (CommonDirOrNull(target, stalls) is not { } common)
        {
            return Refuse(hasGitFile ? RefusalReason.Orphan : RefusalReason.NotAWorktree, null);
        }
        IReadOnlyList<WorktreeRecord> records = Discoverer.ListWorktrees(common);
        if (records.Count == 0)
        {
            return Refuse(RefusalReason.NotAWorktree, null);
        }
        string mainWorktree = records[0].Path;
        string key = Discoverer.PathKey(target);
        int index = IndexOf(records, key);
        if (index >= 0)
        {
            // Git calls it prunable (its .git file is gone) while the folder is still here: no live registration backs the folder,
            // so removing it is not a prune.
            return records[index].Prunable is not null ? Refuse(RefusalReason.Orphan, mainWorktree) : ResolveRecord(records, index, common, stalls);
        }
        bool underRecord = records.Any(record => key.StartsWith(RecordKey(record) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        RefusalReason reason =
            hasGitFile ? RefusalReason.Orphan
            : underRecord ? RefusalReason.Subfolder
            : RefusalReason.NotAWorktree;
        return Refuse(reason, mainWorktree);
    }

    /// <summary>
    /// Resolves a path with nothing on disk: a prunable record of the repo holding its nearest existing ancestor, of the repo a
    /// container ancestor sits beside, or of one of the ancestor's direct child repos.
    /// </summary>
    private static Resolution ResolveMissing(string absolute, VolumeStalls stalls)
    {
        string? existing = Path.GetDirectoryName(absolute);
        while (existing is not null && !Directory.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing);
        }
        if (existing is null)
        {
            return Refuse(RefusalReason.NotFound, null);
        }
        string canonical = PathResolver.Resolve(existing);
        string key = Discoverer.PathKey(Path.Join(canonical, Path.GetRelativePath(existing, absolute)));
        foreach (string repo in ReposNear(canonical))
        {
            if (CommonDirOrNull(repo, stalls) is not { } common)
            {
                continue;
            }
            IReadOnlyList<WorktreeRecord> records;
            try
            {
                records = Discoverer.ListWorktrees(common);
            }
            catch (GitException error)
            {
                if (error is GitTimeoutException)
                {
                    stalls.Mark(repo);
                }
                Trace.TraceWarning($"skipping repo {repo}: {error.Message}");
                continue;
            }
            int index = IndexOf(records, key);
            if (index >= 0)
            {
                return ResolveRecord(records, index, common, stalls);
            }
        }
        return Refuse(RefusalReason.NotFound, null);
    }

    /// <summary>
    /// The folders a missing path's record may be registered by: <paramref name="dir"/> itself, the repo it sits beside when it is a
    /// container and that repo exists, then its direct child repos not already listed.
    /// </summary>
    private static List<string> ReposNear(string dir)
    {
        var repos = new List<string> { dir };
        if (ContainerRepo(dir) is { } sibling && Directory.Exists(sibling))
        {
            repos.Add(sibling);
        }
        foreach (string child in ChildRepos(dir))
        {
            string childKey = Discoverer.PathKey(child);
            if (!repos.Exists(repo => Discoverer.PathKey(repo) == childKey))
            {
                repos.Add(child);
            }
        }
        return repos;
    }

    /// <summary>
    /// The repos that are direct children of <paramref name="dir"/> (a child folder with a <c>.git</c> folder, neither a link, as
    /// discovery finds them), in ordinal order; a folder that cannot be listed has none, with a trace warning.
    /// </summary>
    private static List<string> ChildRepos(string dir)
    {
        try
        {
            return
            [
                .. Directory
                    .EnumerateDirectories(dir, "*", AllEntries)
                    .Where(child => IsPlainDir(child) && IsPlainDir(Path.Join(child, ".git")))
                    .Order(StringComparer.Ordinal),
            ];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"cannot list {dir}: {error.Message}");
            return [];
        }
    }

    /// <summary>Whether <paramref name="path"/> is a folder that is not a reparse point; <see langword="false"/> when unreadable.</summary>
    private static bool IsPlainDir(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The repo a container folder sits beside: <c>D:\x</c> for <c>D:\x.wt</c>, <c>D:\x-wt</c> or <c>D:\x.worktrees</c>.</summary>
    /// <returns>The repo's path; <see langword="null"/> when the folder's name has no container ending, or nothing before it.</returns>
    private static string? ContainerRepo(string dir)
    {
        string name = Path.GetFileName(dir);
        string? suffix = Array.Find(ContainerSuffixes, ending => name.EndsWith(ending, StringComparison.OrdinalIgnoreCase));
        if (suffix is null || name.Length == suffix.Length || Path.GetDirectoryName(dir) is not { } parent)
        {
            return null;
        }
        return Path.Join(parent, name[..^suffix.Length]);
    }

    /// <summary>
    /// The record at <paramref name="index"/> of the repo whose common git dir is <paramref name="common"/>: refused when it is the
    /// main worktree or bare, else built into a candidate.
    /// </summary>
    private static Resolution ResolveRecord(IReadOnlyList<WorktreeRecord> records, int index, string common, VolumeStalls stalls)
    {
        string mainWorktree = records[0].Path;
        WorktreeRecord record = records[index];
        if (record.Bare)
        {
            return Refuse(RefusalReason.BareRepo, mainWorktree);
        }
        if (index == 0)
        {
            return Refuse(RefusalReason.MainWorktree, mainWorktree);
        }
        return new Resolution.Resolved(BuildCandidate(mainWorktree, common, record, stalls), mainWorktree);
    }

    /// <summary>
    /// The candidate for a linked worktree, built as a scan builds it; for a prunable record, whose folder is gone, the merge state
    /// of its branch is then read from the main worktree.
    /// </summary>
    private static RegisteredCandidate BuildCandidate(string mainWorktree, string common, WorktreeRecord record, VolumeStalls stalls)
    {
        DefaultBranches defaults = Scanner.DefaultBranchesOrNone(mainWorktree, stalls);
        RegisteredCandidate candidate = Scanner.ReadRegistered(mainWorktree, defaults, common, record, stalls);
        return record.Prunable is null ? candidate : WithMergeStateFromMain(candidate, mainWorktree, defaults, stalls);
    }

    /// <summary>
    /// <paramref name="candidate"/> with the merge state of its branch read from the main worktree; a read that fails leaves it
    /// unknown, its message added to the signal errors and traced.
    /// </summary>
    private static RegisteredCandidate WithMergeStateFromMain(
        RegisteredCandidate candidate,
        string mainWorktree,
        DefaultBranches defaults,
        VolumeStalls stalls
    )
    {
        var reads = new SignalReads(stalls, mainWorktree);
        (MergeState State, string Against)? merge = reads.Read(() => SignalReader.ReadMergeState(mainWorktree, defaults, candidate.Record));
        foreach (string error in reads.Errors)
        {
            Trace.TraceWarning($"worktree {candidate.Path}: {error}");
        }
        WorktreeSignals signals = candidate.Signals with
        {
            MergeState = merge?.State,
            MergeStateAgainst = merge?.Against,
            Errors = [.. candidate.Signals.Errors, .. reads.Errors],
        };
        return candidate with { Signals = signals };
    }

    /// <summary>The repo's common git dir as seen from <paramref name="dir"/>; <see langword="null"/> when git finds no repo there.</summary>
    /// <exception cref="GitException">Git cannot be started or times out, or the volume is stalled.</exception>
    private static string? CommonDirOrNull(string dir, VolumeStalls stalls)
    {
        stalls.ThrowIfStalled(dir);
        GitStatus status = GitRunner.RunStatus(dir, ["rev-parse", "--path-format=absolute", "--git-common-dir"]);
        return status.Success ? Discoverer.FromGitPath(status.Stdout.Trim()) : null;
    }

    /// <summary>The index of the record whose <see cref="RecordKey"/> is <paramref name="key"/>; -1 when none is.</summary>
    private static int IndexOf(IReadOnlyList<WorktreeRecord> records, string key)
    {
        for (int index = 0; index < records.Count; index++)
        {
            if (RecordKey(records[index]) == key)
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>A record's comparison key, from its path resolved (its nearest existing ancestor when the folder is gone).</summary>
    private static string RecordKey(WorktreeRecord record) => Discoverer.PathKey(PathResolver.Resolve(record.Path));

    private static Resolution.Refusal Refuse(RefusalReason reason, string? repo) => new(reason, repo);
}
