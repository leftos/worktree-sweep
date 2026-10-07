namespace WorktreeSweep.Processes;

/// <summary>One process from a process snapshot.</summary>
/// <param name="Parent">The parent process ID.</param>
/// <param name="Exe">The image name, such as <c>sudo.exe</c>.</param>
public sealed record ProcessEntry(int Parent, string Exe);
