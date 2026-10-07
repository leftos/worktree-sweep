namespace WorktreeSweep.Unlock;

/// <summary>One process and the handles it holds.</summary>
/// <param name="Process">The image name of the process.</param>
/// <param name="Pid">The process id.</param>
/// <param name="Handles">The handles, in the order <c>handle.exe</c> listed them.</param>
public sealed record Locker(string Process, int Pid, IReadOnlyList<HeldHandle> Handles);
