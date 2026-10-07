using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The parser for the CSV that <c>handle.exe -nobanner -v</c> prints.</summary>
public sealed class HandleCsvTests
{
    /// <summary>A dump taken with no name to match takes its layout from the header, and the Name keeps its commas.</summary>
    [Fact]
    public void UnfilteredDumpLayoutIsParsed()
    {
        IReadOnlyList<HandleRow> rows = HandleCsv.Parse(Fixture("handle-dump-sample.csv"));

        Assert.Equal(5, rows.Count);
        Assert.Equal(new HandleRow("winlogon.exe", 1720, "File", 0x54, @"C:\Windows\System32"), rows[4]);
        Assert.Equal(new HandleRow("GameInputRedistService.exe", 7036, "File", 0x58, @"C:\Windows\System32"), rows[0]);
        Assert.Equal("Section", rows[1].Kind);
        Assert.Equal(0x444ul, rows[1].Handle);
        Assert.Equal(@"\Sessions\1\BaseNamedObjects\windows_shell_global_counters", rows[1].Name);
        Assert.Equal(@"D:\example\repo.wt\feature\notes, draft.txt", rows[3].Name);

        Assert.Equal<int[]>([1720, 7036, 9620, 35952], [.. HandleCsv.GroupByProcess(rows).Select(locker => locker.Pid)]);
    }

    /// <summary>The recorded no-name dump parses into its two rows, grouped into the one locker that holds them.</summary>
    [Fact]
    public void ParsesTheRecordedFixture()
    {
        IReadOnlyList<HandleRow> rows = HandleCsv.Parse(Fixture("handle-sample.csv"));

        Assert.Equal(2, rows.Count);
        Assert.Equal(new HandleRow("pwsh.exe", 64336, "File", 0x54, @"D:\worktree-sweep\.tmp\lockprobe"), rows[0]);
        Assert.Equal(0x748ul, rows[1].Handle);
        Assert.Equal(@"D:\worktree-sweep\.tmp\lockprobe\held.txt", rows[1].Name);

        Locker locker = Assert.Single(HandleCsv.GroupByProcess(rows));
        Assert.Equal(64336, locker.Pid);
        Assert.Equal(2, locker.Handles.Count);
    }

    /// <summary>The line handle.exe prints when nothing matches is no row.</summary>
    [Fact]
    public void NoMatchLineIsEmpty() => Assert.Empty(HandleCsv.Parse("No matching handles found.\r\n"));

    /// <summary>A handle whose digits are separated from their <c>0x</c> prefix, as in <c>0x 54</c>, is no handle.</summary>
    [Fact]
    public void SpacedHandleValueIsSkipped() => Assert.Empty(HandleCsv.Parse("x.exe,7,File,0x 54,D:\\a \n"));

    /// <summary>Blank input, and input of blank lines, is no row.</summary>
    [Fact]
    public void EmptyInputIsEmpty()
    {
        Assert.Empty(HandleCsv.Parse(""));
        Assert.Empty(HandleCsv.Parse("\n\n"));
    }

    /// <summary>A line with too few fields, a bad pid or a bad handle is skipped, and the rows around it are kept.</summary>
    [Fact]
    public void MalformedLineInTheMiddleIsSkipped()
    {
        const string Text =
            "Process,PID,Type,Handle,Name\n"
            + "a.exe,10,File,0x10,D:\\a \n"
            + "garbage line\n"
            + "b.exe,notapid,File,0x11,D:\\b \n"
            + "c.exe,12,File,zz,D:\\c \n"
            + "d.exe,13,File,0x13,D:\\d \n";

        IReadOnlyList<HandleRow> rows = HandleCsv.Parse(Text);

        Assert.Equal<int[]>([10, 13], [.. rows.Select(row => row.Pid)]);
    }

    /// <summary>A comma inside the Name does not split it: the Name is everything after the last separating comma it needs.</summary>
    [Fact]
    public void NameContainingACommaIsKeptWhole()
    {
        IReadOnlyList<HandleRow> rows = HandleCsv.Parse("x.exe,7,File,0x1F,D:\\a,b\\c, d.txt \n");

        HandleRow row = Assert.Single(rows);
        Assert.Equal(@"D:\a,b\c, d.txt", row.Name);
    }

    /// <summary>Each line loses its CRLF ending, and the lockers come out sorted by pid.</summary>
    [Fact]
    public void CrlfLineEndingsAreTrimmed()
    {
        IReadOnlyList<HandleRow> rows = HandleCsv.Parse("Process,PID,Type,Handle,Name\r\nx.exe,7,File,0x1,D:\\a \r\ny.exe,3,File,0x2,D:\\b\r\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(@"D:\a", rows[0].Name);
        Assert.Equal(@"D:\b", rows[1].Name);
        Assert.Equal<int[]>([3, 7], [.. HandleCsv.GroupByProcess(rows).Select(locker => locker.Pid)]);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Join(AppContext.BaseDirectory, "fixtures", name));
}
