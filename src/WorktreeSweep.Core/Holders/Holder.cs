namespace WorktreeSweep.Holders;

/// <summary>A process that holds something inside the folder.</summary>
/// <param name="Pid">The process ID.</param>
/// <param name="Exe">The image name, such as <c>cargo.exe</c>.</param>
/// <param name="Image">The full image path, or <see langword="null"/> when it could not be read.</param>
/// <param name="Started">The creation time as a FILETIME count, or 0 when it could not be read.</param>
/// <param name="CommandLine">The command line, or <see langword="null"/> when it could not be read; never read for a 32-bit process.</param>
/// <param name="Holds">What the process holds inside the folder.</param>
public sealed record Holder(int Pid, string Exe, string? Image, ulong Started, string? CommandLine, IReadOnlyList<Hold> Holds);
