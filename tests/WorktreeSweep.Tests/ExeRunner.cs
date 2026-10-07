using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using WorktreeSweep.Git;

namespace WorktreeSweep.Tests;

/// <summary>
/// The built program run as a test runs it: its exit code, its output, and the environment and folder it starts in. Every
/// call closes the run's standard input and kills it when it outlives its timeout.
/// </summary>
internal static class ExeRunner
{
    /// <summary>The built <c>worktree-sweep.exe</c>, beside the test assembly.</summary>
    internal static readonly string Exe = Path.Join(AppContext.BaseDirectory, "worktree-sweep.exe");

    private const string LogVariable = "WORKTREE_SWEEP_LOG";

    private static readonly IReadOnlyDictionary<string, string> NoEnv = new Dictionary<string, string>();

    /// <summary>Runs <see cref="Exe"/> in the current folder.</summary>
    /// <param name="timeout">How long the run may take before it is killed.</param>
    /// <param name="args">The command line.</param>
    /// <returns>The run.</returns>
    internal static Task<Run> RunAsync(TimeSpan timeout, params string[] args) => RunInAsync(Environment.CurrentDirectory, timeout, args);

    /// <summary>Runs <see cref="Exe"/> in the current folder with <paramref name="env"/> set on the cleared environment.</summary>
    /// <param name="timeout">How long the run may take before it is killed.</param>
    /// <param name="env">The variables to set.</param>
    /// <param name="args">The command line.</param>
    /// <returns>The run.</returns>
    internal static Task<Run> RunWithEnvAsync(TimeSpan timeout, IReadOnlyDictionary<string, string> env, params string[] args) =>
        LaunchAsync(Exe, Environment.CurrentDirectory, timeout, env, args);

    /// <summary>Runs <see cref="Exe"/> in <paramref name="workingDirectory"/>.</summary>
    /// <param name="workingDirectory">The folder the exe starts in.</param>
    /// <param name="timeout">How long the run may take before it is killed.</param>
    /// <param name="args">The command line.</param>
    /// <returns>The run.</returns>
    internal static Task<Run> RunInAsync(string workingDirectory, TimeSpan timeout, params string[] args) =>
        LaunchAsync(Exe, workingDirectory, timeout, NoEnv, args);

    /// <summary>Runs <paramref name="program"/> in <paramref name="workingDirectory"/>.</summary>
    /// <param name="workingDirectory">The folder the program starts in.</param>
    /// <param name="program">The program to run.</param>
    /// <param name="timeout">How long the run may take before it is killed.</param>
    /// <param name="args">The command line.</param>
    /// <returns>The run.</returns>
    internal static Task<Run> RunProgramAsync(string workingDirectory, string program, TimeSpan timeout, params string[] args) =>
        LaunchAsync(program, workingDirectory, timeout, NoEnv, args);

    /// <summary>
    /// Starts <paramref name="program"/> with the repo-local git variables and <c>WORKTREE_SWEEP_LOG</c> cleared and
    /// <paramref name="env"/> set, its standard input closed, killing it when it outlives <paramref name="timeout"/> or the
    /// test run is cancelled.
    /// </summary>
    private static async Task<Run> LaunchAsync(
        string program,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string> env,
        string[] args
    )
    {
        var startInfo = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        GitRunner.ClearRepoEnv(startInfo);
        startInfo.Environment.Remove(LogVariable);
        foreach (KeyValuePair<string, string> pair in env)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{program} did not start");
        process.StandardInput.Close();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(timeout);
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancel.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancel.Token);
            await process.WaitForExitAsync(cancel.Token);
            return new Run(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            KillIfRunning(process, program);
            if (TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            throw new TimeoutException($"{program} {string.Join(' ', args)} did not exit within {timeout}");
        }
    }

    /// <summary>Kills <paramref name="process"/> and its tree; one that already exited is only noted.</summary>
    /// <param name="process">The process to kill.</param>
    /// <param name="program">Its name, for the note.</param>
    private static void KillIfRunning(Process process, string program)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException error)
        {
            TestContext.Current.SendDiagnosticMessage($"{program} had already exited when it was to be killed: {error.Message}");
        }
    }
}

/// <summary>One run's exit code and output; <see cref="Json"/> parses standard output as the report.</summary>
/// <param name="Code">The exit code.</param>
/// <param name="Stdout">Standard output, decoded as UTF-8.</param>
/// <param name="Stderr">Standard error, decoded as UTF-8.</param>
internal sealed record Run(int Code, string Stdout, string Stderr)
{
    /// <summary>Standard output parsed as JSON.</summary>
    public JsonNode Json => JsonNode.Parse(Stdout) ?? throw new InvalidOperationException($"stdout is JSON null; stderr: {Stderr}");

    /// <summary>One top-level field of <see cref="Json"/> as a string, or <see langword="null"/>.</summary>
    /// <param name="key">The field's name.</param>
    /// <returns>The value.</returns>
    public string? Text(string key) => Json[key]?.GetValue<string>();

    /// <summary>The process ids of one top-level array field of <see cref="Json"/>.</summary>
    /// <param name="key">The field's name.</param>
    /// <returns>The pids.</returns>
    public List<int> Pids(string key) => Pids(Json[key]);

    /// <summary>The process ids of an array node.</summary>
    /// <param name="items">The array, or <see langword="null"/>.</param>
    /// <returns>The pids.</returns>
    public static List<int> Pids(JsonNode? items) =>
        [.. (items?.AsArray() ?? []).Select(item => item?["pid"]?.GetValue<int>() ?? throw new InvalidOperationException($"no pid in {item}"))];
}
