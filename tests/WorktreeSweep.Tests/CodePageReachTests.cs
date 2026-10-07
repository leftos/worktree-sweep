using System.Text;
using Windows.Win32;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>
/// Which of a machine's code pages can name a path: <c>handle.exe</c> prints a path in the console code pages only, so a folder
/// whose path they cannot name is one the elevated session must never report clear.
/// </summary>
public sealed class CodePageReachTests
{
    /// <summary>A path whose Greek letters are in neither the ANSI nor the OEM code page cannot be named.</summary>
    [Fact]
    public void GreekFolderIsNotNameableIn1252And437() => Assert.False(CodePageReach.CanName(@"X:\tmp\δοκιμή\αρχείο.txt", CodePages(1252, 437)));

    /// <summary>An accented Latin letter is in both code pages, so the path round-trips through each.</summary>
    [Fact]
    public void AccentedLatinIsNameableIn1252And437() => Assert.True(CodePageReach.CanName(@"X:\tmp\café\x.txt", CodePages(1252, 437)));

    /// <summary>The euro sign is in the ANSI code page but not the OEM one, so the path cannot be named.</summary>
    [Fact]
    public void EuroSignIsNotNameableIn437() => Assert.False(CodePageReach.CanName(@"X:\tmp\€\x", CodePages(1252, 437)));

    /// <summary>An ASCII path round-trips through every code page.</summary>
    [Fact]
    public void AsciiPathIsNameable() => Assert.True(CodePageReach.CanName(@"X:\tmp\plain\x.txt", CodePages(1252, 437)));

    /// <summary>The machine's own code pages are its ANSI and OEM ones, in that order.</summary>
    [Fact]
    public void SystemCodePagesAreAnsiAndOem()
    {
        IReadOnlyList<Encoding> pages = CodePageReach.SystemCodePages();

        Assert.Equal(2, pages.Count);
        Assert.Equal((int)PInvoke.GetACP(), pages[0].CodePage);
        Assert.Equal((int)PInvoke.GetOEMCP(), pages[1].CodePage);
    }

    /// <summary>
    /// The encodings named by <paramref name="codePages"/>, registering the code page provider first: .NET ships the Unicode
    /// encodings only, and the tests name code pages explicitly so they do not depend on this machine's own.
    /// </summary>
    /// <param name="codePages">The code page numbers, in the order they are returned.</param>
    /// <returns>The encodings.</returns>
    private static IReadOnlyList<Encoding> CodePages(params int[] codePages)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return [.. codePages.Select(Encoding.GetEncoding)];
    }
}
