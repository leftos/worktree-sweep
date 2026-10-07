namespace WorktreeSweep.Holders;

/// <summary>A process that may hold the folder, but could not be fully inspected.</summary>
/// <param name="Pid">The process ID.</param>
/// <param name="Exe">The image name.</param>
/// <param name="Why">Why it could not be ruled out.</param>
public sealed record MayHold(int Pid, string Exe, MayHoldWhy Why);

/// <summary>Why a process may hold the folder.</summary>
public enum MayHoldWhy
{
    /// <summary>One of its disk handles could not be named in time.</summary>
    UnnamedHandle,

    /// <summary>It uses the folder, but it cannot be opened to see how.</summary>
    CannotOpen,
}
