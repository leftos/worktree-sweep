using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace WorktreeSweep.Unlock;

/// <summary>
/// The file the elevated unlock session writes its exit code to, so the unelevated side learns the verdict even when the
/// session's process cannot finish exiting.
/// </summary>
public static class UnlockResultFile
{
    /// <summary>A path under the user's temporary folder, unique to one run.</summary>
    /// <returns>The path; no file is created.</returns>
    public static string NewPath() => Path.Join(Path.GetTempPath(), $"worktree-sweep-unlock-{Guid.NewGuid():N}.txt");

    /// <summary>
    /// Writes <paramref name="code"/> as invariant text followed by a newline, creating the file and never replacing one that
    /// already exists: the elevated session writes into a folder the user can write, so anything planted at the path, a symbolic
    /// link or junction included, fails the create instead of being followed. A failed write leaves nothing behind.
    /// </summary>
    /// <param name="path">Where to write.</param>
    /// <param name="code">The exit code.</param>
    /// <exception cref="IOException">The file cannot be created, including when anything already has that name.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public static void Write(string path, int code)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(Encoding.UTF8.GetBytes($"{code.ToString(CultureInfo.InvariantCulture)}\n"));
        file.Flush();
    }

    /// <summary>Writes the code, tracing a failure as a warning instead of throwing it.</summary>
    /// <param name="path">Where to write.</param>
    /// <param name="code">The exit code.</param>
    public static void WriteTraced(string path, int code)
    {
        try
        {
            Write(path, code);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"cannot write the unlock result to {path}: {error.Message}");
        }
    }

    /// <summary>Reads the exit code, once the file holds a complete line.</summary>
    /// <param name="path">Where the code goes.</param>
    /// <returns>The code, or <see langword="null"/> when the file is absent, unreadable, or does not end with a newline after an
    /// integer.</returns>
    public static int? TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            string text = File.ReadAllText(path);
            bool complete = text.EndsWith('\n');
            return complete && int.TryParse(text[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) ? code : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes the file, tracing a failure as a warning instead of throwing it.</summary>
    /// <param name="path">The file to delete.</param>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"cannot delete the unlock result at {path}: {error.Message}");
        }
    }
}
