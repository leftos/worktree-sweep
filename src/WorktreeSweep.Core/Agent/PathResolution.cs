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
    /// <exception cref="GitTimeoutException">A git read times out or its volume is stalled, so a repo that may register the path
    /// cannot be read; a missing path is never reported not found for that.</exception>
    /// <exception cref="GitException">Git cannot be started, or fails listing the worktrees of the repo holding an existing
    /// folder.</exception>
    /// <exception cref="IOException">The path's attributes cannot be read for a reason other than its absence.</exception>
    /// <exception cref="UnauthorizedAccessException">The path's attributes cannot be read for lack of access.</exception>
    public static Resolution ResolveOne(string path) => ResolveOne(path, new VolumeStalls(), Discoverer.ListWorktrees);

    /// <summary>Resolves what <see cref="ResolveOne(string)"/> resolves, listing each repo's worktrees with <paramref name="listWorktrees"/>.</summary>
    /// <param name="path">The path given for removal; relative paths are made absolute against the current folder.</param>
    /// <param name="stalls">The volumes an earlier git call has stalled, shared by the whole call.</param>
    /// <param name="listWorktrees">Lists a repo's worktrees, as <see cref="Discoverer.ListWorktrees"/> does.</param>
    /// <returns>The resolved worktree, or the refusal.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or not a valid path.</exception>
    /// <exception cref="GitTimeoutException">A git read times out or its volume is stalled, so a repo that may register the path
    /// cannot be read; a missing path is never reported not found for that.</exception>
    /// <exception cref="GitException">Git cannot be started, or fails listing the worktrees of the repo holding an existing
    /// folder.</exception>
    /// <exception cref="IOException">The path's attributes cannot be read for a reason other than its absence.</exception>
    /// <exception cref="UnauthorizedAccessException">The path's attributes cannot be read for lack of access.</exception>
    internal static Resolution ResolveOne(string path, VolumeStalls stalls, Func<string, IReadOnlyList<WorktreeRecord>> listWorktrees)
    {
        ArgumentNullException.ThrowIfNull(path);
        string absolute = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(absolute);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return ResolveMissing(absolute, stalls, listWorktrees);
        }
        bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
        FileSystemInfo info = isDirectory ? new DirectoryInfo(absolute) : new FileInfo(absolute);
        if (Discoverer.IsLink(info))
        {
            return Refuse(RefusalReason.Link, null);
        }
        return isDirectory ? ResolveFolder(PathResolver.Resolve(absolute), stalls, listWorktrees) : Refuse(RefusalReason.NotAWorktree, null);
    }

    /// <summary>Resolves an existing folder that is not a link, already resolved to the name the system gives it.</summary>
    private static Resolution ResolveFolder(string target, VolumeStalls stalls, Func<string, IReadOnlyList<WorktreeRecord>> listWorktrees)
    {
        bool hasGitFile = File.Exists(Path.Join(target, ".git"));
        if (Scanner.CommonDirOrNull(target, stalls) is not { } common)
        {
            return Refuse(hasGitFile ? RefusalReason.Orphan : RefusalReason.NotAWorktree, null);
        }
        IReadOnlyList<WorktreeRecord> records = listWorktrees(common);
        if (records.Count == 0)
        {
            return Refuse(RefusalReason.NotAWorktree, null);
        }
        string mainWorktree = records[0].Path;
        string key = Discoverer.PathKey(target);
        List<string> recordKeys = RecordKeys(records);
        int index = recordKeys.IndexOf(key);
        if (index >= 0)
        {
            // Git calls it prunable (its .git file is gone) while the folder is still here: no live registration backs the folder,
            // so removing it is not a prune.
            return records[index].Prunable is not null ? Refuse(RefusalReason.Orphan, mainWorktree) : ResolveRecord(records, index, common, stalls);
        }
        bool underRecord = recordKeys.Exists(recordKey => key.StartsWith(recordKey + Path.DirectorySeparatorChar, StringComparison.Ordinal));
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
    /// <exception cref="GitTimeoutException">A repo's git read times out or its volume is stalled; every repo near the path sits on
    /// the same volume, so none of the rest could be read either.</exception>
    private static Resolution ResolveMissing(string absolute, VolumeStalls stalls, Func<string, IReadOnlyList<WorktreeRecord>> listWorktrees)
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
            if (Scanner.CommonDirOrNull(repo, stalls) is not { } common)
            {
                continue;
            }
            IReadOnlyList<WorktreeRecord> records;
            try
            {
                records = listWorktrees(common);
            }
            catch (GitException error)
            {
                if (error is GitTimeoutException)
                {
                    stalls.Mark(repo);
                    throw;
                }
                Trace.TraceWarning($"skipping repo {repo}: {error.Message}");
                continue;
            }
            int index = RecordKeys(records).IndexOf(key);
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
                    .EnumerateDirectories(dir, "*", Discoverer.AllEntries)
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

    private static bool IsPlainDir(string path) => Discoverer.IsPlainDir(Discoverer.AttributesOrSkip(path));

    /// <summary>The repo a container folder sits beside: <c>D:\x</c> for <c>D:\x.wt</c>, <c>D:\x-wt</c> or <c>D:\x.worktrees</c>.</summary>
    /// <returns>The repo's path; <see langword="null"/> when the folder's name has no container ending, or nothing before it.</returns>
    private static string? ContainerRepo(string dir)
    {
        string name = Path.GetFileName(dir);
        string? suffix = Discoverer.ContainerSuffixes.FirstOrDefault(ending => name.EndsWith(ending, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>Each record's comparison key, from its path resolved (its nearest existing ancestor when the folder is gone), in order.</summary>
    private static List<string> RecordKeys(IReadOnlyList<WorktreeRecord> records) =>
        [.. records.Select(record => Discoverer.PathKey(PathResolver.Resolve(record.Path)))];

    private static Resolution.Refusal Refuse(RefusalReason reason, string? repo) => new(reason, repo);
}
