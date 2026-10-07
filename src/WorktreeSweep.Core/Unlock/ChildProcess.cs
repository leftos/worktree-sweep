using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace WorktreeSweep.Unlock;

/// <summary>Runs a child process to completion, reading both of its streams and its exit code.</summary>
internal static class ChildProcess
{
    /// <summary>Runs <paramref name="program"/> with <paramref name="arguments"/> and waits for it.</summary>
    /// <param name="program">The program name, looked up on PATH.</param>
    /// <param name="arguments">The arguments, passed one by one so nothing is re-parsed.</param>
    /// <param name="timeout">How long it may run; past that its whole tree is killed.</param>
    /// <param name="outputEncoding">How to decode its output, or <see langword="null"/> for the default of
    /// <see cref="ProcessStartInfo.StandardOutputEncoding"/>.</param>
    /// <returns>The exit code and both decoded streams.</returns>
    /// <exception cref="ProgramNotFoundException">The program is not on the PATH.</exception>
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

    /// <summary>Whether a start failure means the program is not on the PATH.</summary>
    /// <param name="nativeErrorCode">The <see cref="Win32Exception.NativeErrorCode"/> of the failure.</param>
    /// <returns><see langword="true"/> for 2 (file not found) and 3 (path not found), the codes a missing program
    /// gives.</returns>
    internal static bool IsNotFound(int nativeErrorCode) => nativeErrorCode is 2 or 3;

    /// <summary>Starts the program, telling a missing one from one that cannot be run at all.</summary>
    /// <param name="program">The program name.</param>
    /// <param name="startInfo">How to start it.</param>
    /// <returns>The running process.</returns>
    /// <exception cref="ProgramNotFoundException">The program is not on the PATH.</exception>
    /// <exception cref="UnlockException">It cannot be started for any other reason.</exception>
    private static Process Start(string program, ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new UnlockException($"cannot start {program}");
        }
        catch (Win32Exception error) when (IsNotFound(error.NativeErrorCode))
        {
            throw new ProgramNotFoundException($"{program} is not on the PATH", error);
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

/// <summary>A program cannot be run because it is not on the PATH.</summary>
internal sealed class ProgramNotFoundException : UnlockException
{
    /// <summary>Initializes a new instance of the <see cref="ProgramNotFoundException"/> class.</summary>
    /// <param name="message">What is missing.</param>
    /// <param name="innerException">The start failure that showed it.</param>
    internal ProgramNotFoundException(string message, Exception innerException)
        : base(message, innerException) { }
}
