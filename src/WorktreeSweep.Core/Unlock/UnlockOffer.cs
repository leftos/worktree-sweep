namespace WorktreeSweep.Unlock;

/// <summary>
/// The unelevated side of the unlock flow, offered once per run: prints the locked paths, checks that <c>sudo</c> is in Inline
/// mode, asks (default yes), and runs this program's <c>unlock</c> subcommand through <c>sudo</c> with the console shared, so the
/// elevated prompts reach the user. When <c>sudo</c> is missing or in another mode it prints the command to run by hand and skips.
/// </summary>
/// <param name="input">Where the answer comes from.</param>
/// <param name="output">Where the paths, the question and the manual command go.</param>
/// <param name="sudo">The <c>sudo</c> checked and run.</param>
/// <param name="self">This program's path, its caller and its PID, which the elevated side is given.</param>
public sealed class UnlockOffer(TextReader input, TextWriter output, ISudoRunner sudo, SweepProcess self)
{
    /// <summary>The question the offer asks before elevating.</summary>
    private const string Question = "Run an elevated scan with sudo to find what holds them?";

    /// <summary>The program <c>sudo</c> must run instead of <c>dotnet</c>: the apphost the build puts beside the assembly.</summary>
    private const string Apphost = "worktree-sweep.exe";

    /// <summary>Offers to find and clear the locks other processes hold on files under the paths.</summary>
    /// <param name="paths">The locked paths.</param>
    /// <returns><see cref="UnlockOutcome.Skipped"/> when <c>sudo</c> cannot be used, this program runs under the <c>dotnet</c>
    /// host, the user declines, or the elevated session ends with a code it never uses (a declined UAC prompt included), each
    /// but the decline with a line saying why; otherwise the outcome the elevated session's exit code names.</returns>
    /// <exception cref="UnlockException">This program's path is unknown, <c>sudo config</c> cannot be run, or <c>sudo</c>
    /// cannot be started.</exception>
    public UnlockOutcome Offer(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        output.WriteLine("Another process holds files open under:");
        foreach (string path in paths)
        {
            output.WriteLine($"  {path}");
        }
        output.Flush();
        string exe = self.ExePath ?? throw new UnlockException("cannot find the path of this program");
        SudoMode? mode = sudo.Mode();
        if (mode != SudoMode.Inline)
        {
            output.WriteLine(
                mode is SudoMode other
                    ? $"sudo is set to {other.Label()} mode; worktree-sweep needs inline mode (sudo config --enable normal)."
                    : "sudo is not available on this machine."
            );
            SudoCommand.WriteManual(output, exe, paths);
            return Skip();
        }
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine($"worktree-sweep is running under {exe}, which sudo cannot run as this program; run {Apphost} instead");
            return Skip();
        }
        if (!LinePrompt.AskYesNo(input, output, Question, defaultAnswer: true))
        {
            return UnlockOutcome.Skipped;
        }
        int code = sudo.Run(SudoCommand.Argv(exe, self.CallerPid, self.Pid, paths));
        if (UnlockOutcomes.ForExit(code) is UnlockOutcome outcome)
        {
            return outcome;
        }
        output.WriteLine(
            $"the elevated scan failed (exit code {code}); to retry, run in an administrator terminal: {SudoCommand.Manual(exe, paths)}"
        );
        return Skip();
    }

    /// <summary>Flushes what the offer wrote and skips the unlock.</summary>
    /// <returns><see cref="UnlockOutcome.Skipped"/>.</returns>
    private UnlockOutcome Skip()
    {
        output.Flush();
        return UnlockOutcome.Skipped;
    }
}
