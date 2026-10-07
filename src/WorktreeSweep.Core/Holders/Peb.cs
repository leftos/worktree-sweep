using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Windows.Wdk.System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using WdkPInvoke = Windows.Wdk.PInvoke;

namespace WorktreeSweep.Holders;

/// <summary>
/// Reads a process's current folder and command line from its PEB. <c>NtQueryInformationProcess</c> comes from CsWin32; the offsets
/// below are declared by hand, because the Win32 metadata's <c>RTL_USER_PROCESS_PARAMETERS</c> hides <c>CurrentDirectory</c> inside
/// reserved fields and it has no 32-bit <c>PEB32</c> for a WOW64 process.
/// </summary>
internal static class Peb
{
    /// <summary><c>PEB.ProcessParameters</c> on x64.</summary>
    private const int ProcessParameters = 0x20;

    /// <summary><c>RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath</c> on x64.</summary>
    private const int ParamsCurrentDirectory = 0x38;

    /// <summary><c>RTL_USER_PROCESS_PARAMETERS.CommandLine</c> on x64: 16 bytes and ten pointers, then <c>ImagePathName</c>, then this.</summary>
    private const int ParamsCommandLine = 0x70;

    /// <summary><c>PEB32.ProcessParameters</c> of a WOW64 process.</summary>
    private const int Peb32ProcessParameters = 0x10;

    /// <summary><c>RTL_USER_PROCESS_PARAMETERS32.CurrentDirectory.DosPath</c> of a WOW64 process.</summary>
    private const int Params32CurrentDirectory = 0x24;

    /// <summary>A 64-bit <c>UNICODE_STRING</c>: two lengths, padding, and the buffer pointer at offset 8.</summary>
    private const int UnicodeStringSize = 16;

    /// <summary>A 32-bit <c>UNICODE_STRING</c>: two lengths and the buffer pointer at offset 4.</summary>
    private const int UnicodeString32Size = 8;

    /// <summary>Fails unless this is a 64-bit process, the only kind the offsets above are right for.</summary>
    /// <exception cref="PlatformNotSupportedException">This is a 32-bit process.</exception>
    internal static void EnsureSixtyFourBit()
    {
        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("the PEB offsets are for a 64-bit process; run worktree-sweep as a 64-bit process");
        }
    }

    /// <summary>The process's current folder and, for a 64-bit process, its command line.</summary>
    /// <param name="process">A process open with query and memory-read access.</param>
    /// <returns>The strings, or <see langword="null"/> when the current folder cannot be read.</returns>
    internal static Strings? ReadStrings(SafeHandle process)
    {
        if (!Query(process, PROCESSINFOCLASS.ProcessWow64Information, out nuint wow64Peb))
        {
            return null;
        }
        return wow64Peb != 0 ? Wow64Strings(process, wow64Peb) : NativeStrings(process);
    }

    private static unsafe Strings? NativeStrings(SafeHandle process)
    {
        if (!Query(process, PROCESSINFOCLASS.ProcessBasicInformation, out PROCESS_BASIC_INFORMATION basic))
        {
            return null;
        }
        byte[]? peb = ReadMemory(process, (ulong)basic.PebBaseAddress, ProcessParameters + sizeof(ulong));
        if (peb is null)
        {
            return null;
        }
        ulong parametersAddress = BinaryPrimitives.ReadUInt64LittleEndian(peb.AsSpan(ProcessParameters));
        byte[]? parameters = ReadMemory(process, parametersAddress, ParamsCommandLine + UnicodeStringSize);
        if (parameters is null)
        {
            return null;
        }
        string? currentFolder = RemoteString(process, parameters, ParamsCurrentDirectory, sizeof(ulong));
        return currentFolder is null ? null : new Strings(currentFolder, RemoteString(process, parameters, ParamsCommandLine, sizeof(ulong)));
    }

    private static Strings? Wow64Strings(SafeHandle process, nuint wow64Peb)
    {
        byte[]? peb = ReadMemory(process, wow64Peb, Peb32ProcessParameters + sizeof(uint));
        if (peb is null)
        {
            return null;
        }
        uint parametersAddress = BinaryPrimitives.ReadUInt32LittleEndian(peb.AsSpan(Peb32ProcessParameters));
        byte[]? parameters = ReadMemory(process, parametersAddress, Params32CurrentDirectory + UnicodeString32Size);
        if (parameters is null)
        {
            return null;
        }
        string? currentFolder = RemoteString(process, parameters, Params32CurrentDirectory, sizeof(uint));
        return currentFolder is null ? null : new Strings(currentFolder, null);
    }

    /// <summary>A <c>UNICODE_STRING</c> inside <paramref name="header"/> at <paramref name="at"/>, read from the process.</summary>
    /// <param name="process">The process.</param>
    /// <param name="header">The bytes holding the <c>UNICODE_STRING</c>.</param>
    /// <param name="at">Its offset in <paramref name="header"/>.</param>
    /// <param name="pointerSize">The process's pointer size: 8, or 4 for a WOW64 process.</param>
    /// <returns>The string, or <see langword="null"/> when it is empty or cannot be read.</returns>
    private static string? RemoteString(SafeHandle process, byte[] header, int at, int pointerSize)
    {
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(at));
        ulong address =
            pointerSize == sizeof(ulong)
                ? BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(at + sizeof(ulong)))
                : BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(at + sizeof(uint)));
        if (length == 0 || address == 0)
        {
            return null;
        }
        byte[]? bytes = ReadMemory(process, address, length);
        return bytes is null ? null : new string(MemoryMarshal.Cast<byte, char>(bytes));
    }

    private static unsafe byte[]? ReadMemory(SafeHandle process, ulong address, int length)
    {
        byte[] buffer = new byte[length];
        return PInvoke.ReadProcessMemory(process, (void*)address, buffer) ? buffer : null;
    }

    /// <summary>Fills <paramref name="value"/> with one fixed-size piece of process information.</summary>
    private static unsafe bool Query<T>(SafeHandle process, PROCESSINFOCLASS infoClass, out T value)
        where T : unmanaged
    {
        T result = default;
        uint returned = 0;
        using var raw = BorrowedHandle.Of(process);
        NTSTATUS status = WdkPInvoke.NtQueryInformationProcess(raw.Value, infoClass, &result, (uint)sizeof(T), ref returned);
        value = result;
        return status.Value >= 0;
    }

    /// <summary>What a PEB says about the process.</summary>
    /// <param name="CurrentFolder">The current folder, as the process set it.</param>
    /// <param name="CommandLine">The command line, or <see langword="null"/> when it could not be read; never read for a WOW64 process.</param>
    internal sealed record Strings(string CurrentFolder, string? CommandLine);
}
