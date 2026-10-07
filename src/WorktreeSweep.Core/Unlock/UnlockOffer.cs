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
    /// <returns><see cref="UnlockOutcome.Skipped"/> when <c>sudo</c> cannot be used or the user declines, otherwise the outcome
    /// the elevated session's exit code names.</returns>
    /// <exception cref="UnlockException">This program's path is unknown or is the <c>dotnet</c> host, <c>sudo</c> cannot be run,
    /// or the elevated session ends with a code it never uses.</exception>
    public UnlockOutcome Offer(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        output.WriteLine("Another process holds files open under:");
        foreach (string path in paths)
        {
            output.WriteLine($"  {path}");
        }
        output.Flush();
        string exe = ExePath();
        SudoMode? mode = sudo.Mode();
        if (mode != SudoMode.Inline)
        {
            output.WriteLine(
                mode is SudoMode other
                    ? $"sudo is set to {other.Label()} mode; worktree-sweep needs inline mode (sudo config --enable normal)."
                    : "sudo is not available on this machine."
            );
            SudoCommand.WriteManual(output, exe, paths);
            output.Flush();
            return UnlockOutcome.Skipped;
        }
        if (!LinePrompt.AskYesNo(input, output, Question, defaultAnswer: true))
        {
            return UnlockOutcome.Skipped;
        }
        int code = sudo.Run(SudoCommand.Argv(exe, self.CallerPid, self.Pid, paths));
        return UnlockOutcomes.ForExit(code)
            ?? throw new UnlockException(
                $"the elevated scan failed (exit code {code}); to retry, run in an administrator terminal: {SudoCommand.Manual(exe, paths)}"
            );
    }

    /// <summary>The program <c>sudo</c> is to run: this one, unless it is the <c>dotnet</c> host, which would not run this program.</summary>
    /// <returns>The path of this program.</returns>
    /// <exception cref="UnlockException">The path is unknown, or it is <c>dotnet</c>.</exception>
    private string ExePath()
    {
        string exe = self.ExePath ?? throw new UnlockException("cannot find the path of this program");
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnlockException($"worktree-sweep is running under {exe}, which sudo cannot run as this program; run {Apphost} instead");
        }
        return exe;
    }
}
