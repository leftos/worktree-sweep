namespace WorktreeSweep.Unlock;

/// <summary>One process and the handles it holds.</summary>
/// <param name="Process">The image name of the process.</param>
/// <param name="Pid">The process id.</param>
/// <param name="Handles">The file handles it holds under the locked folders, sorted by name.</param>
/// <param name="Started">The process's creation time as a <c>FILETIME</c> count, or <see langword="null"/> when it could not be
/// read; it tells a live PID from a newer process that has taken it.</param>
public sealed record Locker(string Process, int Pid, IReadOnlyList<HeldHandle> Handles, ulong? Started);
