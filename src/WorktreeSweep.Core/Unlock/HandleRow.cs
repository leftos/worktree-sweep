namespace WorktreeSweep.Unlock;

/// <summary>One open handle from a <c>handle.exe</c> dump.</summary>
/// <param name="Process">The image name of the process holding the handle, such as <c>pwsh.exe</c>.</param>
/// <param name="Pid">The process id.</param>
/// <param name="Kind">The handle type, such as <c>File</c>.</param>
/// <param name="Handle">The handle value.</param>
/// <param name="Name">The object the handle refers to.</param>
public sealed record HandleRow(string Process, int Pid, string Kind, ulong Handle, string Name);
