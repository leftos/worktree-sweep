using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WorktreeSweep.Tests;

/// <summary>
/// The Win32 path-name calls the fixtures need. Declared by hand with <see cref="LibraryImportAttribute"/> because the test
/// project has no CsWin32 source generator.
/// </summary>
internal static partial class NativeMethods
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

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
}
