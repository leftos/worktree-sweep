using System.IO;
using System.Text;

namespace WorktreeSweep;

/// <summary>
/// The encoding and setup of the tool's standard output and error: UTF-8 when a stream is redirected, UTF-16 through
/// <c>WriteConsoleW</c> when it is a console. It owns the writers those streams are given.
/// </summary>
public static class ConsoleEncodings
{
    /// <summary>
    /// Installs the tool's standard output and error writers: UTF-16 through <c>WriteConsoleW</c> for a stream that is a
    /// console, UTF-8 without a BOM for a redirected one, both with <c>\n</c> line ends and flushing every write. Call it
    /// first, before anything opens or writes a standard stream.
    /// </summary>
    public static void InstallStandardWriters()
    {
        bool outRedirected = Console.IsOutputRedirected;
        bool errorRedirected = Console.IsErrorRedirected;
        if (!outRedirected || !errorRedirected)
        {
            // Code page 1200 makes .NET write a console through WriteConsoleW without calling SetConsoleOutputCP,
            // so the shell keeps its code page.
            Console.OutputEncoding = Encoding.Unicode;
        }
        Console.SetOut(StandardWriter(Console.OpenStandardOutput(), outRedirected));
        Console.SetError(StandardWriter(Console.OpenStandardError(), errorRedirected));
    }

    /// <summary>
    /// The text encoding for a standard stream: UTF-8 when it is redirected, UTF-16 for <c>WriteConsoleW</c> when it is a
    /// console; neither writes a BOM.
    /// </summary>
    /// <param name="redirected">Whether the stream goes to a file or pipe rather than a console.</param>
    /// <returns>The encoding.</returns>
    public static Encoding For(bool redirected) =>
        redirected ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) : new UnicodeEncoding(bigEndian: false, byteOrderMark: false);

    /// <summary>
    /// A writer over a standard stream in <see cref="For"/>'s encoding, with <c>\n</c> line ends, flushing every write.
    /// </summary>
    private static StreamWriter StandardWriter(Stream stream, bool redirected) => new(stream, For(redirected)) { NewLine = "\n", AutoFlush = true };
}
