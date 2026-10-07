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
        ChildResult result;
        try
        {
            result = ChildProcess.Run("sudo", ["config"], Limit, outputEncoding: null);
        }
        catch (ProgramNotFoundException)
        {
            return null;
        }
        return Parse(result.Output + result.Error);
    }
}
