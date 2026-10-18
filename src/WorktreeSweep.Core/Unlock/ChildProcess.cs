using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace WorktreeSweep.Unlock;

/// <summary>Runs a child process to completion with both of its streams read, or attached to this console until it exits or
/// reports its result.</summary>
internal static class ChildProcess
{
    /// <summary>How often the wait looks for the program's result file.</summary>
    private static readonly TimeSpan ResultPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>Runs <paramref name="program"/> with <paramref name="arguments"/> and waits for it.</summary>
    /// <param name="program">The full path of the program.</param>
    /// <param name="arguments">The arguments, passed one by one so nothing is re-parsed.</param>
    /// <param name="timeout">How long it may run; past that its whole tree is killed.</param>
    /// <param name="outputEncoding">How to decode its output, or <see langword="null"/> for the default of
    /// <see cref="ProcessStartInfo.StandardOutputEncoding"/>.</param>
    /// <returns>The exit code and both decoded streams.</returns>
    /// <exception cref="ProgramNotFoundException">The program does not exist.</exception>
    /// <exception cref="UnlockException">It cannot be started for any other reason, or does not finish within
    /// <paramref name="timeout"/>.</exception>
    internal static ChildResult Run(string program, IReadOnlyList<string> arguments, TimeSpan timeout, Encoding? outputEncoding)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (outputEncoding is Encoding encoding)
        {
            startInfo.StandardOutputEncoding = encoding;
            startInfo.StandardErrorEncoding = encoding;
        }
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        Process process = Start(program, startInfo);
        using (process)
        {
            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                Kill(process, program);
                throw new UnlockException($"{program} did not finish within {timeout.TotalSeconds} s");
            }
            return new ChildResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
    }

    /// <summary>
    /// Runs <paramref name="program"/> in this console, its streams shared with this process so it can prompt the user, and waits
    /// for it. Once the process writes its result to <paramref name="resultFile"/>, it is given <paramref name="afterResult"/>
    /// more to exit; past that its result is used and the process is left running, never stopped. The result file is deleted
    /// before this returns.
    /// </summary>
    /// <param name="program">The full path of the program.</param>
    /// <param name="arguments">The arguments, passed one by one so nothing is re-parsed.</param>
    /// <param name="resultFile">Where the program writes its result, one integer as text.</param>
    /// <param name="afterResult">How long the process may still run once its result can be read.</param>
    /// <returns>The exit code, or the reported result when the process did not exit in time, and whether it is still running.</returns>
    /// <exception cref="ProgramNotFoundException">The program does not exist.</exception>
    /// <exception cref="UnlockException">It cannot be started for any other reason.</exception>
    internal static AttachedExit RunAttached(string program, IReadOnlyList<string> arguments, string resultFile, TimeSpan afterResult)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(resultFile);
        ArgumentOutOfRangeException.ThrowIfLessThan(afterResult, TimeSpan.Zero);
        var startInfo = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using Process process = Start(program, startInfo);
        try
        {
            return WaitAttached(process, resultFile, afterResult);
        }
        finally
        {
            UnlockResultFile.Delete(resultFile);
        }
    }

    /// <summary>Waits for the process to exit, but for no more than <paramref name="afterResult"/> once its result is readable.</summary>
    /// <param name="process">The running process.</param>
    /// <param name="resultFile">Where the process writes its result.</param>
    /// <param name="afterResult">How long it may still run once its result can be read.</param>
    /// <returns>The exit code and whether the process was left running.</returns>
    private static AttachedExit WaitAttached(Process process, string resultFile, TimeSpan afterResult)
    {
        while (!process.WaitForExit(ResultPoll))
        {
            if (UnlockResultFile.TryRead(resultFile) is not int code)
            {
                continue;
            }
            if (process.WaitForExit(afterResult))
            {
                return new AttachedExit(process.ExitCode, StillRunning: false);
            }
            return new AttachedExit(code, StillRunning: true);
        }
        return new AttachedExit(process.ExitCode, StillRunning: false);
    }

    /// <summary>
    /// Finds <paramref name="fileName"/> in the folders of a PATH value, in order. Only fully qualified entries are searched, so
    /// neither the current directory nor an entry relative to it (<c>.</c>, an empty entry, <c>bin</c>) can supply the program.
    /// </summary>
    /// <param name="fileName">The program's file name, extension included, such as <c>handle.exe</c>.</param>
    /// <param name="pathVariable">The PATH value: folders separated by <c>;</c>, each optionally in double quotes.</param>
    /// <returns>The full path of the first match, or <see langword="null"/> when no searched folder holds it.</returns>
    internal static string? FindOnPath(string fileName, string? pathVariable)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        foreach (string entry in (pathVariable ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string folder = entry.Trim('"');
            if (!Path.IsPathFullyQualified(folder))
            {
                continue;
            }
            string candidate = Path.Join(folder, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>Whether a start failure means the program does not exist.</summary>
    /// <param name="nativeErrorCode">The <see cref="Win32Exception.NativeErrorCode"/> of the failure.</param>
    /// <returns><see langword="true"/> for 2 (file not found) and 3 (path not found), the codes a missing program
    /// gives.</returns>
    internal static bool IsNotFound(int nativeErrorCode) => nativeErrorCode is 2 or 3;

    /// <summary>Starts the program, telling a missing one from one that cannot be run at all.</summary>
    /// <param name="program">The program name.</param>
    /// <param name="startInfo">How to start it.</param>
    /// <returns>The running process.</returns>
    /// <exception cref="ProgramNotFoundException">The program does not exist.</exception>
    /// <exception cref="UnlockException">It cannot be started for any other reason.</exception>
    private static Process Start(string program, ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new UnlockException($"cannot start {program}");
        }
        catch (Win32Exception error) when (IsNotFound(error.NativeErrorCode))
        {
            throw new ProgramNotFoundException($"cannot find {program}", error);
        }
        catch (Win32Exception error)
        {
            throw new UnlockException($"cannot run {program}: {error.Message}", error);
        }
    }

    /// <summary>Kills a process that outlived its timeout, with its whole tree; one that exited since the wait is nothing left
    /// to kill.</summary>
    /// <param name="process">The process.</param>
    /// <param name="program">The program name, for the warning.</param>
    private static void Kill(Process process, string program)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            Trace.TraceWarning($"cannot stop the {program} that outlived its limit: {error.Message}");
        }
    }
}

/// <summary>What one child process run produced.</summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="Output">Standard output, decoded.</param>
/// <param name="Error">Standard error, decoded.</param>
internal sealed record ChildResult(int ExitCode, string Output, string Error);

/// <summary>What waiting for a program attached to this console produced.</summary>
/// <param name="Code">Its exit code, or the result it reported when it could not exit in time.</param>
/// <param name="StillRunning">Whether it was left running because it did not exit after reporting its result.</param>
public readonly record struct AttachedExit(int Code, bool StillRunning);

/// <summary>A program cannot be run because it does not exist.</summary>
internal sealed class ProgramNotFoundException : UnlockException
{
    /// <summary>Initializes a new instance of the <see cref="ProgramNotFoundException"/> class.</summary>
    /// <param name="message">What is missing.</param>
    /// <param name="innerException">The start failure that showed it.</param>
    internal ProgramNotFoundException(string message, Exception innerException)
        : base(message, innerException) { }
}
