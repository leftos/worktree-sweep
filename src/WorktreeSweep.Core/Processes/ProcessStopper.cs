using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace WorktreeSweep.Processes;

/// <summary>Stops a process by PID, after checking it is still the program the caller saw, and waits for it to exit.</summary>
public static class ProcessStopper
{
    /// <summary>The exit code a stopped process is given.</summary>
    private const uint StoppedExitCode = 1;

    /// <summary>The longest wait in milliseconds; one less than <c>INFINITE</c>, so a huge wait still ends.</summary>
    private const uint LongestWaitMillis = uint.MaxValue - 1;

    /// <summary>The longest Win32 path in characters, the size of the image name buffer.</summary>
    private const int LongestPath = 32_768;

    /// <summary>Gets how long <see cref="Stop"/> waits for a stopped process to exit, unless told otherwise.</summary>
    public static TimeSpan DefaultWait { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Terminates the process with exit code 1 and waits up to <paramref name="wait"/> for it to exit. The image name is read
    /// through the same handle that terminates it, so a PID reused by another program is never stopped. A process that has
    /// already exited counts as stopped.
    /// </summary>
    /// <param name="pid">The process to stop.</param>
    /// <param name="expectedExe">The image file name the caller saw, such as <c>code.exe</c>; compared case-insensitively.</param>
    /// <param name="wait">How long to wait for it to exit; at least <see cref="TimeSpan.Zero"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="wait"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">The PID now belongs to another image; nothing was stopped.</exception>
    /// <exception cref="Win32Exception">
    /// The process cannot be opened, its image name read or it terminated, or it has not exited within the wait.
    /// </exception>
    public static void Stop(int pid, string expectedExe, TimeSpan wait)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero);
        using SafeFileHandle process = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE
                | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE
                | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            (uint)pid
        );
        if (process.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"cannot open process {pid}");
        }
        if (!TryImageName(process, out string? image))
        {
            int error = Marshal.GetLastPInvokeError();
            if (HasExited(process))
            {
                return;
            }
            throw new Win32Exception(error, $"cannot read the image name of process {pid}");
        }
        string actual = Path.GetFileName(image);
        if (!actual.Equals(expectedExe, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"process {pid} is now {actual}, not {expectedExe}; not stopped");
        }
        if (!PInvoke.TerminateProcess(process, StoppedExitCode))
        {
            int error = Marshal.GetLastPInvokeError();
            if (HasExited(process))
            {
                return;
            }
            throw new Win32Exception(error, $"cannot stop process {pid}");
        }
        WaitForExit(process, pid, wait);
    }

    private static bool TryImageName(SafeFileHandle process, [NotNullWhen(true)] out string? image)
    {
        char[] buffer = new char[LongestPath];
        uint length = (uint)buffer.Length;
        if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length))
        {
            image = null;
            return false;
        }
        image = new string(buffer, 0, (int)length);
        return true;
    }

    private static bool HasExited(SafeFileHandle process) => PInvoke.WaitForSingleObject(process, 0) == WAIT_EVENT.WAIT_OBJECT_0;

    private static void WaitForExit(SafeFileHandle process, int pid, TimeSpan wait)
    {
        uint millis = (uint)Math.Min(wait.TotalMilliseconds, LongestWaitMillis);
        WAIT_EVENT waited = PInvoke.WaitForSingleObject(process, millis);
        if (waited == WAIT_EVENT.WAIT_FAILED)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"cannot wait for process {pid} to exit");
        }
        if (waited != WAIT_EVENT.WAIT_OBJECT_0)
        {
            throw new Win32Exception((int)WAIT_EVENT.WAIT_TIMEOUT, $"process {pid} did not exit within {wait.TotalSeconds} s of being stopped");
        }
    }
}
