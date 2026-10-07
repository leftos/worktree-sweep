using System.Text;
using Windows.Win32;

namespace WorktreeSweep.Unlock;

/// <summary>
/// Which of a machine's code pages can name a path. <c>handle.exe</c> prints paths in the console code pages only, so a locked
/// folder whose path holds a character outside them comes back as a row of <c>?</c> and matches nothing; a path this says cannot
/// be named is one the elevated session must never report clear. A caller registers the code page encodings with
/// <see cref="RegisterProvider"/> before asking for <see cref="SystemCodePages"/>.
/// </summary>
public static class CodePageReach
{
    /// <summary>
    /// Registers the code page encodings with .NET, which ships the Unicode encodings only, so a code page such as 437 can be
    /// looked up. It is idempotent, and with it registered the runtime decodes a child process's output with the console code
    /// page whatever the order of the calls, so an elevated run registers it before it starts <c>handle.exe</c>.
    /// </summary>
    public static void RegisterProvider() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Whether every code page can name <paramref name="path"/>: its bytes in that code page decode back to it
    /// unchanged.</summary>
    /// <param name="path">The path.</param>
    /// <param name="codePages">The encodings the path must round-trip through.</param>
    /// <returns><see langword="true"/> when every code page round-trips the path unchanged.</returns>
    public static bool CanName(string path, IReadOnlyList<Encoding> codePages)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(codePages);
        foreach (Encoding codePage in codePages)
        {
            if (!string.Equals(codePage.GetString(codePage.GetBytes(path)), path, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>This machine's ANSI, OEM and console output code pages, without repeats: the three <c>handle.exe</c> prints
    /// paths in.</summary>
    /// <returns>The encodings, in code page order, with a repeat dropped.</returns>
    /// <exception cref="InvalidOperationException">The code page provider was not registered.</exception>
    public static IReadOnlyList<Encoding> SystemCodePages() =>
        [
            .. new[] { (int)PInvoke.GetACP(), (int)PInvoke.GetOEMCP(), (int)PInvoke.GetConsoleOutputCP() }
                .Select(CodePage)
                .DistinctBy(page => page.CodePage),
        ];

    /// <summary>The encoding for one code page, blaming a missing registration when .NET does not carry it.</summary>
    /// <param name="codePage">The code page number.</param>
    /// <returns>The encoding.</returns>
    /// <exception cref="InvalidOperationException">The code page provider was not registered.</exception>
    private static Encoding CodePage(int codePage)
    {
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (NotSupportedException error)
        {
            throw new InvalidOperationException(
                $"code page {codePage} is not available; call {nameof(CodePageReach)}.{nameof(RegisterProvider)} before it",
                error
            );
        }
    }
}
