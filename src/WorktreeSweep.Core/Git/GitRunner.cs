using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace WorktreeSweep.Git;

/// <summary>
/// Runs <c>git</c> as a child process. Every call sets <c>GIT_OPTIONAL_LOCKS=0</c> and <c>core.fsmonitor=false</c>, so reading a
/// repo never rewrites its index or starts a file-system monitor daemon in it, and removes <see cref="RepoLocalEnvVars"/> from the
/// child's environment.
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

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Runs <c>git -C {dir} {args}</c> and returns its trimmed standard output.</summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments, after <c>-C {dir}</c>.</param>
    /// <returns>Standard output, trimmed.</returns>
    /// <exception cref="GitException">Git cannot be started or exits with a non-zero code; the message names the command, the
    /// exit code and git's standard error.</exception>
    public static string Run(string dir, IReadOnlyList<string> args) => RunRaw(dir, args).Trim();

    /// <summary>
    /// Runs <c>git -C {dir} {args}</c> and returns its standard output untrimmed, for output where leading spaces or NUL
    /// separators carry meaning.
    /// </summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments, after <c>-C {dir}</c>.</param>
    /// <returns>Standard output, untrimmed.</returns>
    /// <exception cref="GitException">Git cannot be started or exits with a non-zero code; the message names the command, the
    /// exit code and git's standard error.</exception>
    public static string RunRaw(string dir, IReadOnlyList<string> args)
    {
        GitStatus status = RunStatus(dir, args);
        return status.Success ? status.Stdout : throw Failure(dir, args, status);
    }

    /// <summary>Runs <c>git -C {dir} {args}</c> and returns its exit code and output without treating a non-zero exit as an error.</summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments, after <c>-C {dir}</c>.</param>
    /// <returns>The exit code and both streams.</returns>
    /// <exception cref="GitException">Only when git cannot be started.</exception>
    public static GitStatus RunStatus(string dir, IReadOnlyList<string> args) =>
        RunStatusWithEnv(dir, args, ImmutableDictionary<string, string>.Empty);

    /// <summary>Like <see cref="RunStatus"/>, with extra environment variables set for the child after the repo-local ones are removed.</summary>
    /// <param name="dir">The directory git runs in.</param>
    /// <param name="args">The git arguments, after <c>-C {dir}</c>.</param>
    /// <param name="env">Variables to set for the child.</param>
    /// <returns>The exit code and both streams.</returns>
    /// <exception cref="GitException">Only when git cannot be started.</exception>
    public static GitStatus RunStatusWithEnv(string dir, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env)
    {
        ProcessStartInfo startInfo = StartInfo(dir, args, env);
        using Process process = Start(startInfo, dir, args);
        process.StandardInput.Close();
        Task<byte[]> stderr = ReadAllAsync(process.StandardError.BaseStream);
        byte[] stdout = ReadAll(process.StandardOutput.BaseStream);
        byte[] stderrBytes = stderr.GetAwaiter().GetResult();
        process.WaitForExit();
        return new GitStatus(process.ExitCode, Utf8.GetString(stdout), Utf8.GetString(stderrBytes).Trim());
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

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static string Describe(string dir, IReadOnlyList<string> args) => $"`git -C {dir} {string.Join(' ', args)}`";
}
