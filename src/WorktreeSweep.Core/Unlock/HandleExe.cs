using System.Globalization;

namespace WorktreeSweep.Unlock;

/// <summary>The real <c>handle.exe</c>, found on PATH and run as a child process.</summary>
public sealed class HandleExe : IHandleExe
{
    /// <summary>The command that installs Sysinternals <c>handle.exe</c>.</summary>
    private const string Install = "winget install Microsoft.Sysinternals.Handle";

    /// <summary>The program name, looked up on PATH.</summary>
    private const string Program = "handle.exe";

    /// <summary>How long closing one handle may take.</summary>
    private static readonly TimeSpan CloseLimit = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    public bool Close(nint handle, int pid, out string output)
    {
        ChildResult result = Run(
            ["-nobanner", "-c", handle.ToString("X", CultureInfo.InvariantCulture), "-p", pid.ToString(CultureInfo.InvariantCulture), "-y"],
            CloseLimit
        );
        output = result.Output;
        return result.ExitCode == 0;
    }

    /// <summary>Runs <c>handle.exe</c>, naming the missing install when the program is not on PATH.</summary>
    /// <param name="arguments">The arguments.</param>
    /// <param name="limit">How long it may take.</param>
    /// <returns>The exit code and both streams.</returns>
    /// <exception cref="UnlockException"><c>handle.exe</c> is not on PATH, cannot be run, or does not finish in time.</exception>
    private static ChildResult Run(IReadOnlyList<string> arguments, TimeSpan limit)
    {
        string program = ChildProcess.FindOnPath(Program, Environment.GetEnvironmentVariable("PATH")) ?? throw NotOnPath();
        try
        {
            // Decoded with the default encoding: the output is only quoted in a failure trace.
            return ChildProcess.Run(program, arguments, limit, outputEncoding: null);
        }
        catch (ProgramNotFoundException)
        {
            throw NotOnPath();
        }
    }

    /// <summary>The failure for a <c>handle.exe</c> the PATH search does not find, naming the command that installs it.</summary>
    /// <returns>The failure.</returns>
    private static UnlockException NotOnPath() => new($"handle.exe is not on PATH; install it with `{Install}`");
}
