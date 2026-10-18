namespace WorktreeSweep.Holders;

/// <summary>
/// Lists and names the open disk handles of the inspected processes, and finds the processes using the folder. Its names and unlisted
/// processes cover only <c>pids</c>; the processes using the folder may be any.
/// </summary>
/// <param name="pids">The inspected processes: those running as the current user.</param>
/// <param name="roots">Each folder once, fully resolved.</param>
/// <param name="account">Records each name lookup given up on while it runs; the caller reads it before it decides the folder is free.</param>
/// <returns>What the handles showed.</returns>
internal delegate HandleFindings HandleScanner(IReadOnlyCollection<int> pids, IReadOnlyList<string> roots, LookupAccount account);

/// <summary>What a <see cref="HandleScanner"/> found.</summary>
/// <param name="Names">Each disk handle; its name is <see langword="null"/> when it could not be read in time, or when the check
/// failed on a process listed in <paramref name="Users"/>.</param>
/// <param name="Unlisted">Processes whose handles could not be listed.</param>
/// <param name="Users">Each process the file system reports as using one of the roots, with that root.</param>
internal sealed record HandleFindings(IReadOnlyList<NamedHandle> Names, IReadOnlyCollection<int> Unlisted, IReadOnlyList<RootUser> Users)
{
    /// <summary>Gets the findings when handles are not looked at: nothing.</summary>
    public static HandleFindings None { get; } = new([], [], []);

    /// <summary>Gets the roots whose users the file system could not list, so what uses them is not known; empty unless set.</summary>
    public IReadOnlyList<string> RootsWithoutUsers { get; init; } = [];

    /// <summary>
    /// Gets the processes with a handle whose name lookup timed out, so what it names is not known; a check that failed at once does
    /// not put its process here. Empty unless set.
    /// </summary>
    public IReadOnlySet<int> TimedOut { get; init; } = new HashSet<int>();

    /// <summary>Gets every process the file system reports as using any root, each once.</summary>
    public IReadOnlyList<int> Using => [.. Users.Select(user => user.Pid).Distinct()];
}

/// <summary>A process the file system reports as using a root.</summary>
/// <param name="Pid">The process.</param>
/// <param name="Root">The root, as the scan was given it.</param>
internal sealed record RootUser(int Pid, string Root);
