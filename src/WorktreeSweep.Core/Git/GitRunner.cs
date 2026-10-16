using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Win32.SafeHandles;

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

    /// <summary>Bounds each wait, after a cancelled git is killed, for it to exit and for its output pipes to close.</summary>
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(2);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Runs <c>git -C {dir} {args}</c> and returns its trimmed standard output, killing git when the token is cancelled.</summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments, after <c>-C {dir}</c>.</param>
    /// <param name="token">Cancels the call: git is not started, or its process tree is killed.</param>
    /// <returns>Standard output, trimmed.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> is cancelled.</exception>
    /// <exception cref="GitTimeoutException">Git does not exit within <see cref="CallTimeout"/>; the message names the command and the
    /// time limit.</exception>
    /// <exception cref="GitException">Git cannot be started, leaves its output open, or exits with a non-zero code; the message names
    /// the command, and the exit code and git's standard error.</exception>
    public static string Run(string dir, IReadOnlyList<string> args, CancellationToken token) => RunRaw(dir, args, token).Trim();

    /// <summary>
    /// Runs <c>git -C {dir} {args}</c> and returns its standard output untrimmed, for output where leading spaces or NUL
    /// separators carry meaning.
    /// </summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments, after <c>-C {dir}</c>.</param>
    /// <param name="token">Cancels the call: git is not started, or its process tree is killed.</param>
    /// <returns>Standard output, untrimmed.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> is cancelled.</exception>
    /// <exception cref="GitTimeoutException">Git does not exit within <see cref="CallTimeout"/>; the message names the command and the
    /// time limit.</exception>
    /// <exception cref="GitException">Git cannot be started, leaves its output open, or exits with a non-zero code; the message names
    /// the command, and the exit code and git's standard error.</exception>
    public static string RunRaw(string dir, IReadOnlyList<string> args, CancellationToken token)
    {
        GitStatus status = RunStatus(dir, args, token);
        return status.Success ? status.Stdout : throw Failure(dir, args, status);
    }

    /// <summary>Runs <c>git -C {dir} {args}</c> and returns its exit code and output without treating a non-zero exit as an error.</summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments, after <c>-C {dir}</c>.</param>
    /// <param name="token">Cancels the call: git is not started, or its process tree is killed.</param>
    /// <returns>The exit code and both streams.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> is cancelled.</exception>
    /// <exception cref="GitTimeoutException">Only when git does not exit within <see cref="CallTimeout"/>.</exception>
    /// <exception cref="GitException">Only when git cannot be started or leaves its output open.</exception>
    public static GitStatus RunStatus(string dir, IReadOnlyList<string> args, CancellationToken token) =>
        RunStatusWithEnv(dir, args, ImmutableDictionary<string, string>.Empty, token);

    /// <summary>Like <see cref="RunStatus"/>, with extra environment variables set for the child after the repo-local ones are removed.</summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments, after <c>-C {dir}</c>.</param>
    /// <param name="env">Variables to set for the child.</param>
    /// <param name="token">Cancels the call: git is not started, or its process tree is killed.</param>
    /// <returns>The exit code and both streams.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> is cancelled.</exception>
    /// <exception cref="GitTimeoutException">Only when git does not exit within <see cref="CallTimeout"/>.</exception>
    /// <exception cref="GitException">Only when git cannot be started or leaves its output open.</exception>
    public static GitStatus RunStatusWithEnv(
        string dir,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env,
        CancellationToken token
    ) => RunStatusWithEnv(dir, args, env, CallTimeout, ReadGrace, token);

    /// <summary>Like the public overload, with the time limit and the grace given instead of <see cref="CallTimeout"/> and
    /// <see cref="ReadGrace"/>.</summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments, after <c>-C {dir}</c>.</param>
    /// <param name="env">Variables to set for the child.</param>
    /// <param name="timeout">How long git may run before its process tree is killed.</param>
    /// <param name="grace">How long each wait for git's output pipes to close may take, after it exits or is killed.</param>
    /// <param name="token">Cancels the call: git is not started, or its process tree is killed as on a timeout.</param>
    /// <returns>The exit code and both streams.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> is cancelled; the message names the command.</exception>
    /// <exception cref="GitTimeoutException">Only when git does not exit within <paramref name="timeout"/>.</exception>
    /// <exception cref="GitException">Only when git cannot be started, or exits with its output still held open after
    /// <paramref name="grace"/>.</exception>
    internal static GitStatus RunStatusWithEnv(
        string dir,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env,
        TimeSpan timeout,
        TimeSpan grace,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        ProcessStartInfo startInfo = StartInfo(dir, args, env);
        using Process process = Start(startInfo, dir, args);
        process.StandardInput.Close();
        Task<byte[]> stdout = ReadAllAsync(process.StandardOutput.BaseStream);
        Task<byte[]> stderr = ReadAllAsync(process.StandardError.BaseStream);
        var call = new Call(process, Observe(Task.WhenAll(stdout, stderr)), Describe(dir, args), new Lazy<Exception?>(() => KillTree(process)));
        // Declared after the process, so disposed before it on every exit path; disposing waits for a running kill, so no kill
        // can reach the process after it is disposed.
        using CancellationTokenRegistration registration = token.Register(() => _ = call.Kill.Value);
        bool exited = process.WaitForExit(timeout);
        if (token.IsCancellationRequested)
        {
            throw Cancelled(call, token);
        }
        if (!exited)
        {
            throw TimedOut(call, timeout, grace, token);
        }
        if (!WaitForReads(call.Reads, grace, token))
        {
            if (token.IsCancellationRequested)
            {
                throw Cancelled(call, token);
            }
            throw new GitException($"{call.Command} exited but its output stayed open for {Seconds(grace)} s");
        }
        byte[] stdoutBytes = stdout.GetAwaiter().GetResult();
        byte[] stderrBytes = stderr.GetAwaiter().GetResult();
        return new GitStatus(process.ExitCode, Utf8.GetString(stdoutBytes), Utf8.GetString(stderrBytes).Trim());
    }

    /// <summary>The common git dir of the repo <paramref name="dir"/> lies in, as a full path with <c>\</c> separators.</summary>
    /// <param name="dir">A folder inside the repo or one of its worktrees.</param>
    /// <returns>The common git dir; for a bare repo, the repo itself.</returns>
    /// <exception cref="GitTimeoutException">Git does not exit within <see cref="CallTimeout"/>; the message names the command and the
    /// time limit.</exception>
    /// <exception cref="GitException">
    /// Git cannot be started, fails, or prints a path that is not fully qualified, as a git without <c>--path-format</c> does.
    /// </exception>
    /// <param name="token">Cancels the call: git is not started, or its process tree is killed.</param>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> is cancelled.</exception>
    public static string CommonDir(string dir, CancellationToken token)
    {
        GitStatus status = RunStatus(dir, CommonDirArgs, token);
        return status.Success ? FullCommonDir(dir, status.Stdout) : throw Failure(dir, CommonDirArgs, status);
    }

    /// <summary>The git arguments that print the common git dir as a full path.</summary>
    internal static IReadOnlyList<string> CommonDirArgs { get; } = ["rev-parse", "--path-format=absolute", "--git-common-dir"];

    /// <summary>The common git dir <see cref="CommonDirArgs"/> printed, with <c>\</c> separators.</summary>
    /// <param name="dir">The directory git ran in.</param>
    /// <param name="stdout">Git's standard output.</param>
    /// <returns>The common git dir.</returns>
    /// <exception cref="GitException">The path is not fully qualified, as a git without <c>--path-format</c> prints it.</exception>
    internal static string FullCommonDir(string dir, string stdout)
    {
        string path = stdout.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new GitException($"{Describe(dir, CommonDirArgs)} printed \"{path}\", which is not a full path");
        }
        return path;
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
    /// The error for a git child that outlived its limit, after its tree is killed and its exit and reads are each waited for up to
    /// <paramref name="grace"/>. A cancel that arrives during those waits ends them, and the error becomes the cancel's.
    /// </summary>
    private static Exception TimedOut(Call call, TimeSpan timeout, TimeSpan grace, CancellationToken token)
    {
        Exception? failure = call.Kill.Value;
        _ = WaitForExit(call.Process, grace, token);
        _ = WaitForReads(call.Reads, grace, token);
        if (token.IsCancellationRequested)
        {
            return Cancelled(call, token);
        }
        string limit = $"{call.Command} did not exit within {Seconds(timeout)} s";
        return failure is not null
            ? new GitTimeoutException($"{limit}; killing it failed: {failure.Message}", failure)
            : new GitTimeoutException($"{limit} and was killed");
    }

    /// <summary>
    /// The error for a cancelled call, after git's tree is killed, whether or not git itself has exited: a process git started
    /// may still hold its output. Its exit and reads are each waited for up to <see cref="CancelGrace"/>, so a cancel never waits
    /// the longer <see cref="ReadGrace"/>.
    /// </summary>
    private static OperationCanceledException Cancelled(Call call, CancellationToken token)
    {
        Exception? failure = call.Kill.Value;
        _ = WaitForExit(call.Process, CancelGrace, CancellationToken.None);
        _ = WaitForReads(call.Reads, CancelGrace, CancellationToken.None);
        string cancelled = $"{call.Command} was cancelled";
        return failure is not null
            ? new OperationCanceledException($"{cancelled}; killing it failed: {failure.Message}", failure, token)
            : new OperationCanceledException($"{cancelled} and was killed", token);
    }

    /// <summary>
    /// Kills a git child with its whole tree: a grandchild such as the shell running an alias holds the output pipes open, so
    /// killing git alone would leave the reads waiting. The one kill path for a timeout and a cancel.
    /// </summary>
    /// <returns><see langword="null"/> when the kill was sent; the error when it failed.</returns>
    private static Exception? KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or AggregateException)
        {
            return error;
        }
    }

    /// <summary>
    /// Waits up to <paramref name="limit"/> for git to exit, on its process handle directly rather than through the thread pool,
    /// and returns early once <paramref name="token"/> is cancelled.
    /// </summary>
    /// <returns>Whether git has exited.</returns>
    private static bool WaitForExit(Process process, TimeSpan limit, CancellationToken token)
    {
        if (!token.CanBeCanceled)
        {
            return process.WaitForExit(limit);
        }
        using var exit = new ProcessWaitHandle(process);
        return WaitHandle.WaitAny([exit, token.WaitHandle], limit) == 0;
    }

    /// <summary>Waits up to <paramref name="limit"/> for git's output pipes to close, returning early once <paramref name="token"/>
    /// is cancelled.</summary>
    /// <returns>Whether the reads have ended.</returns>
    private static bool WaitForReads(Task reads, TimeSpan limit, CancellationToken token)
    {
        try
        {
            return reads.Wait(limit, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>One git call: the process, the task reading its output, the command as messages name it, and the kill of its
    /// tree, run at most once whether a cancel, a timeout or both ask for it.</summary>
    private sealed record Call(Process Process, Task Reads, string Command, Lazy<Exception?> Kill);

    /// <summary>
    /// A git process's handle as a <see cref="WaitHandle"/>, so it can be waited on beside a token's. The <see cref="Process"/> keeps
    /// owning the handle: disposing this closes nothing.
    /// </summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(Process process)
        {
            SafeWaitHandle = new SafeWaitHandle(process.Handle, ownsHandle: false);
        }
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
