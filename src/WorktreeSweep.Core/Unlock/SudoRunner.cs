using System.ComponentModel;
using System.Diagnostics;

namespace WorktreeSweep.Unlock;

/// <summary>The real <c>sudo</c>, found on PATH and run in this console.</summary>
public sealed class SudoRunner : ISudoRunner
{
    /// <inheritdoc/>
    public SudoMode? Mode() => SudoConfig.Read();

    /// <inheritdoc/>
    public int Run(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo("sudo") { UseShellExecute = false };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        try
        {
            using Process process = Process.Start(startInfo) ?? throw new UnlockException("cannot run sudo: it did not start");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception error)
        {
            throw new UnlockException($"cannot run sudo: {error.Message}", error);
        }
    }
}
