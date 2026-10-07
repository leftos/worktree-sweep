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
/// <see cref="StringComparer.Ordinal"/>. Paths are compared resolved, by <see cref="PathKey"/>, which folds case: the root and each
/// registered worktree's parent folder are resolved (subst drive, 8.3 name, <c>\\?\</c> prefix), and a walked path is keyed below
/// the resolved root without being resolved itself. A walked path also matches a registered path spelled the same way, so a
/// resolution that fails never adds an orphan.</para>
/// <para>Diagnostics go to <see cref="Trace"/>: a skipped repo as a warning, a skipped entry as a plain line.</para>
/// </remarks>
public static class Discoverer
{
    private const char Separator = '\\';
    private const string GitdirPrefix = "gitdir:";

    /// <summary>Lists every entry, hidden and system ones included, and fails on a folder it cannot read rather than skipping it.</summary>
    internal static EnumerationOptions AllEntries { get; } = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    /// <summary>
    /// The name endings, compared ignoring case, of a container folder beside its repo, longest first where one ends another:
    /// <c>x.wt</c> sits beside <c>x</c>.
    /// </summary>
    internal static IReadOnlyList<string> ContainerSuffixes { get; } = [".wt", "-wt", ".worktrees", "-worktrees", "worktrees"];

    /// <summary>Finds the repos, registered worktrees, container dirs and orphans under <paramref name="root"/>.</summary>
    /// <remarks>
    /// A child that cannot be read (such as <c>System Volume Information</c>) is skipped with a trace line, and a repo whose
    /// worktrees git cannot list is skipped with a trace warning; a failed list, an unreadable worktrees folder and a worktree
    /// git's list leaves out are all reported in the result's errors. A repo on a volume <paramref name="stalls"/> already holds is
    /// skipped the same way, without git being started, and a listing that times out marks the repo's volume there, so no later repo
    /// on it pays the time limit. A walked folder matches a registered worktree when its path
    /// below
    /// <paramref name="root"/>, put below the resolved root, names the registered path with its parent resolved; so a substed,
    /// 8.3-spelled or <c>\\?\</c>-prefixed root matches the paths git prints, while a link inside a container never matches through
    /// its target, and a registered worktree folder that is itself a junction still matches. A walked folder spelled as git spells a
    /// registered path matches too.
    /// Every path in the result keeps the spelling of <paramref name="root"/> made absolute.
    /// </remarks>
    /// <param name="root">The folder to scan; made absolute.</param>
    /// <param name="stalls">The volumes an earlier git call has stalled, shared with the scan's signal reads.</param>
    /// <returns>What was found.</returns>
    /// <exception cref="IOException"><paramref name="root"/> itself cannot be listed; the message names it.</exception>
    public static DiscoveryResult Discover(string root, VolumeStalls stalls) => Discover(root, PathResolver.Resolve, stalls);

    /// <summary>Finds what <see cref="Discover(string, VolumeStalls)"/> finds, resolving paths with <paramref name="resolve"/>.</summary>
    /// <param name="root">The folder to scan; made absolute.</param>
    /// <param name="resolve">Resolves the root and each registered worktree's parent folder, as <see cref="PathResolver.Resolve"/> does.</param>
    /// <param name="stalls">The volumes an earlier git call has stalled, shared with the scan's signal reads.</param>
    /// <returns>What was found.</returns>
    /// <exception cref="IOException"><paramref name="root"/> itself cannot be listed; the message names it.</exception>
    internal static DiscoveryResult Discover(string root, Func<string, string> resolve, VolumeStalls stalls) =>
        Discover(root, resolve, stalls, ListWorktrees);

    /// <summary>Finds what <see cref="Discover(string, VolumeStalls)"/> finds, listing each repo's worktrees with <paramref name="listWorktrees"/>.</summary>
    /// <param name="root">The folder to scan; made absolute.</param>
    /// <param name="resolve">Resolves the root and each registered worktree's parent folder, as <see cref="PathResolver.Resolve"/> does.</param>
    /// <param name="stalls">The volumes an earlier git call has stalled, shared with the scan's signal reads.</param>
    /// <param name="listWorktrees">Lists a repo's worktrees: the git call whose timeout stalls the repo's volume.</param>
    /// <returns>What was found.</returns>
    /// <exception cref="IOException"><paramref name="root"/> itself cannot be listed; the message names it.</exception>
    internal static DiscoveryResult Discover(
        string root,
        Func<string, string> resolve,
        VolumeStalls stalls,
        Func<string, IReadOnlyList<WorktreeRecord>> listWorktrees
    )
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(stalls);
        ArgumentNullException.ThrowIfNull(listWorktrees);
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
        var errors = new List<DiscoveryError>();
        foreach (FileSystemInfo child in children)
        {
            if (IsPlainDir(child.Attributes))
            {
                Classify(child.FullName, repos, containers, errors, stalls, listWorktrees);
            }
        }

        var registered = new RegisteredPaths(full, repos.SelectMany(repo => repo.Worktrees), resolve);
        _ = containers.RemoveAll(registered.IsRegistered);

        var orphans = new List<Orphan>();
        foreach (string container in containers)
        {
            WalkContainer(container, container, registered, orphans);
        }
        return new DiscoveryResult(full, repos, containers, orphans, errors);
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
    /// <returns>
    /// The key; two paths naming the same folder the same way have equal keys under ordinal comparison. It resolves nothing, so
    /// discovery keys resolved paths: the root resolved, the registered paths with their parents resolved, and walked paths below
    /// the resolved root.
    /// </returns>
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

    private static void Classify(
        string child,
        List<Repo> repos,
        List<string> containers,
        List<DiscoveryError> errors,
        VolumeStalls stalls,
        Func<string, IReadOnlyList<WorktreeRecord>> listWorktrees
    )
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
            stalls.ThrowIfStalled(child);
            worktrees = listWorktrees(child);
        }
        catch (GitException error)
        {
            if (error is GitTimeoutException)
            {
                stalls.Mark(child);
            }
            Trace.TraceWarning($"skipping repo {child}: {error.Message}");
            errors.Add(new DiscoveryError(child, child, $"git worktree list failed: {error.Message}"));
            return;
        }
        errors.AddRange(LeftOutWorktrees(child, worktrees));
        string claudeDir = Path.Join(child, ".claude");
        string claude = Path.Join(claudeDir, "worktrees");
        if (IsPlainDir(AttributesOrSkip(claudeDir)) && IsPlainDir(AttributesOrSkip(claude)))
        {
            containers.Add(claude);
        }
        repos.Add(new Repo(child, worktrees));
    }

    /// <summary>
    /// The problems in the admin folders under the repo's <c>.git\worktrees</c>: one whose <c>gitdir</c> file is missing, unreadable
    /// or empty, or one that names a worktree git's list does not hold; either way git leaves that worktree out of its list, so its
    /// folder would otherwise look like an orphan.
    /// </summary>
    /// <param name="repo">The repo whose worktrees git listed.</param>
    /// <param name="listed">The records git returned, the main worktree first.</param>
    /// <returns>One error per problem, naming the <c>gitdir</c> file or the <c>worktrees</c> folder.</returns>
    internal static List<DiscoveryError> LeftOutWorktrees(string repo, IReadOnlyList<WorktreeRecord> listed)
    {
        var errors = new List<DiscoveryError>();
        string worktrees = Path.Join(repo, ".git", "worktrees");
        if (!IsPlainDir(AttributesOrSkip(worktrees)))
        {
            return errors;
        }
        List<FileSystemInfo> entries;
        try
        {
            entries = ReadChildren(worktrees);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"cannot read {worktrees}: {error.Message}");
            errors.Add(new DiscoveryError(repo, worktrees, $"cannot read {worktrees}: {error.Message}"));
            return errors;
        }
        var listedKeys = new HashSet<string>(listed.Skip(1).Select(record => PathKey(record.Path)), StringComparer.Ordinal);
        foreach (FileSystemInfo entry in entries)
        {
            if (IsLink(entry) || !entry.Attributes.HasFlag(FileAttributes.Directory))
            {
                continue;
            }
            string gitdir = Path.Join(entry.FullName, "gitdir");
            if (LeftOutMessage(entry.FullName, gitdir, listedKeys) is { } message)
            {
                errors.Add(new DiscoveryError(repo, gitdir, message));
            }
        }
        return errors;
    }

    /// <summary>Why git's list leaves out the worktree an admin folder describes, or <see langword="null"/> when it listed it.</summary>
    /// <param name="adminFolder">The admin folder, <c>{repo}\.git\worktrees\{id}</c>.</param>
    /// <param name="gitdir">Its <c>gitdir</c> file.</param>
    /// <param name="listedKeys">The path keys of the linked worktrees git's list holds.</param>
    /// <returns>The message for the result's errors, or <see langword="null"/> when the worktree is listed.</returns>
    private static string? LeftOutMessage(string adminFolder, string gitdir, HashSet<string> listedKeys)
    {
        string text;
        try
        {
            text = File.ReadAllText(gitdir).Trim();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return "git's worktree list leaves this worktree out: its gitdir file is missing; "
                + "if the worktree folder is gone, git worktree prune clears the record";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"git's worktree list leaves this worktree out: cannot read its gitdir file: {error.Message}";
        }
        if (text.Length == 0)
        {
            return "git's worktree list leaves this worktree out: its gitdir file is empty";
        }
        string worktree;
        try
        {
            worktree = WorktreeGitdirNames(adminFolder, text);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            return $"git's worktree list leaves this worktree out: cannot read its gitdir file: {error.Message}";
        }
        return listedKeys.Contains(PathKey(worktree)) ? null : $"git's worktree list leaves out the worktree its gitdir file names: {worktree}";
    }

    /// <summary>
    /// The worktree a <c>gitdir</c> file names: its content without the worktree's <c>.git</c> file, resolved against the admin
    /// folder when git wrote it relative, as it does with <c>worktree.useRelativePaths</c>.
    /// </summary>
    /// <param name="adminFolder">The admin folder a relative content is against.</param>
    /// <param name="text">The trimmed content of the <c>gitdir</c> file.</param>
    /// <returns>The worktree folder's path.</returns>
    private static string WorktreeGitdirNames(string adminFolder, string text)
    {
        string rest = text.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? text[..^".git".Length] : text;
        string trimmed = rest.TrimEnd(Separator, '/');
        return Path.IsPathFullyQualified(trimmed) ? trimmed : Path.GetFullPath(Path.Join(adminFolder, trimmed));
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

    private static void WalkContainer(string container, string dir, RegisteredPaths registered, List<Orphan> orphans)
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
            if (registered.IsRegistered(child.FullName))
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
            else if (registered.HoldsRegistered(child.FullName))
            {
                WalkContainer(container, child.FullName, registered, orphans);
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
        string name = Path.GetFileName(path);
        return ContainerSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
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
    internal static FileAttributes? AttributesOrSkip(string path)
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

    /// <summary>Whether attributes read by <see cref="AttributesOrSkip"/> are a folder's that is not a reparse point.</summary>
    internal static bool IsPlainDir(FileAttributes? attributes) =>
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

    /// <summary>
    /// The registered worktree paths and the keys a walk compares with them. Each registered path is held twice: as spelled, and with
    /// its parent folder resolved and its own name in long form, never resolved through, so a worktree folder that is itself a
    /// junction keeps the key the walk gives it. A walked path matches when either its own spelling or its part below the typed root,
    /// put below the resolved root, is held, so resolution can only remove an orphan, never add one. A walked path is never resolved
    /// itself, so a link inside the walk keeps its own key.
    /// </summary>
    private sealed class RegisteredPaths
    {
        private readonly string typedRoot;
        private readonly Func<string, string> resolve;
        private readonly string resolvedRoot;
        private readonly Dictionary<string, string> resolvedParents = new(StringComparer.Ordinal);
        private readonly HashSet<string> known = new(StringComparer.Ordinal);
        private readonly HashSet<string> ancestors;

        /// <summary>Initializes a new instance of the <see cref="RegisteredPaths"/> class, resolving the root and every record's parent.</summary>
        /// <param name="typedRoot">The root as the walk spells it.</param>
        /// <param name="records">Every registered worktree, main worktrees included.</param>
        /// <param name="resolve">Resolves the root and each record's parent folder.</param>
        public RegisteredPaths(string typedRoot, IEnumerable<WorktreeRecord> records, Func<string, string> resolve)
        {
            this.typedRoot = typedRoot;
            this.resolve = resolve;
            resolvedRoot = resolve(typedRoot);
            foreach (WorktreeRecord record in records)
            {
                _ = known.Add(PathKey(record.Path));
                _ = known.Add(ResolvedRecordKey(record.Path));
            }
            ancestors = AncestorKeys(known);
        }

        /// <summary>Whether a walked path is a registered worktree.</summary>
        /// <param name="walked">A path under the root, as the walk spells it.</param>
        /// <returns><see langword="true"/> when it is one.</returns>
        public bool IsRegistered(string walked) => known.Contains(PathKey(walked)) || known.Contains(ResolvedKey(walked));

        /// <summary>Whether a registered worktree lies below a walked path.</summary>
        /// <param name="walked">A path under the root, as the walk spells it.</param>
        /// <returns><see langword="true"/> when one does.</returns>
        public bool HoldsRegistered(string walked) => ancestors.Contains(PathKey(walked)) || ancestors.Contains(ResolvedKey(walked));

        /// <summary>A registered path's resolved key: its parent resolved, joined with its own name in long form.</summary>
        private string ResolvedRecordKey(string path)
        {
            string trimmed = Path.TrimEndingDirectorySeparator(path);
            string? parent = Path.GetDirectoryName(trimmed);
            if (parent is null)
            {
                return PathKey(resolve(trimmed));
            }
            string name = Path.GetFileName(PathResolver.LongPath(trimmed));
            return PathKey(Path.Join(ResolvedParent(parent), name));
        }

        /// <summary>A record's parent folder resolved, once per folder: worktrees sharing a container resolve it once.</summary>
        private string ResolvedParent(string parent)
        {
            string key = PathKey(parent);
            if (!resolvedParents.TryGetValue(key, out string? resolved))
            {
                resolved = resolve(parent);
                resolvedParents.Add(key, resolved);
            }
            return resolved;
        }

        /// <summary>A walked path's part below the typed root, put below the resolved root; its own key when it is not below the root.</summary>
        private string ResolvedKey(string walked)
        {
            if (!walked.StartsWith(typedRoot, StringComparison.Ordinal))
            {
                Trace.WriteLine($"walked path {walked} is not under the root {typedRoot}; comparing it as spelled");
                return PathKey(walked);
            }
            string below = walked[typedRoot.Length..].TrimStart(Separator);
            return PathKey(Path.Join(resolvedRoot, below));
        }
    }
}
