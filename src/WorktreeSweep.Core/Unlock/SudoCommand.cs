using System.Globalization;

namespace WorktreeSweep.Unlock;

/// <summary>The elevated <c>unlock</c> command, and the hint to run it by hand when it cannot be run for the user.</summary>
public static class SudoCommand
{
    /// <summary>The arguments to give <c>sudo</c>: <c>&lt;exe&gt; unlock [--caller-pid &lt;pid&gt;] --sweep-pid &lt;pid&gt;
    /// &lt;paths&gt;</c>, every path with <c>\</c> separators.</summary>
    /// <param name="exe">The path of this program, as the elevated side must run it.</param>
    /// <param name="callerPid">The process that started this one, or <see langword="null"/> when it is not known.</param>
    /// <param name="sweepPid">The unelevated worktree-sweep, which the elevated side must never offer to stop.</param>
    /// <param name="paths">The locked paths.</param>
    /// <returns>The arguments, the program first.</returns>
    public static IReadOnlyList<string> Argv(string exe, int? callerPid, int sweepPid, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(exe);
        ArgumentNullException.ThrowIfNull(paths);
        var argv = new List<string> { exe, "unlock" };
        if (callerPid is int pid)
        {
            argv.Add("--caller-pid");
            argv.Add(pid.ToString(CultureInfo.InvariantCulture));
        }
        argv.Add("--sweep-pid");
        argv.Add(sweepPid.ToString(CultureInfo.InvariantCulture));
        argv.AddRange(paths.Select(Backslashed));
        return argv;
    }

    /// <summary>The command to run by hand in an administrator terminal.</summary>
    /// <param name="exe">The path of this program.</param>
    /// <param name="paths">The locked paths.</param>
    /// <returns><c>"&lt;exe&gt;" unlock "&lt;p1&gt;" "&lt;p2&gt;"</c>, with the paths backslashed and each in quotes, and no pid
    /// flags. A path's trailing separator is dropped, so a quoted path never ends in <c>\"</c>; a drive root such as
    /// <c>D:\</c> keeps it.</returns>
    public static string Manual(string exe, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(exe);
        ArgumentNullException.ThrowIfNull(paths);
        string quoted = string.Join(' ', paths.Select(QuotedPath));
        return $"\"{exe}\" unlock {quoted}";
    }

    /// <summary>Writes the two-line hint that names the administrator terminal and the command to run there.</summary>
    /// <param name="writer">Where the hint goes.</param>
    /// <param name="exe">The path of this program.</param>
    /// <param name="paths">The locked paths.</param>
    public static void WriteManual(TextWriter writer, string exe, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine("To find and clear the locks, run this in an administrator terminal, then run worktree-sweep again:");
        writer.WriteLine($"  {Manual(exe, paths)}");
    }

    /// <summary>The path in quotes, with every <c>/</c> turned into <c>\</c> and any trailing separator dropped, so the
    /// closing quote is never escaped.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The quoted path, such as <c>"D:\a.wt\x"</c>.</returns>
    private static string QuotedPath(string path)
    {
        string windows = Backslashed(path);
        string trimmed = IsDriveRoot(windows) ? windows : windows.TrimEnd('\\');
        return $"\"{trimmed}\"";
    }

    /// <summary>The path with every <c>/</c> turned into <c>\</c>.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The path as Windows spells it.</returns>
    private static string Backslashed(string path) => path.Replace('/', '\\');

    /// <summary>Whether the path is a drive root such as <c>D:\</c>, whose one separator is the whole path.</summary>
    /// <param name="path">The path, already backslashed.</param>
    /// <returns><see langword="true"/> for a drive root.</returns>
    private static bool IsDriveRoot(string path) => path.Length == 3 && path[1] == ':' && path[2] == '\\';
}
