using System.Diagnostics;
using WorktreeSweep.Git;

namespace WorktreeSweep.Discovery;

/// <summary>
/// Finds the repos under a root, their registered worktrees, the container dirs and the orphan folders in them.
/// </summary>
/// <remarks>
/// <para>A <c>.git</c> directory marks a repo. Containers are <c>*.wt</c>, <c>*-wt</c>, <c>*worktrees</c> and a repo's
/// <c>.claude\worktrees</c> when neither folder on that path is a reparse point.</para>
/// <para>The walk never follows a symbolic link or junction: a link inside a container is reported as an orphan of its own kind and
/// never entered, walked or sized. A reparse point is detected by its attribute.</para>
/// <para>Children are listed with <c>AttributesToSkip = 0</c>, so hidden and system entries are seen, and sorted with
/// <see cref="StringComparer.Ordinal"/>. Paths are compared by <see cref="PathKey"/>, which folds case.</para>
/// <para>Diagnostics go to <see cref="Trace"/>: a skipped repo as a warning, a skipped entry as a plain line.</para>
/// </remarks>
public static class Discoverer
{
    private const char Separator = '\\';
    private const string GitdirPrefix = "gitdir:";

    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    /// <summary>Finds the repos, registered worktrees, container dirs and orphans under <paramref name="root"/>.</summary>
    /// <remarks>
    /// A child that cannot be read (such as <c>System Volume Information</c>) is skipped with a trace line, and a repo whose
    /// worktrees git cannot list is skipped with a trace warning.
    /// </remarks>
    /// <param name="root">The folder to scan; made absolute.</param>
    /// <returns>What was found.</returns>
    /// <exception cref="IOException"><paramref name="root"/> itself cannot be listed; the message names it.</exception>
    public static DiscoveryResult Discover(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        string full = Path.GetFullPath(root);
        List<FileSystemInfo> children;
        try
        {
            children = ReadChildren(full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"cannot list the root folder {full}: {error.Message}", error);
        }

        var repos = new List<Repo>();
        var containers = new List<string>();
        foreach (FileSystemInfo child in children)
        {
            if (IsPlainDir(child.Attributes))
            {
                Classify(child.FullName, repos, containers);
            }
        }

        var known = new HashSet<string>(repos.SelectMany(repo => repo.Worktrees).Select(record => PathKey(record.Path)), StringComparer.Ordinal);
        _ = containers.RemoveAll(container => known.Contains(PathKey(container)));

        HashSet<string> ancestors = AncestorKeys(known);
        var orphans = new List<Orphan>();
        foreach (string container in containers)
        {
            WalkContainer(container, container, known, ancestors, orphans);
        }
        return new DiscoveryResult(full, repos, containers, orphans);
    }

    /// <summary>Parses <c>git worktree list --porcelain</c> output into records, in git's order (the main worktree first).</summary>
    /// <param name="text">The porcelain output.</param>
    /// <returns>The records; a line outside a record or with an unknown key is skipped with a trace line.</returns>
    public static IReadOnlyList<WorktreeRecord> ParseWorktreePorcelain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var records = new List<WorktreeRecord>();
        WorktreeRecord? current = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            string key = space < 0 ? line : line[..space];
            string? value = space < 0 ? null : line[(space + 1)..];
            if (key == "worktree")
            {
                Flush();
                current = new WorktreeRecord { Path = FromGitPath(value ?? "") };
            }
            else if (current is null)
            {
                Trace.WriteLine($"worktree porcelain line outside a record: {line}");
            }
            else
            {
                current = ApplyPorcelainLine(current, key, value, line);
            }
        }
        Flush();
        return records;

        void Flush()
        {
            if (current is not null)
            {
                records.Add(current);
                current = null;
            }
        }
    }

    /// <summary>A comparison key for a path: separators unified and case folded; no trailing separator except a drive root's.</summary>
    /// <param name="path">The path; <c>/</c> and <c>\</c> both separate.</param>
    /// <returns>The key; two paths naming the same folder the same way have equal keys under ordinal comparison.</returns>
    public static string PathKey(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string key = path.Replace('/', Separator).ToLowerInvariant();
        while (key.Length > 1 && key.EndsWith(Separator) && !key.EndsWith(":\\", StringComparison.Ordinal))
        {
            key = key[..^1];
        }
        return key;
    }

    /// <summary>Converts a path git printed (forward slashes on Windows too) to one with <c>\</c> separators.</summary>
    /// <param name="text">The path as git printed it.</param>
    /// <returns>The path with <c>\</c> separators.</returns>
    public static string FromGitPath(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace('/', Separator);
    }

    /// <summary>
    /// The git dir a worktree's <c>.git</c> file points to (<c>gitdir: &lt;path&gt;</c>), resolved against the worktree when
    /// relative.
    /// </summary>
    /// <param name="worktree">The worktree folder.</param>
    /// <returns>The git dir; <see langword="null"/> when <c>.git</c> is missing, is a directory, or holds no <c>gitdir:</c> line.</returns>
    public static string? ReadGitdirFile(string worktree)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        string dotGit = Path.Join(worktree, ".git");
        string text;
        try
        {
            text = File.ReadAllText(dotGit);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"cannot read {dotGit} as a gitdir file: {error.Message}");
            return null;
        }
        string? line = text.Split('\n').FirstOrDefault(candidate => candidate.StartsWith(GitdirPrefix, StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }
        string target = FromGitPath(line[GitdirPrefix.Length..].Trim());
        return Path.IsPathFullyQualified(target) ? target : Path.Join(worktree, target);
    }

    /// <summary>
    /// Whether an entry (its attributes read without following links) is a junction, a symbolic link, or a folder that is a reparse
    /// point of another kind; none of these is entered by a walk.
    /// </summary>
    /// <param name="info">The entry, as a directory enumeration returned it.</param>
    /// <returns><see langword="true"/> for a reparse-point folder or a link to a file.</returns>
    public static bool IsLink(FileSystemInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        FileAttributes attributes = info.Attributes;
        return attributes.HasFlag(FileAttributes.ReparsePoint) && (attributes.HasFlag(FileAttributes.Directory) || IsSymlink(info));
    }

    /// <summary>Removes the <c>\\?\</c> or <c>\??\</c> prefix Windows puts on some link targets; a <c>UNC\</c> one is kept.</summary>
    /// <param name="path">A link target.</param>
    /// <returns>The path without the prefix.</returns>
    public static string StripVerbatim(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string? rest =
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
            : path.StartsWith(@"\??\", StringComparison.Ordinal) ? path[4..]
            : null;
        return rest is not null && !rest.StartsWith(@"UNC\", StringComparison.Ordinal) ? rest : path;
    }

    /// <summary>Lists the worktrees git registers for the repo at <paramref name="repo"/> (any folder git resolves to it).</summary>
    /// <param name="repo">A folder inside the repo.</param>
    /// <returns>The records, main worktree first.</returns>
    /// <exception cref="GitException">Git fails.</exception>
    public static IReadOnlyList<WorktreeRecord> ListWorktrees(string repo) =>
        ParseWorktreePorcelain(GitRunner.Run(repo, ["worktree", "list", "--porcelain"]));

    private static WorktreeRecord ApplyPorcelainLine(WorktreeRecord record, string key, string? value, string line) =>
        key switch
        {
            "HEAD" => record with { Head = value },
            "branch" => record with { Branch = value is null ? null : StripBranchPrefix(value) },
            "detached" => record with { Detached = true },
            "bare" => record with { Bare = true },
            "locked" => record with { Locked = value ?? "" },
            "prunable" => record with { Prunable = value ?? "" },
            _ => UnknownPorcelainKey(record, line),
        };

    private static string StripBranchPrefix(string value) =>
        value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value["refs/heads/".Length..] : value;

    private static WorktreeRecord UnknownPorcelainKey(WorktreeRecord record, string line)
    {
        Trace.WriteLine($"unknown worktree porcelain key: {line}");
        return record;
    }

    private static void Classify(string child, List<Repo> repos, List<string> containers)
    {
        if (!IsPlainDir(AttributesOrSkip(Path.Join(child, ".git"))))
        {
            if (IsContainerName(child))
            {
                containers.Add(child);
            }
            return;
        }
        IReadOnlyList<WorktreeRecord> worktrees;
        try
        {
            worktrees = ListWorktrees(child);
        }
        catch (GitException error)
        {
            Trace.TraceWarning($"skipping repo {child}: {error.Message}");
            return;
        }
        string claudeDir = Path.Join(child, ".claude");
        string claude = Path.Join(claudeDir, "worktrees");
        if (IsPlainDir(AttributesOrSkip(claudeDir)) && IsPlainDir(AttributesOrSkip(claude)))
        {
            containers.Add(claude);
        }
        repos.Add(new Repo(child, worktrees));
    }

    /// <summary>Every proper ancestor key of each known key, so one lookup tells whether a registered worktree lies below a folder.</summary>
    private static HashSet<string> AncestorKeys(HashSet<string> known)
    {
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in known)
        {
            for (int index = key.IndexOf(Separator, StringComparison.Ordinal); index >= 0; index = key.IndexOf(Separator, index + 1))
            {
                _ = ancestors.Add(key[..index]);
            }
        }
        return ancestors;
    }

    private static void WalkContainer(string container, string dir, HashSet<string> known, HashSet<string> ancestors, List<Orphan> orphans)
    {
        List<FileSystemInfo> children;
        try
        {
            children = ReadChildren(dir);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"skipping unreadable folder {dir}: {error.Message}");
            return;
        }
        foreach (FileSystemInfo child in children)
        {
            string key = PathKey(child.FullName);
            if (known.Contains(key))
            {
                continue;
            }
            if (IsLink(child))
            {
                orphans.Add(LinkOrphan(container, child));
            }
            else if (!child.Attributes.HasFlag(FileAttributes.Directory))
            {
                Trace.WriteLine($"skipping file {child.FullName} in a container");
            }
            else if (ancestors.Contains(key))
            {
                WalkContainer(container, child.FullName, known, ancestors, orphans);
            }
            else
            {
                orphans.Add(FolderOrphan(container, child.FullName));
            }
        }
    }

    private static Orphan LinkOrphan(string container, FileSystemInfo link)
    {
        string? target = ReadLinkTarget(link, out bool unreadable);
        if (target is null && !unreadable)
        {
            Trace.WriteLine($"cannot read the target of link {link.FullName}");
        }
        return new Orphan
        {
            Path = link.FullName,
            Container = container,
            Kind = OrphanKind.Link,
            LinkTarget = target is null ? null : StripVerbatim(target),
        };
    }

    private static Orphan FolderOrphan(string container, string path)
    {
        string dotGit = Path.Join(path, ".git");
        FileAttributes? attributes = AttributesOrSkip(dotGit);
        bool gitFile = attributes is { } found && !found.HasFlag(FileAttributes.Directory) && !IsSymlink(new FileInfo(dotGit));
        string? gitdir = gitFile ? ReadGitdirFile(path) : null;
        (bool Stale, string? Live) state = GitdirState(gitdir);
        return new Orphan
        {
            Path = path,
            Container = container,
            Kind = OrphanKind.Folder,
            StaleGitdir = state.Stale,
            LiveGitdir = state.Live,
            HasGitDir = IsPlainDir(attributes),
        };
    }

    private static (bool Stale, string? Live) GitdirState(string? gitdir)
    {
        if (gitdir is null)
        {
            return (false, null);
        }
        try
        {
            FileAttributes attributes = File.GetAttributes(gitdir);
            bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
            if (
                attributes.HasFlag(FileAttributes.ReparsePoint)
                && EntryAt(gitdir, isDirectory).ResolveLinkTarget(returnFinalTarget: true) is { } target
            )
            {
                _ = File.GetAttributes(target.FullName);
            }
            return (false, gitdir);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return (true, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.WriteLine($"cannot check gitdir {gitdir}: {error.Message}; treating it as still registered");
            return (false, gitdir);
        }
    }

    private static bool IsContainerName(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        bool wtSuffix = name.EndsWith(".wt", StringComparison.Ordinal) || name.EndsWith("-wt", StringComparison.Ordinal);
        return wtSuffix || name.EndsWith("worktrees", StringComparison.Ordinal);
    }

    /// <summary>A folder's entries, sorted by path; throws when the folder cannot be listed.</summary>
    private static List<FileSystemInfo> ReadChildren(string dir)
    {
        List<FileSystemInfo> children = [.. new DirectoryInfo(dir).EnumerateFileSystemInfos("*", AllEntries)];
        children.Sort((a, b) => StringComparer.Ordinal.Compare(a.FullName, b.FullName));
        return children;
    }

    /// <summary>
    /// A path's attributes without following links; <see langword="null"/> when it is missing, or (with a trace line) unreadable.
    /// </summary>
    private static FileAttributes? AttributesOrSkip(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"skipping unreadable {path}: {error.Message}");
            return null;
        }
    }

    private static bool IsPlainDir(FileAttributes? attributes) =>
        attributes is { } found && found.HasFlag(FileAttributes.Directory) && !found.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Whether a reparse point is a symbolic link or a junction, the kinds <see cref="FileSystemInfo.LinkTarget"/> resolves.</summary>
    /// <remarks>A reparse point whose data cannot be read counts as a link, so it is never entered.</remarks>
    private static bool IsSymlink(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) && (ReadLinkTarget(info, out bool unreadable) is not null || unreadable);

    /// <summary>
    /// A link's immediate target; <see langword="null"/> when the entry is not a symbolic link or junction, or when its reparse data
    /// cannot be read, which sets <paramref name="unreadable"/> and writes a trace line naming the path and the error.
    /// </summary>
    private static string? ReadLinkTarget(FileSystemInfo info, out bool unreadable)
    {
        unreadable = false;
        try
        {
            return info.LinkTarget;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"cannot read the reparse point of {info.FullName}: {error.Message}; treating it as a link with an unknown target");
            unreadable = true;
            return null;
        }
    }

    private static FileSystemInfo EntryAt(string path, bool isDirectory) => isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
}
