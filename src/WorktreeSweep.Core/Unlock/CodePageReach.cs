using System.Text;
using Windows.Win32;

namespace WorktreeSweep.Unlock;

/// <summary>
/// Which of a machine's code pages can name a path. <c>handle.exe</c> prints paths in the console code pages only, so a locked
/// folder whose path holds a character outside them comes back as a row of <c>?</c> and matches nothing; a path this says cannot
/// be named is one the elevated session must never report clear.
/// </summary>
public static class CodePageReach
{
    /// <summary>
    /// Registers the code page encodings with .NET, which ships the Unicode encodings only, so a code page such as 437 can be
    /// looked up.
    /// </summary>
    static CodePageReach() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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

    /// <summary>This machine's ANSI and OEM code pages, the two <c>handle.exe</c> prints paths in.</summary>
    /// <returns>The ANSI code page, then the OEM one.</returns>
    public static IReadOnlyList<Encoding> SystemCodePages() =>
        [Encoding.GetEncoding((int)PInvoke.GetACP()), Encoding.GetEncoding((int)PInvoke.GetOEMCP())];
}
