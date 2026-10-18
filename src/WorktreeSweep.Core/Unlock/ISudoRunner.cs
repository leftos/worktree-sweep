namespace WorktreeSweep.Unlock;

/// <summary>The <c>sudo</c> the unlock offer checks and runs, so tests can stand in for it.</summary>
public interface ISudoRunner
{
    /// <summary>The mode <c>sudo config</c> reports.</summary>
    /// <returns>The mode, or <see langword="null"/> when there is no <c>sudo</c> on the PATH.</returns>
    /// <exception cref="UnlockException"><c>sudo config</c> cannot be run.</exception>
    SudoMode? Mode();

    /// <summary>Runs <c>sudo</c> with the console shared, so the elevated side's prompts reach the user, and waits for it.</summary>
    /// <param name="arguments">The arguments to give <c>sudo</c>, the program to elevate first.</param>
    /// <param name="resultFile">Where the elevated side writes its exit code; the wait is bounded once it holds one.</param>
    /// <returns>The exit code of <c>sudo</c>, which is the elevated program's, or the code it reported when it did not exit in
    /// time, and whether it is still running.</returns>
    /// <exception cref="UnlockException"><c>sudo</c> cannot be started.</exception>
    AttachedExit Run(IReadOnlyList<string> arguments, string resultFile);
}
