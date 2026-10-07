using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace WorktreeSweep.Holders;

/// <summary>What the holder finder reads about a process.</summary>
internal static class ProcessQuery
{
    /// <summary>Room for a <c>TOKEN_USER</c> and the SID it points to; a SID is at most 68 bytes.</summary>
    private const int TokenUserSize = 512;

    /// <summary>The first path buffer size tried, in characters; a longer path grows it once.</summary>
    private const int PathBufferSize = 1024;

    /// <summary>The longest Win32 path in characters.</summary>
    private const int LongestPath = 32_768;

    /// <summary>Opens a process.</summary>
    /// <param name="pid">The process.</param>
    /// <param name="access">The access wanted.</param>
    /// <returns>The open process, or <see langword="null"/> when it cannot be opened with that access.</returns>
    internal static SafeFileHandle? Open(int pid, PROCESS_ACCESS_RIGHTS access)
    {
        SafeFileHandle process = PInvoke.OpenProcess_SafeHandle(access, false, (uint)pid);
        if (process.IsInvalid)
        {
            process.Dispose();
            return null;
        }
        return process;
    }

    /// <summary>This process's user.</summary>
    /// <returns>Its <c>TOKEN_USER</c>, as <see cref="TokenUser"/> returns it.</returns>
    /// <exception cref="Win32Exception">The user cannot be read.</exception>
    internal static byte[] CurrentUser()
    {
        using SafeFileHandle own = PInvoke.GetCurrentProcess_SafeHandle();
        return ReadTokenUser(own, out int error) ?? throw new Win32Exception(error, "cannot read this process's user");
    }

    /// <summary>
    /// The process's <c>TOKEN_USER</c>, in a pinned buffer: the SID pointer inside it points into the same buffer, so the buffer must
    /// never move.
    /// </summary>
    /// <param name="process">A process open with query access.</param>
    /// <returns>The buffer, or <see langword="null"/> when the token cannot be read.</returns>
    internal static byte[]? TokenUser(SafeHandle process) => ReadTokenUser(process, out _);

    /// <summary>Whether two <c>TOKEN_USER</c> buffers from <see cref="TokenUser"/> name the same user.</summary>
    /// <param name="first">One buffer.</param>
    /// <param name="second">The other buffer.</param>
    /// <returns><see langword="true"/> when their SIDs are equal.</returns>
    internal static bool SameUser(byte[] first, byte[] second) =>
        PInvoke.EqualSid(MemoryMarshal.Read<TOKEN_USER>(first).User.Sid, MemoryMarshal.Read<TOKEN_USER>(second).User.Sid);

    /// <summary>The process's full image path.</summary>
    /// <param name="process">A process open with query access.</param>
    /// <returns>The path, or <see langword="null"/> when it cannot be read.</returns>
    internal static string? ImagePath(SafeHandle process)
    {
        foreach (int capacity in (int[])[PathBufferSize, LongestPath])
        {
            char[] buffer = new char[capacity];
            uint length = (uint)capacity;
            if (PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length))
            {
                return new string(buffer, 0, (int)length);
            }
            if (Marshal.GetLastPInvokeError() != (int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// The process's <c>TOKEN_USER</c>, as <see cref="TokenUser"/> describes, with the Win32 error of the call that failed, read
    /// before the token handle is closed.
    /// </summary>
    private static byte[]? ReadTokenUser(SafeHandle process, out int error)
    {
        if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, out SafeFileHandle token))
        {
            error = Marshal.GetLastPInvokeError();
            token.Dispose();
            return null;
        }
        using (token)
        {
            byte[] buffer = GC.AllocateArray<byte>(TokenUserSize, pinned: true);
            bool read = PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, buffer, out _);
            error = read ? 0 : Marshal.GetLastPInvokeError();
            return read ? buffer : null;
        }
    }

    /// <summary>The process's creation time as a FILETIME count.</summary>
    /// <param name="process">A process open with query access.</param>
    /// <returns>The count, or <see langword="null"/> when it cannot be read or is 0.</returns>
    internal static ulong? CreationTime(SafeHandle process)
    {
        if (!PInvoke.GetProcessTimes(process, out FILETIME created, out _, out _, out _))
        {
            return null;
        }
        ulong started = ((ulong)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
        return started == 0 ? null : started;
    }
}
