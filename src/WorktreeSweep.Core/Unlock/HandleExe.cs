using System.Globalization;

namespace WorktreeSweep.Unlock;

/// <summary>The real <c>handle.exe</c>, found on PATH and run as a child process.</summary>
public sealed class HandleExe : IHandleExe
{
    /// <summary>The command that installs Sysinternals <c>handle.exe</c>.</summary>
    private const string Install = "winget install Microsoft.Sysinternals.Handle";

    /// <summary>The program name, looked up on PATH.</summary>
    private const string Program = "handle.exe";

    /// <summary>How many lines of a failed dump's output the failure message keeps.</summary>
    private const int FailureLines = 20;

    /// <summary>How long a dump may take: a dump of every handle on a machine, run unelevated, measured about 141 s.</summary>
    private static readonly TimeSpan DumpLimit = TimeSpan.FromSeconds(300);

    /// <summary>How long closing one handle may take.</summary>
    private static readonly TimeSpan CloseLimit = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    public string Dump()
    {
        ChildResult result = Run(["-nobanner", "-accepteula", "-v"], DumpLimit);
        if (result.ExitCode == 0 || result.Output.Contains(HandleCsv.NoMatch, StringComparison.Ordinal))
        {
            return result.Output;
        }
        throw new UnlockException($"handle.exe failed (exit {result.ExitCode}): {Tail(result.Output)} {result.Error.Trim()}");
    }

    /// <inheritdoc/>
    public string DumpProcess(int pid) => Run(["-nobanner", "-accepteula", "-v", "-p", pid.ToString(CultureInfo.InvariantCulture)], DumpLimit).Output;

    /// <inheritdoc/>
    public bool Close(ulong handle, int pid, out string output)
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
        try
        {
            // Decoded with the default encoding: handle.exe prints `?` for every character outside the machine's code
            // page, so no decoding of its output can recover such a name, and a measured Greek file name came back
            // mangled in UTF-8, in the OEM code page and in the ANSI code page alike.
            return ChildProcess.Run(Program, arguments, limit, outputEncoding: null);
        }
        catch (ProgramNotFoundException)
        {
            throw new UnlockException($"handle.exe is not on PATH; install it with `{Install}`");
        }
    }

    /// <summary>The last <see cref="FailureLines"/> lines of a dump, so a failure message stays short.</summary>
    /// <param name="output">The dump's standard output.</param>
    /// <returns>The tail, trimmed.</returns>
    private static string Tail(string output)
    {
        string[] lines = output.Split('\n');
        return string.Join('\n', lines[^Math.Min(lines.Length, FailureLines)..]).Trim();
    }
}
