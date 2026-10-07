using System.ComponentModel;
using System.Diagnostics;
using WorktreeSweep.Discovery;
using WorktreeSweep.Git;
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
    {
        _ = EmptyGitConfig.Value;
        Root = Directory.CreateTempSubdirectory("worktree-sweep-").FullName;
    }

    /// <summary>Gets the temporary root folder.</summary>
    public string Root { get; }

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
        DiscoveryResult found = Discoverer.Discover(Root);
        (Repo Repo, WorktreeRecord Record) pair = Assert.Single(found.Registered, candidate => SamePath(candidate.Record.Path, path));
        DefaultBranches defaults = SignalReader.ReadDefaultBranches(pair.Repo.Path);
        return (pair.Record, SignalReader.ReadWorktreeSignals(defaults, pair.Record));
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
