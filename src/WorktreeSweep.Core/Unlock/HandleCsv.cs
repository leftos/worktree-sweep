using System.Diagnostics;
using System.Globalization;

namespace WorktreeSweep.Unlock;

/// <summary>A parser for the CSV that Sysinternals <c>handle.exe -nobanner -v</c> prints.</summary>
/// <remarks>
/// With a name to match, each row is <c>Process,PID,Type,Handle,Name</c>; without one (every handle on the system) it is
/// <c>Process,PID,User,Handle,Type,Share Flags,Name</c>. The header line says which. The Name is last, not quoted and carries a
/// trailing space, so it is everything after the last separating comma, trimmed.
/// </remarks>
public static class HandleCsv
{
    /// <summary>The line <c>handle.exe</c> prints when nothing matches.</summary>
    public const string NoMatch = "No matching handles found.";

    /// <summary>Parses <c>handle.exe -nobanner -v</c> output, with or without a name to match.</summary>
    /// <param name="text">The dump as printed. The column layout comes from its header line, or from the named layout when none is
    /// seen.</param>
    /// <returns>One row per handle. Blank lines and the no-match line are skipped; a line with too few fields, a bad pid or a bad
    /// handle is skipped with a warning. A bad line never fails the parse.</returns>
    public static IReadOnlyList<HandleRow> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Layout layout = Layout.Named;
        var rows = new List<HandleRow>();
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed == NoMatch)
            {
                continue;
            }
            if (FromHeader(trimmed) is Layout header)
            {
                layout = header;
                continue;
            }
            if (ParseLine(trimmed, layout) is HandleRow row)
            {
                rows.Add(row);
            }
        }
        return rows;
    }

    /// <summary>Groups rows by process, sorted by pid, keeping each process's handles in the order they appeared.</summary>
    /// <param name="rows">The rows to group.</param>
    /// <returns>One locker per process, under the first name seen for its pid.</returns>
    public static IReadOnlyList<Locker> GroupByProcess(IEnumerable<HandleRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return
        [
            .. rows.GroupBy(row => row.Pid)
                .OrderBy(group => group.Key)
                .Select(group => new Locker(
                    group.First().Process,
                    group.Key,
                    [.. group.Select(row => new HeldHandle(row.Handle, row.Kind, row.Name))]
                )),
        ];
    }

    /// <summary>The layout a header line such as <c>Process,PID,User,Handle,Type,Share Flags,Name</c> describes; the titles are
    /// located by name, so the columns may sit in any order.</summary>
    /// <param name="line">The trimmed line.</param>
    /// <returns>The layout, or <see langword="null"/> when the line is not a header.</returns>
    private static Layout? FromHeader(string line)
    {
        string[] columns = [.. line.Split(',').Select(column => column.Trim())];
        if (columns[0] != "Process" || columns[^1] != "Name")
        {
            return null;
        }
        int pid = Array.IndexOf(columns, "PID");
        int kind = Array.IndexOf(columns, "Type");
        int handle = Array.IndexOf(columns, "Handle");
        return pid >= 0 && kind >= 0 && handle >= 0 ? new Layout(columns.Length, pid, kind, handle) : null;
    }

    /// <summary>One row; the Name is everything after the last separating comma the layout needs, so a comma inside it is kept
    /// whole.</summary>
    /// <param name="line">The trimmed line.</param>
    /// <param name="layout">Where the columns of this dump sit.</param>
    /// <returns>The row, or <see langword="null"/> when the line is malformed, with a warning naming it.</returns>
    private static HandleRow? ParseLine(string line, Layout layout)
    {
        string[] fields = line.Split(',', layout.Width);
        if (fields.Length < layout.Width)
        {
            Trace.TraceWarning(
                string.Create(CultureInfo.InvariantCulture, $"skipping a handle.exe line with fewer than {layout.Width} fields: {line}")
            );
            return null;
        }
        if (!int.TryParse(fields[layout.Pid].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
        {
            Trace.TraceWarning($"skipping a handle.exe line with a bad pid: {line}");
            return null;
        }
        if (ParseHex(fields[layout.Handle].Trim()) is not ulong handle)
        {
            Trace.TraceWarning($"skipping a handle.exe line with a bad handle: {line}");
            return null;
        }
        return new HandleRow(fields[0].Trim(), pid, fields[layout.Kind].Trim(), handle, fields[layout.Width - 1].Trim());
    }

    /// <summary>The handle value, which <c>handle.exe</c> prints as hex with a <c>0x</c> or <c>0X</c> prefix the value must
    /// carry.</summary>
    /// <param name="text">The trimmed column.</param>
    /// <returns>The value, or <see langword="null"/> when it is not hex with that prefix.</returns>
    private static ulong? ParseHex(string text)
    {
        string digits = text.StartsWith("0x", StringComparison.Ordinal) || text.StartsWith("0X", StringComparison.Ordinal) ? text[2..] : "";
        return digits.Length > 0 && ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value)
            ? value
            : null;
    }

    /// <summary>Where the columns sit in one layout. Process is always first and Name always last.</summary>
    /// <param name="Width">How many comma-separated columns a row has.</param>
    /// <param name="Pid">The column the process id sits in.</param>
    /// <param name="Kind">The column the handle type sits in.</param>
    /// <param name="Handle">The column the handle value sits in.</param>
    private readonly record struct Layout(int Width, int Pid, int Kind, int Handle)
    {
        /// <summary><c>Process,PID,Type,Handle,Name</c>, printed when <c>handle.exe</c> is given a name to match.</summary>
        public static readonly Layout Named = new(5, 1, 2, 3);
    }
}
