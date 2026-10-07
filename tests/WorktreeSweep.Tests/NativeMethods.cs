using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WorktreeSweep.Tests;

/// <summary>
/// The Win32 path-name calls the fixtures need. Declared by hand with <see cref="LibraryImportAttribute"/> because the test
/// project has no CsWin32 source generator.
/// </summary>
internal static partial class NativeMethods
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;

    /// <summary>The path with 8.3 short names expanded, or as given when it does not exist.</summary>
    /// <param name="path">An absolute path; <c>/</c> and <c>\</c> both separate.</param>
    /// <returns>The long form of the path.</returns>
    /// <exception cref="Win32Exception">The call fails for a reason other than a missing path.</exception>
    public static string LongPath(string path) => Expand(path.Replace('/', '\\'), GetLongPathNameW);

    /// <summary>The path with each component in its 8.3 short form, or as given when the volume has no short names.</summary>
    /// <param name="path">An existing absolute path.</param>
    /// <returns>The short form of the path.</returns>
    /// <exception cref="Win32Exception">The call fails.</exception>
    public static string ShortPath(string path) => Expand(path, GetShortPathNameW);

    /// <summary>Opens a volume, such as <c>\\.\X:</c>, with no access and full sharing, which needs no elevation.</summary>
    /// <param name="volume">The volume's device path.</param>
    /// <returns>The open volume.</returns>
    /// <exception cref="Win32Exception">The volume cannot be opened.</exception>
    public static SafeFileHandle OpenVolume(string volume)
    {
        SafeFileHandle handle = CreateFileW(volume, 0, FileShareAll, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, $"cannot open {volume}");
        }
        return handle;
    }

    private static string Expand(string path, Func<string, char[], uint, uint> call)
    {
        char[] buffer = new char[260];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint length = call(path, buffer, (uint)buffer.Length);
            if (length == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                return error is ErrorFileNotFound or ErrorPathNotFound ? path : throw new Win32Exception(error, $"cannot expand {path}");
            }
            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }
            buffer = new char[length];
        }
        throw new Win32Exception($"the expanded form of {path} kept growing");
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint bufferLength);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint bufferLength);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile
    );
}
