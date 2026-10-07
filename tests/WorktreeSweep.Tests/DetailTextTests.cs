using WorktreeSweep.Discovery;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;
using static WorktreeSweep.Tests.ReportSamples;

namespace WorktreeSweep.Tests;

/// <summary>The picker's detail pane: the seven lines and the local date line.</summary>
public sealed class DetailTextTests
{
    /// <summary>2026-09-21 00:00 UTC, the day <see cref="ReportSamples.Now"/> falls on.</summary>
    private const long NowDay = 1_789_948_800;

    /// <summary>Every date is rendered in UTC, so the expected strings do not depend on the machine's zone.</summary>
    private static readonly Func<long, TimeSpan> UtcZone = _ => TimeSpan.Zero;

    /// <summary>The epoch day and a fixed instant render in UTC.</summary>
    [Fact]
    public void FormatLocalRendersTheUtcEpochDay()
    {
        Assert.Equal("1970-01-01 00:00 (0m ago)", DetailText.FormatLocal(0, TimeSpan.Zero, 0));
        Assert.Equal("2026-09-21 14:13 (0m ago)", DetailText.FormatLocal(Now, TimeSpan.Zero, Now));
    }

    /// <summary>An offset can move the day, forwards and backwards.</summary>
    [Fact]
    public void FormatLocalAppliesTheOffsetAcrossMidnight()
    {
        long late = NowDay + (23 * 3600) + (30 * 60);
        Assert.Equal("2026-09-22 01:30 (0m ago)", DetailText.FormatLocal(late, TimeSpan.FromMinutes(120), late));
        long early = NowDay + (30 * 60);
        Assert.Equal("2026-09-20 23:30 (0m ago)", DetailText.FormatLocal(early, TimeSpan.FromMinutes(-60), early));
    }

    /// <summary>The leap day and the day after render as themselves.</summary>
    [Fact]
    public void FormatLocalHandlesALeapDay()
    {
        long noon = 1_835_395_200 + (12 * 3600);
        Assert.Equal("2028-02-29 12:00 (0m ago)", DetailText.FormatLocal(noon, TimeSpan.Zero, noon));
        Assert.Equal("2028-03-01 12:00 (0m ago)", DetailText.FormatLocal(noon + Day, TimeSpan.Zero, noon + Day));
    }

    /// <summary>The age suffix uses the unit <see cref="ReportTable.Age"/> picks.</summary>
    [Fact]
    public void FormatLocalAppendsTheAge()
    {
        Assert.EndsWith(" (3d ago)", DetailText.FormatLocal(Now - (3 * Day), TimeSpan.Zero, Now), StringComparison.Ordinal);
        Assert.EndsWith(" (2h ago)", DetailText.FormatLocal(Now - (2 * 3600), TimeSpan.Zero, Now), StringComparison.Ordinal);
    }

    /// <summary>A time past the year 9999 prints the year whole rather than throwing.</summary>
    [Fact]
    public void FormatLocalOfAFarFutureTimeDoesNotThrow() =>
        Assert.Equal("33658-09-27 01:46 (0m ago)", DetailText.FormatLocal(999_999_999_999, TimeSpan.Zero, Now));

    /// <summary>A negative offset can put the local time on the day before the epoch.</summary>
    [Fact]
    public void FormatLocalWithANegativeOffsetBeforeTheEpoch() =>
        Assert.Equal("1969-12-31 22:59 (56y ago)", DetailText.FormatLocal(-1, TimeSpan.FromHours(-1), Now));

    /// <summary>An offset over 14 h either way, or one that is not whole minutes, does not throw.</summary>
    /// <param name="offsetSeconds">The offset, in seconds.</param>
    /// <param name="expected">The exact text, or <see langword="null"/> to assert only that a string comes back.</param>
    [Theory]
    [InlineData(54_000, null)]
    [InlineData(-54_000, null)]
    [InlineData(90, "1970-01-01 00:01 (56y ago)")]
    public void FormatLocalNeverThrowsForAnyOffset(int offsetSeconds, string? expected)
    {
        string text = DetailText.FormatLocal(0, TimeSpan.FromSeconds(offsetSeconds), Now);

        if (expected is null)
        {
            Assert.NotEmpty(text);
        }
        else
        {
            Assert.Equal(expected, text);
        }
    }

    /// <summary>A registered worktree with work, unpushed commits, a size and a git lock reads as seven exact lines.</summary>
    [Fact]
    public void RegisteredDetailHasSevenLines()
    {
        var signals = new WorktreeSignals
        {
            MergeState = MergeState.Unmerged(2),
            MergeStateAgainst = "main",
            Dirty = new Dirty { Modified = 1 },
            Upstream = Upstream.Tracking(3),
            LastActivityUnix = Now - (3 * Day),
            Size = Size(1536, Now),
        };
        RegisteredCandidate baseCandidate = Registered(@"yaat.wt\one", "one", signals);
        RegisteredCandidate candidate = baseCandidate with { Record = baseCandidate.Record with { Locked = "on a USB drive" } };

        string[] expected =
        [
            @"D:\yaat.wt\one",
            @"repo D:\yaat; branch one",
            "2 commits not on main; 1 modified, 0 untracked; +3 not pushed",
            "last activity 2026-09-18 14:13 (3d ago); 1.5 KB, 1 files",
            "git-locked: on a USB drive",
            "1 modified file, 2 commits not on main and 3 commits not pushed will be lost. It is git-locked: on a USB drive.",
            "",
        ];

        Assert.Equal(expected, DetailText.Lines(candidate, Now, UtcZone));
    }

    /// <summary>A detached worktree reads as detached with its short head, and its release carries the date and reason label.</summary>
    [Fact]
    public void DetachedReleasedDetail()
    {
        RegisteredCandidate baseCandidate = Registered(@"yaat.wt\held", null, new WorktreeSignals());
        RegisteredCandidate candidate = baseCandidate with
        {
            Record = baseCandidate.Record with { Head = "abc1234567890" },
            Released = new Released
            {
                ReleasedAtUnix = Now,
                Reason = Reason.Locked,
                Holders = [],
            },
        };

        string[] lines = [.. DetailText.Lines(candidate, Now, UtcZone)];

        Assert.Equal(7, lines.Length);
        Assert.Equal(@"repo D:\yaat; detached abc1234", lines[1]);
        Assert.Contains("released 2026-09-21 14:13 (0m ago): locked", lines[4], StringComparison.Ordinal);
    }

    /// <summary>An orphan link reads as a link with its target's note, no size words, and empty mark and error lines.</summary>
    [Fact]
    public void OrphanLinkDetail()
    {
        OrphanCandidate orphan = Orphan(@"yaat-server.wt\yaat", OrphanKind.Link, Size(0, Now - (2 * 3600)));
        OrphanCandidate candidate = orphan with
        {
            Orphan = orphan.Orphan with { Container = Under("yaat-server.wt"), LinkTarget = Under("yaat-server") },
        };

        string[] lines = [.. DetailText.Lines(candidate, Now, UtcZone)];

        Assert.Equal(7, lines.Length);
        Assert.Equal(@"container D:\yaat-server.wt; link", lines[1]);
        Assert.Equal(@"→ D:\yaat-server, not touched", lines[2]);
        Assert.Equal("last write 2026-09-21 12:13 (2h ago)", lines[3]);
        Assert.DoesNotContain("files", lines[3], StringComparison.Ordinal);
        Assert.Equal("", lines[4]);
        Assert.Equal("", lines[6]);
    }

    /// <summary>A merged, clean, pushed worktree loses nothing.</summary>
    [Fact]
    public void NothingIsLostWhenLossTextIsNull()
    {
        var signals = new WorktreeSignals
        {
            MergeState = MergeState.Ancestor,
            MergeStateAgainst = "main",
            Dirty = new Dirty(),
            Upstream = Upstream.Tracking(0),
        };

        string[] lines = [.. DetailText.Lines(Registered(@"yaat.wt\clean", "clean", signals), Now, UtcZone)];

        Assert.Equal("Nothing is lost.", lines[5]);
    }

    /// <summary>The signals that could not be read join on the last line.</summary>
    [Fact]
    public void SignalErrorsAreJoinedOnLineSeven()
    {
        string[] lines = [.. DetailText.Lines(Registered(@"yaat.wt\one", "one", new WorktreeSignals { Errors = ["a", "b"] }), Now, UtcZone)];

        Assert.Equal("a; b", lines[6]);
    }

    /// <summary>Each signal error is flattened to one line before the errors are joined.</summary>
    [Fact]
    public void SignalErrorsAreFlattenedToOneLine()
    {
        RegisteredCandidate candidate = Registered(@"yaat.wt\one", "one", new WorktreeSignals { Errors = ["git failed\nfatal: x"] });

        string[] lines = [.. DetailText.Lines(candidate, Now, UtcZone)];

        Assert.Equal("git failed; fatal: x", lines[6]);
        Assert.DoesNotContain("\n", lines[6], StringComparison.Ordinal);
    }

    /// <summary>A partly measured size reads as a lower bound and still names its unreadable entries.</summary>
    [Fact]
    public void PartlyMeasuredSizeReadsAsALowerBound()
    {
        var signals = new WorktreeSignals
        {
            Size = new SizeInfo
            {
                Bytes = 1536,
                Files = 12,
                Unreadable = 3,
                LastWriteUnix = Now,
            },
        };

        string[] lines = [.. DetailText.Lines(Registered(@"yaat.wt\one", "one", signals), Now, UtcZone)];

        Assert.Equal("at least 1.5 KB, 12 files, 3 unreadable", lines[3]);
    }
}
