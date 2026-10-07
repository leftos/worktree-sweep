namespace WorktreeSweep.Unlock;

/// <summary>One handle a <see cref="Locker"/> holds.</summary>
/// <param name="Handle">The handle value.</param>
/// <param name="Kind">The handle type, such as <c>File</c> or <c>Section</c>.</param>
/// <param name="Name">The object the handle refers to.</param>
public sealed record HeldHandle(ulong Handle, string Kind, string Name);
