using System.Text;

namespace WorktreeSweep;

/// <summary>The text encoding for one of the tool's standard streams: UTF-8 when it is redirected, UTF-16 for a console.</summary>
public static class ConsoleEncodings
{
    /// <summary>
    /// The text encoding for a standard stream: UTF-8 when it is redirected, UTF-16 for <c>WriteConsoleW</c> when it is a
    /// console; neither writes a BOM.
    /// </summary>
    /// <param name="redirected">Whether the stream goes to a file or pipe rather than a console.</param>
    /// <returns>The encoding.</returns>
    public static Encoding For(bool redirected) =>
        redirected ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) : new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
}
