namespace WorktreeSweep.Holders;

/// <summary>One process from a snapshot, with its creation time.</summary>
/// <param name="Parent">The parent process ID.</param>
/// <param name="Exe">The image name.</param>
/// <param name="Started">The creation time as a FILETIME count, or <see langword="null"/> when it could not be read.</param>
public sealed record TimedProcess(int Parent, string Exe, ulong? Started);
