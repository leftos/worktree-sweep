namespace WorktreeSweep.Unlock;

/// <summary>The Sysinternals <c>handle.exe</c> calls the unlock session makes.</summary>
public interface IHandleExe
{
    /// <summary>Lists every open handle on the system, as <c>handle.exe -nobanner -accepteula -v</c> prints them.</summary>
    /// <returns>The dump.</returns>
    /// <exception cref="UnlockException"><c>handle.exe</c> is not on PATH, cannot be run, or failed.</exception>
    string Dump();

    /// <summary>Lists one process's open handles, as <c>handle.exe -nobanner -accepteula -v -p &lt;pid&gt;</c> prints them.</summary>
    /// <param name="pid">The process whose handles are listed.</param>
    /// <returns>The dump.</returns>
    /// <exception cref="UnlockException"><c>handle.exe</c> is not on PATH or cannot be run.</exception>
    string DumpProcess(int pid);

    /// <summary>Closes one handle behind its process's back.</summary>
    /// <param name="handle">The handle value.</param>
    /// <param name="pid">The process holding it.</param>
    /// <param name="output">The attempt's standard output, for a failure message.</param>
    /// <returns><see langword="true"/> when <c>handle.exe</c> reported success.</returns>
    /// <exception cref="UnlockException"><c>handle.exe</c> is not on PATH or cannot be run.</exception>
    bool Close(ulong handle, int pid, out string output);
}
