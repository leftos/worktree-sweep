namespace WorktreeSweep.Unlock;

/// <summary>The Sysinternals <c>handle.exe</c> call the unlock session makes.</summary>
public interface IHandleExe
{
    /// <summary>Closes one handle behind its process's back.</summary>
    /// <param name="handle">The handle value.</param>
    /// <param name="pid">The process holding it.</param>
    /// <param name="output">The attempt's standard output, for a failure message.</param>
    /// <returns><see langword="true"/> when <c>handle.exe</c> reported success.</returns>
    /// <exception cref="UnlockException"><c>handle.exe</c> is not on PATH or cannot be run.</exception>
    bool Close(nint handle, int pid, out string output);
}
