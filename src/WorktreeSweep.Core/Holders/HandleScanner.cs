namespace WorktreeSweep.Holders;

/// <summary>
/// Lists and names the open disk handles of the inspected processes, and finds the processes using the folder. Its names and unlisted
/// processes cover only <c>pids</c>; the processes using the folder may be any.
/// </summary>
/// <param name="pids">The inspected processes: those running as the current user.</param>
/// <param name="roots">Every spelling of the folder, the fully resolved one first.</param>
/// <returns>What the handles showed.</returns>
internal delegate HandleFindings HandleScanner(IReadOnlyCollection<int> pids, IReadOnlyList<string> roots);

/// <summary>What a <see cref="HandleScanner"/> found.</summary>
/// <param name="Names">Each disk handle's process and name; the name is <see langword="null"/> when it could not be read in time, or
/// when the check failed on a process listed in <paramref name="Using"/>.</param>
/// <param name="Unlisted">Processes whose handles could not be listed.</param>
/// <param name="Using">Processes the file system reports as using the folder.</param>
internal sealed record HandleFindings(IReadOnlyList<(int Pid, string? Name)> Names, IReadOnlyCollection<int> Unlisted, IReadOnlyList<int> Using)
{
    /// <summary>Gets the findings when handles are not looked at: nothing.</summary>
    public static HandleFindings None { get; } = new([], [], []);
}
