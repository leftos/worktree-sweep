using System.Diagnostics;
using System.Text;

namespace WorktreeSweep.Tests;

/// <summary>
/// The recent folders file, kept under <c>%TEMP%</c>, and the lines the Choose screen shows for it. In the process environment
/// collection because the corrupt-file test listens on the process-wide <see cref="Trace.Listeners"/>.
/// </summary>
[Collection(ProcessEnvironment.Name)]
public sealed class RecentFoldersTests : IDisposable
{
    private const long Now = 1_800_000_000;
    private const long Day = 24 * 60 * 60;

    /// <summary>The folder the test's file is kept in.</summary>
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"wts-recent-{Guid.NewGuid():N}");

    /// <summary>The test's recent folders file, in a folder that does not exist until the first write.</summary>
    private string File => Path.Combine(folder, "recent.json");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>What is recorded is read back from the file, most recent first; a missing file reads as none.</summary>
    [Fact]
    public void RoundTrip()
    {
        var recent = RecentFolders.Load(File);
        Assert.Empty(recent.Entries);
        var older = new RecentFolder(@"D:\src", Now - Day, 3, 1024);
        var newer = new RecentFolder(@"X:\dev", Now, 14, 9 * 1024L * 1024 * 1024);

        recent.Record(older);
        recent.Record(newer);

        Assert.Equal([newer, older], RecentFolders.Load(File).Entries);
    }

    /// <summary>At most five folders are kept: a sixth drops the oldest.</summary>
    [Fact]
    public void KeepsAtMostFive()
    {
        var recent = RecentFolders.Load(File);

        for (int index = 1; index <= 6; index++)
        {
            recent.Record(new RecentFolder($@"D:\f{index}", Now + index, index, index));
        }

        Assert.Equal([@"D:\f6", @"D:\f5", @"D:\f4", @"D:\f3", @"D:\f2"], RecentFolders.Load(File).Entries.Select(entry => entry.Folder));
    }

    /// <summary>Scanning a folder again moves it to the top, matching its path case-insensitively, without a second entry.</summary>
    [Fact]
    public void SameFolderMovesToTheTop()
    {
        var recent = RecentFolders.Load(File);
        recent.Record(new RecentFolder(@"X:\dev", Now - (2 * Day), 1, 1));
        recent.Record(new RecentFolder(@"D:\src", Now - Day, 1, 1));

        recent.Record(new RecentFolder(@"x:\DEV", Now, 2, 2));

        Assert.Equal([@"x:\DEV", @"D:\src"], RecentFolders.Load(File).Entries.Select(entry => entry.Folder));
    }

    /// <summary>
    /// A folder with a trailing separator is the same folder as without it: one entry, moved to the top, kept without the separator;
    /// a drive root keeps its own.
    /// </summary>
    [Fact]
    public void TrailingSeparatorIsTheSameFolder()
    {
        var recent = RecentFolders.Load(File);
        recent.Record(new RecentFolder(@"X:\dev", Now - (2 * Day), 1, 1));
        recent.Record(new RecentFolder(@"D:\", Now - Day, 1, 1));

        recent.Record(new RecentFolder(@"X:\dev\", Now, 2, 2));

        Assert.Equal([@"X:\dev", @"D:\"], RecentFolders.Load(File).Entries.Select(entry => entry.Folder));
    }

    /// <summary>A scan that found nothing is recorded, and its line says nothing was found.</summary>
    [Fact]
    public void EmptyScanIsRecordedAsNothingFound()
    {
        var recent = RecentFolders.Load(File);

        recent.Record(new RecentFolder(@"C:\src", Now - (14 * Day), 0, 0));

        RecentFolder entry = Assert.Single(RecentFolders.Load(File).Entries);
        Assert.Equal("2 weeks ago · nothing found", RecentFolders.Summary(entry, Now));
    }

    /// <summary>A file that is not a list of folders reads as none, with a warning naming the file.</summary>
    /// <param name="text">The file's text.</param>
    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[{\"candidates\": 1}]")]
    [InlineData("[null]")]
    public void CorruptFileReadsAsEmptyAndTracesAWarning(string text)
    {
        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(File, text);
        using var capture = new TraceCapture();

        var recent = RecentFolders.Load(File);

        Assert.Empty(recent.Entries);
        Assert.Contains($"cannot read the recent folders from {File}", capture.Text, StringComparison.Ordinal);
    }

    /// <summary>A folder's line counts its candidates, singular for one, and sums their size with the report's size format.</summary>
    [Fact]
    public void RowTextCountsCandidatesAndSize()
    {
        Assert.Equal("yesterday · 14 candidates · 9.6 GB", RecentFolders.Summary(new(@"X:\dev", Now - Day, 14, 10_307_921_510), Now));
        Assert.Equal("3 days ago · 1 candidate · 512 B", RecentFolders.Summary(new(@"X:\dev", Now - (3 * Day), 1, 512), Now));
    }

    /// <summary>How long ago a scan was, at each threshold.</summary>
    /// <param name="secondsAgo">How many seconds before now.</param>
    /// <param name="expected">The text.</param>
    [Theory]
    [InlineData(-5, "just now")]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(3599, "59 min ago")]
    [InlineData(3600, "1 h ago")]
    [InlineData(Day - 1, "23 h ago")]
    [InlineData(Day, "yesterday")]
    [InlineData((2 * Day) - 1, "yesterday")]
    [InlineData(2 * Day, "2 days ago")]
    [InlineData((14 * Day) - 1, "13 days ago")]
    [InlineData(14 * Day, "2 weeks ago")]
    [InlineData(21 * Day, "3 weeks ago")]
    public void WhenThresholds(long secondsAgo, string expected) => Assert.Equal(expected, RecentFolders.When(Now - secondsAgo, Now));

    /// <summary>Collects what is traced while it is alive.</summary>
    private sealed class TraceCapture : TraceListener
    {
        private readonly StringBuilder text = new();

        public TraceCapture() => Trace.Listeners.Add(this);

        public string Text => text.ToString();

        public override void Write(string? message) => text.Append(message);

        public override void WriteLine(string? message) => text.AppendLine(message);

        protected override void Dispose(bool disposing)
        {
            Trace.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
}
