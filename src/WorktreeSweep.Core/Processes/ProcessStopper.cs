using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using WorktreeSweep.Holders;

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
    /// Terminates the process with exit code 1 and waits up to <paramref name="wait"/> for it to exit. The image name and, when
    /// <paramref name="expectedStart"/> is known, the creation time are read through the same handle that terminates it, so a PID
    /// reused by another program is never stopped. A process that has already exited counts as stopped.
    /// </summary>
    /// <param name="pid">The process to stop.</param>
    /// <param name="expectedExe">The image file name the caller saw, such as <c>code.exe</c>; compared case-insensitively.</param>
    /// <param name="expectedStart">The creation time the caller saw, as a <c>FILETIME</c> count, or <see langword="null"/> to
    /// check the image name alone.</param>
    /// <param name="wait">How long to wait for it to exit; at least <see cref="TimeSpan.Zero"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="wait"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">The PID now belongs to another image, or to a process that started at another
    /// time; nothing was stopped.</exception>
    /// <exception cref="Win32Exception">
    /// The process cannot be opened, its image name read or it terminated, or it has not exited within the wait.
    /// </exception>
    public static void Stop(int pid, string expectedExe, ulong? expectedStart, TimeSpan wait)
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
        if (expectedStart is ulong expected)
        {
            ulong? started = ProcessQuery.CreationTime(process);
            if (started != expected)
            {
                throw new InvalidOperationException(NotSeenMessage(pid, started, expected));
            }
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

    /// <summary>The message for a PID that no longer names the process the caller saw.</summary>
    /// <param name="pid">The process.</param>
    /// <param name="actual">The creation time read from the open handle, or <see langword="null"/> when it could not be read.</param>
    /// <param name="expected">The creation time the caller saw.</param>
    /// <returns>The message.</returns>
    private static string NotSeenMessage(int pid, ulong? actual, ulong expected) =>
        $"process {pid} is not the process that was seen (started {StartedText(actual)}, expected {StartedText(expected)}); not stopped";

    /// <summary>A creation time as text, or <c>unknown</c> when it could not be read.</summary>
    /// <param name="started">The creation time, or <see langword="null"/>.</param>
    /// <returns>The text.</returns>
    private static string StartedText(ulong? started) => started is ulong value ? value.ToString(CultureInfo.InvariantCulture) : "unknown";

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
