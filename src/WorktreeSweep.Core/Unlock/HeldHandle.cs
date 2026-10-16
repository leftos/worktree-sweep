namespace WorktreeSweep.Unlock;

/// <summary>One file handle a <see cref="Locker"/> holds.</summary>
/// <param name="Handle">The handle value, meaningful only with its locker's PID and start time.</param>
/// <param name="Name">The file the handle refers to, without a verbatim drive prefix.</param>
public sealed record HeldHandle(nint Handle, string Name);
