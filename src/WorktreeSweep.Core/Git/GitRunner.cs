using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace WorktreeSweep.Git;

/// <summary>
/// Runs <c>git</c> as a child process. Every call sets <c>GIT_OPTIONAL_LOCKS=0</c> and <c>core.fsmonitor=false</c>, so reading a
/// repo never rewrites its index or starts a file-system monitor daemon in it; sets <c>GIT_TERMINAL_PROMPT=0</c> and
/// <c>GCM_INTERACTIVE=never</c>, so git and Git Credential Manager fail instead of waiting for an answer nobody can type; removes
/// <see cref="RepoLocalEnvVars"/> from the child's environment; and kills the child's process tree once it has run for
/// <see cref="CallTimeout"/>.
/// </summary>
public static class GitRunner
{
    /// <summary>
    /// The repo-local environment variables, as <c>git rev-parse --local-env-vars</c> lists them (git 2.56). A git hook, or any
    /// process git starts, inherits them pointing at the calling repo; a child git that kept them would read or write that repo
    /// instead of the one it was pointed at with <c>-C</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> RepoLocalEnvVars =
    [
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_CONFIG",
        "GIT_CONFIG_PARAMETERS",
        "GIT_CONFIG_COUNT",
        "GIT_OBJECT_DIRECTORY",
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_IMPLICIT_WORK_TREE",
        "GIT_GRAFT_FILE",
        "GIT_INDEX_FILE",
        "GIT_NO_REPLACE_OBJECTS",
        "GIT_REPLACE_REF_BASE",
        "GIT_PREFIX",
        "GIT_SHALLOW_FILE",
        "GIT_COMMON_DIR",
    ];

    /// <summary>
    /// How long one git call may run before its process tree is killed: a stalled network share, a locked volume or a credential
    /// prompt would otherwise hang the scan.
    /// </summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Bounds each wait, after git exits or is killed, for its output pipes to close, which a process outside git's tree may still
    /// hold.
    /// </summary>
    private static readonly TimeSpan ReadGrace = TimeSpan.FromSeconds(15);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Runs <c>git -C {dir} {args}</c> and returns its trimmed standard output.</summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments, after <c>-C {dir}</c>.</param>
    /// <returns>Standard output, trimmed.</returns>
    /// <exception cref="GitException">Git cannot be started, does not exit within <see cref="CallTimeout"/>, leaves its output open,
    /// or exits with a non-zero code; the message names the command, and the exit code and git's standard error or the time limit.
    /// </exception>
    public static string Run(string dir, IReadOnlyList<string> args) => RunRaw(dir, args).Trim();

    /// <summary>
    /// Runs <c>git -C {dir} {args}</c> and returns its standard output untrimmed, for output where leading spaces or NUL
    /// separators carry meaning.
    /// </summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments, after <c>-C {dir}</c>.</param>
    /// <returns>Standard output, untrimmed.</returns>
    /// <exception cref="GitException">Git cannot be started, does not exit within <see cref="CallTimeout"/>, leaves its output open,
    /// or exits with a non-zero code; the message names the command, and the exit code and git's standard error or the time limit.
    /// </exception>
    public static string RunRaw(string dir, IReadOnlyList<string> args)
    {
        GitStatus status = RunStatus(dir, args);
        return status.Success ? status.Stdout : throw Failure(dir, args, status);
    }

    /// <summary>Runs <c>git -C {dir} {args}</c> and returns its exit code and output without treating a non-zero exit as an error.</summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments, after <c>-C {dir}</c>.</param>
    /// <returns>The exit code and both streams.</returns>
    /// <exception cref="GitException">Only when git cannot be started, does not exit within <see cref="CallTimeout"/>, or leaves its
    /// output open.</exception>
    public static GitStatus RunStatus(string dir, IReadOnlyList<string> args) =>
        RunStatusWithEnv(dir, args, ImmutableDictionary<string, string>.Empty);

    /// <summary>Like <see cref="RunStatus"/>, with extra environment variables set for the child after the repo-local ones are removed.</summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments, after <c>-C {dir}</c>.</param>
    /// <param name="env">Variables to set for the child.</param>
    /// <returns>The exit code and both streams.</returns>
    /// <exception cref="GitException">Only when git cannot be started, does not exit within <see cref="CallTimeout"/>, or leaves its
    /// output open.</exception>
    public static GitStatus RunStatusWithEnv(string dir, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env) =>
        RunStatusWithEnv(dir, args, env, CallTimeout, ReadGrace);

    /// <summary>Like the public overload, with the time limit and the grace given instead of <see cref="CallTimeout"/> and
    /// <see cref="ReadGrace"/>.</summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments, after <c>-C {dir}</c>.</param>
    /// <param name="env">Variables to set for the child.</param>
    /// <param name="timeout">How long git may run before its process tree is killed.</param>
    /// <param name="grace">How long each wait for git's output pipes to close may take, after it exits or is killed.</param>
    /// <returns>The exit code and both streams.</returns>
    /// <exception cref="GitException">Only when git cannot be started, does not exit within <paramref name="timeout"/>, or exits
    /// with its output still held open after <paramref name="grace"/>.</exception>
    internal static GitStatus RunStatusWithEnv(
        string dir,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env,
        TimeSpan timeout,
        TimeSpan grace
    )
    {
        ProcessStartInfo startInfo = StartInfo(dir, args, env);
        using Process process = Start(startInfo, dir, args);
        process.StandardInput.Close();
        Task<byte[]> stdout = ReadAllAsync(process.StandardOutput.BaseStream);
        Task<byte[]> stderr = ReadAllAsync(process.StandardError.BaseStream);
        Task reads = Observe(Task.WhenAll(stdout, stderr));
        if (!process.WaitForExit(timeout))
        {
            throw Killed(process, reads, Describe(dir, args), timeout, grace);
        }
        if (!reads.Wait(grace))
        {
            throw new GitException($"{Describe(dir, args)} exited but its output stayed open for {Seconds(grace)} s");
        }
        byte[] stdoutBytes = stdout.GetAwaiter().GetResult();
        byte[] stderrBytes = stderr.GetAwaiter().GetResult();
        return new GitStatus(process.ExitCode, Utf8.GetString(stdoutBytes), Utf8.GetString(stderrBytes).Trim());
    }

    /// <summary>Removes every <see cref="RepoLocalEnvVars"/> entry from a child's environment.</summary>
    /// <param name="startInfo">The child's start info.</param>
    public static void ClearRepoEnv(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        foreach (string key in RepoLocalEnvVars)
        {
            startInfo.Environment.Remove(key);
        }
    }

    /// <summary>The error for a git command that exited with a code the caller does not accept.</summary>
    /// <param name="dir">The directory git ran in.</param>
    /// <param name="args">The git arguments.</param>
    /// <param name="status">What git returned.</param>
    /// <returns>An exception naming the command, the exit code and git's standard error.</returns>
    public static GitException Failure(string dir, IReadOnlyList<string> args, GitStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        string code = status.Code.ToString(CultureInfo.InvariantCulture);
        return new GitException($"{Describe(dir, args)} exited with code {code}: {status.Stderr}");
    }

    private static ProcessStartInfo StartInfo(string dir, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(dir);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.fsmonitor=false");
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        ClearRepoEnv(startInfo);
        foreach (KeyValuePair<string, string> pair in env)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "never";
        return startInfo;
    }

    private static Process Start(ProcessStartInfo startInfo, string dir, IReadOnlyList<string> args)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new GitException($"failed to start {Describe(dir, args)}; is git on PATH?");
        }
        catch (Win32Exception error)
        {
            throw new GitException($"failed to start {Describe(dir, args)}; is git on PATH?", error);
        }
    }

    /// <summary>
    /// Kills a git child that outlived its limit, with its whole tree: a grandchild such as the shell running an alias holds the
    /// output pipes open, so killing git alone would leave the reads waiting. Its exit and the reads are each waited for up to
    /// <paramref name="grace"/>; the timeout is reported either way.
    /// </summary>
    private static GitException Killed(Process process, Task reads, string command, TimeSpan timeout, TimeSpan grace)
    {
        string limit = $"{command} did not exit within {Seconds(timeout)} s";
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or AggregateException)
        {
            return new GitException($"{limit}; killing it failed: {error.Message}", error);
        }
        _ = process.WaitForExit(grace);
        _ = reads.Wait(grace);
        return new GitException($"{limit} and was killed");
    }

    /// <summary>
    /// A task that ends when the reads do and observes any fault they end with, so waiting on it never throws and a fault after
    /// the caller has stopped waiting is not left unobserved.
    /// </summary>
    private static Task Observe(Task reads) =>
        reads.ContinueWith(
            static task =>
            {
                _ = task.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static string Describe(string dir, IReadOnlyList<string> args) => $"`git -C {dir} {string.Join(' ', args)}`";
}
