using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using WorktreeSweep.Discovery;
using WorktreeSweep.Git;
using WorktreeSweep.Recycle;
using WorktreeSweep.Report;
using WorktreeSweep.Scan;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>
/// A temporary root folder holding throwaway repos built with real <c>git</c>, isolated from the user's and the system's git
/// config. Disposing it deletes the folder, removing junctions as links without walking through them.
/// </summary>
public sealed class Fixture : IDisposable
{
    private static readonly Lazy<string> EmptyGitConfig = new(IsolateGitConfig);

    /// <summary>Initializes a new instance of the <see cref="Fixture"/> class with a new temporary root folder.</summary>
    public Fixture()
        : this(Directory.CreateTempSubdirectory("worktree-sweep-").FullName) { }

    private Fixture(string root)
    {
        _ = EmptyGitConfig.Value;
        Root = root;
    }

    /// <summary>Gets the temporary root folder.</summary>
    public string Root { get; }

    /// <summary>
    /// A fixture whose root is a new folder under the repo's gitignored <c>.tmp</c> folder, on <c>X:</c>, the repo's volume, not
    /// <c>C:</c>: on <c>C:</c>, where <c>%TEMP%</c> is, a scanner holds freshly written files for seconds, which looks like a lock to
    /// a delete or a recycle.
    /// </summary>
    /// <returns>The fixture.</returns>
    public static Fixture InRepoTmp() => new(Directory.CreateDirectory(Path.Join(RepoTmp(), $"worktree-sweep-{Guid.NewGuid():N}")).FullName);

    /// <summary>Skips the calling test when the volume of <paramref name="path"/> cannot be shown to recycle, naming the volume and why.</summary>
    /// <param name="path">A path on the volume the test is about to hand to the Shell.</param>
    public static void SkipUnlessBinRecycles(string path)
    {
        string volume = Path.GetPathRoot(path) ?? path;
        string? reason = null;
        try
        {
            BinCapacity? capacity = BinCapacityReader.Read(path);
            if (capacity is null)
            {
                reason = $"{volume} has no readable Recycle Bin settings, so a recycle there may delete instead";
            }
            else if (capacity.NukeOnDelete)
            {
                reason = $"{volume}'s Recycle Bin deletes immediately (NukeOnDelete), so a recycle there is permanent";
            }
        }
        catch (IOException error)
        {
            reason = $"no Recycle Bin settings could be read for {volume}: {error.Message}";
        }
        if (reason is not null)
        {
            Assert.Skip(reason);
        }
    }

    /// <summary>A path under the root.</summary>
    /// <param name="relative">The path relative to the root; <c>/</c> and <c>\</c> both separate.</param>
    /// <returns>The full path.</returns>
    public string PathTo(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    /// <summary>Creates <c>{root}/{name}</c> with <c>git init -b main</c> and one commit adding <c>README.md</c>.</summary>
    /// <param name="name">The repo's folder name under the root.</param>
    /// <returns>The repo's path.</returns>
    public string Repo(string name)
    {
        string repo = PathTo(name);
        Directory.CreateDirectory(repo);
        Git(repo, ["init", "-b", "main"]);
        CommitFile(repo, "README.md", "readme\n", "initial");
        return repo;
    }

    /// <summary>
    /// Runs git in <paramref name="dir"/> through <see cref="GitRunner"/>, so without the repo-local variables a calling git hook
    /// exports, with a fixed identity and no signing.
    /// </summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments.</param>
    /// <returns>Standard output, trimmed.</returns>
    public static string Git(string dir, IReadOnlyList<string> args) =>
        GitRunner.Run(dir, ["-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", .. args]);

    /// <summary>Writes <paramref name="file"/> in <paramref name="dir"/>, stages it and commits it.</summary>
    /// <param name="dir">The repo or worktree.</param>
    /// <param name="file">The file's path relative to <paramref name="dir"/>.</param>
    /// <param name="content">The file's content.</param>
    /// <param name="message">The commit message.</param>
    /// <returns>The new commit id.</returns>
    public static string CommitFile(string dir, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(dir, file), content);
        Git(dir, ["add", file]);
        Git(dir, ["commit", "-q", "-m", message]);
        return Git(dir, ["rev-parse", "HEAD"]);
    }

    /// <summary>Adds a worktree at <paramref name="path"/> on a new branch <paramref name="branch"/> from the repo's HEAD.</summary>
    /// <param name="repo">The repo.</param>
    /// <param name="path">Where the worktree goes.</param>
    /// <param name="branch">The new branch's name.</param>
    public static void AddWorktree(string repo, string path, string branch) => Git(repo, ["worktree", "add", "-q", "-b", branch, path]);

    /// <summary>
    /// Creates a bare repo at <c>{root}/origin.git</c> on <c>main</c> and adds it to <paramref name="repo"/> as its <c>origin</c>.
    /// </summary>
    /// <param name="repo">The repo that gets the remote.</param>
    /// <returns>The bare repo's path.</returns>
    public string Origin(string repo)
    {
        string origin = PathTo("origin.git");
        Git(Root, ["init", "-q", "--bare", "-b", "main", origin]);
        Git(repo, ["remote", "add", "origin", origin]);
        return origin;
    }

    /// <summary>
    /// Discovers the root and reads the signals of the registered worktree at <paramref name="path"/> against its repo's default
    /// branches, as a scan does.
    /// </summary>
    /// <param name="path">The registered worktree.</param>
    /// <returns>Its record and its signals.</returns>
    public (WorktreeRecord Record, WorktreeSignals Signals) Registered(string path)
    {
        var stalls = new VolumeStalls();
        DiscoveryResult found = Discoverer.Discover(Root, stalls);
        (Repo Repo, WorktreeRecord Record) pair = RegisteredOnly(found, path);
        DefaultBranches defaults = SignalReader.ReadDefaultBranches(pair.Repo.Path, stalls);
        return (pair.Record, SignalReader.ReadWorktreeSignals(defaults, pair.Record, stalls));
    }

    /// <summary>
    /// The one registered worktree at <paramref name="path"/>, or a failure naming every repo with its worktree paths, every
    /// discovery error and every orphan with its live git dir.
    /// </summary>
    /// <param name="found">What discovery found.</param>
    /// <param name="path">The registered worktree wanted.</param>
    /// <returns>The repo and record.</returns>
    private static (Repo Repo, WorktreeRecord Record) RegisteredOnly(DiscoveryResult found, string path)
    {
        List<(Repo Repo, WorktreeRecord Record)> matches = [.. found.Registered.Where(pair => SamePath(pair.Record.Path, path))];
        if (matches.Count != 1)
        {
            Assert.Fail(DescribeDiscovery(found, path, matches.Count));
        }
        return matches[0];
    }

    /// <summary>What discovery found, as a failure message: the wanted path, then every repo, discovery error and orphan.</summary>
    /// <param name="found">What discovery found.</param>
    /// <param name="path">The registered worktree wanted.</param>
    /// <param name="matches">How many registered worktrees matched it.</param>
    /// <returns>The message.</returns>
    private static string DescribeDiscovery(DiscoveryResult found, string path, int matches)
    {
        var message = new StringBuilder();
        message.AppendLine(CultureInfo.InvariantCulture, $"wanted exactly one registered worktree at {path}, found {matches}");
        foreach (Repo repo in found.Repos)
        {
            message.AppendLine(CultureInfo.InvariantCulture, $"repo {repo.Path}:");
            foreach (WorktreeRecord record in repo.Worktrees)
            {
                message.AppendLine(CultureInfo.InvariantCulture, $"  worktree {record.Path}");
            }
        }
        foreach (DiscoveryError error in found.Errors)
        {
            message.AppendLine(CultureInfo.InvariantCulture, $"discovery error {error.Path}: {error.Message}");
        }
        foreach (Orphan orphan in found.Orphans)
        {
            message.AppendLine(CultureInfo.InvariantCulture, $"orphan {orphan.Path}, live gitdir {orphan.LiveGitdir ?? "none"}");
        }
        return message.ToString();
    }

    /// <summary>Scans the root.</summary>
    /// <returns>The scan report.</returns>
    public ScanReport Scan() => Scanner.Scan(Root);

    /// <summary>Makes a directory junction with <c>mklink /J</c>.</summary>
    /// <param name="link">The junction to create.</param>
    /// <param name="target">The folder it points at.</param>
    /// <returns><see langword="true"/> when made; <see langword="false"/>, with a message on standard error, when <c>mklink /J</c> is
    /// unavailable or fails.</returns>
    public static bool MakeJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string[] args = ["/c", "mklink", "/J", link, target];
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("cmd did not start");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            string stderr = process.StandardError.ReadToEnd();
            _ = stdout.GetAwaiter().GetResult();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return true;
            }
            Console.Error.WriteLine($"skipping: mklink /J failed: {stderr.Trim()}");
            return false;
        }
        catch (Win32Exception error)
        {
            Console.Error.WriteLine($"skipping: cannot run cmd /c mklink /J: {error.Message}");
            return false;
        }
    }

    /// <summary>
    /// Whether two paths name the same folder: 8.3 short names expanded, separators unified, case folded, trailing separators
    /// dropped.
    /// </summary>
    /// <param name="a">One path.</param>
    /// <param name="b">The other path.</param>
    /// <returns><see langword="true"/> when their keys are equal.</returns>
    public static bool SamePath(string a, string b) =>
        string.Equals(Discoverer.PathKey(NativeMethods.LongPath(a)), Discoverer.PathKey(NativeMethods.LongPath(b)), StringComparison.Ordinal);

    /// <summary>
    /// Deletes the root folder. A failure is reported as a diagnostic message naming the folder, and the folder is left, so it never
    /// replaces the test's own result.
    /// </summary>
    public void Dispose()
    {
        try
        {
            DeleteTree(new DirectoryInfo(Root));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TestContext.Current.SendDiagnosticMessage($"cannot delete fixture folder {Root}, left in place: {error.Message}");
        }
    }

    /// <summary>
    /// Makes every git process this test assembly starts, the code under test's included, ignore the system and global config:
    /// sets <c>GIT_CONFIG_NOSYSTEM</c> and points <c>GIT_CONFIG_GLOBAL</c> at an empty file. <see cref="Lazy{T}"/> runs it once.
    /// </summary>
    private static string IsolateGitConfig()
    {
        string empty = Path.Combine(AppContext.BaseDirectory, "empty-gitconfig");
        File.WriteAllText(empty, "");
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", empty);
        return empty;
    }

    /// <summary>The repo's gitignored <c>.tmp</c> folder, found above the test assembly's folder, created when missing.</summary>
    private static string RepoTmp()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Join(dir.FullName, "WorktreeSweep.slnx")))
            {
                return Directory.CreateDirectory(Path.Join(dir.FullName, ".tmp")).FullName;
            }
        }
        throw new InvalidOperationException($"no WorktreeSweep.slnx above {AppContext.BaseDirectory}");
    }

    private static void DeleteTree(DirectoryInfo dir)
    {
        foreach (FileSystemInfo entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                entry.Delete();
            }
            else if (entry is DirectoryInfo child)
            {
                DeleteTree(child);
            }
            else
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }
        dir.Attributes &= ~FileAttributes.ReadOnly;
        dir.Delete();
    }
}
