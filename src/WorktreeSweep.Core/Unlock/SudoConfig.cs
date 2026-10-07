using System.ComponentModel;
using System.Diagnostics;

namespace WorktreeSweep.Unlock;

/// <summary>The configuration <c>sudo config</c> reports.</summary>
public static class SudoConfig
{
    /// <summary>How long <c>sudo config</c> may take before it is killed.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Reads the mode from <c>sudo config</c> output such as <c>Sudo is currently in Inline mode on this machine</c>.</summary>
    /// <param name="configOutput">Standard output and standard error of <c>sudo config</c>, concatenated.</param>
    /// <returns><see cref="SudoMode.Disabled"/> when the text says sudo is turned off, else the mode it names, else
    /// <see cref="SudoMode.Unknown"/>.</returns>
    public static SudoMode Parse(string configOutput)
    {
        ArgumentNullException.ThrowIfNull(configOutput);
        string text = configOutput.ToLowerInvariant();
        if (text.Contains("disabled", StringComparison.Ordinal))
        {
            return SudoMode.Disabled;
        }
        if (text.Contains("inline mode", StringComparison.Ordinal))
        {
            return SudoMode.Inline;
        }
        if (text.Contains("new window", StringComparison.Ordinal))
        {
            return SudoMode.ForceNewWindow;
        }
        if (text.Contains("disable input", StringComparison.Ordinal) || text.Contains("input closed", StringComparison.Ordinal))
        {
            return SudoMode.DisableInput;
        }
        return SudoMode.Unknown;
    }

    /// <summary>Runs <c>sudo config</c> and reads the mode it reports.</summary>
    /// <returns>The mode, or <see langword="null"/> when there is no <c>sudo</c> on the PATH.</returns>
    /// <exception cref="UnlockException"><c>sudo config</c> cannot be started, or does not finish within
    /// <see cref="Limit"/>.</exception>
    public static SudoMode? Read()
    {
        var startInfo = new ProcessStartInfo("sudo")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("config");
        Process? started = TryStart(startInfo);
        if (started is null)
        {
            return null;
        }
        using Process process = started;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Limit))
        {
            Kill(process);
            throw new UnlockException("sudo config did not finish within 10 s; run the unlock step by hand");
        }
        return Parse(stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    /// <summary>Whether a start failure means there is no <c>sudo</c> to run.</summary>
    /// <param name="nativeErrorCode">The <see cref="Win32Exception.NativeErrorCode"/> of the failure.</param>
    /// <returns><see langword="true"/> for 2 (file not found) and 3 (path not found), the codes a missing <c>sudo.exe</c>
    /// gives.</returns>
    internal static bool IsNotFound(int nativeErrorCode) => nativeErrorCode is 2 or 3;

    /// <summary>Starts <c>sudo config</c>, both of whose streams the caller then reads concurrently, so neither can fill and
    /// block it.</summary>
    /// <param name="startInfo">How to start it.</param>
    /// <returns>The running process, or <see langword="null"/> when there is no <c>sudo</c> on the PATH.</returns>
    /// <exception cref="UnlockException">It cannot be started for any other reason.</exception>
    private static Process? TryStart(ProcessStartInfo startInfo)
    {
        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception error) when (IsNotFound(error.NativeErrorCode))
        {
            return null;
        }
        catch (Exception error)
        {
            throw new UnlockException("cannot run `sudo config`", error);
        }
        return process ?? throw new UnlockException("cannot start sudo");
    }

    /// <summary>Kills a <c>sudo config</c> that outlived its limit, with its whole tree; one that exited since the wait is
    /// nothing left to kill.</summary>
    /// <param name="process">The process.</param>
    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            Trace.TraceWarning($"cannot stop the sudo config that outlived its limit: {error.Message}");
        }
    }
}
