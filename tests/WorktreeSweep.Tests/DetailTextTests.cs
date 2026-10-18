using WorktreeSweep.Discovery;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;
using static WorktreeSweep.Tests.ReportSamples;

namespace WorktreeSweep.Tests;

/// <summary>The picker's detail pane: the labelled fields and the local date line.</summary>
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

    /// <summary>A registered worktree with work, unpushed commits, a size and a git lock reads as five exact fields.</summary>
    [Fact]
    public void RegisteredDetailHasItsLabelledFields()
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

        DetailField[] expected =
        [
            new("What it is", @"repo D:\yaat; branch one"),
            new("State", "2 commits not on main; 1 modified, 0 untracked; +3 not pushed"),
            new("Activity and size", "last activity 2026-09-18 14:13 (3d ago); 1.5 KB, 1 files"),
            new("Marks", "git-locked: on a USB drive"),
            new(
                "Removing it loses",
                "1 modified file, 2 commits not on main and 3 commits not pushed will be lost. It is git-locked: on a USB drive."
            )
            {
                IsLoss = true,
            },
        ];

        Assert.Equal(expected, DetailText.Fields(candidate, Now, UtcZone));
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

        IReadOnlyList<DetailField> fields = DetailText.Fields(candidate, Now, UtcZone);

        Assert.Equal(@"repo D:\yaat; detached abc1234", Field(fields, "What it is"));
        Assert.Contains("released 2026-09-21 14:13 (0m ago): locked", Field(fields, "Marks"), StringComparison.Ordinal);
    }

    /// <summary>An orphan link reads as a link with its target's note and no size words, and has no marks or error field.</summary>
    [Fact]
    public void OrphanLinkDetail()
    {
        OrphanCandidate orphan = Orphan(@"yaat-server.wt\yaat", OrphanKind.Link, Size(0, Now - (2 * 3600)));
        OrphanCandidate candidate = orphan with
        {
            Orphan = orphan.Orphan with { Container = Under("yaat-server.wt"), LinkTarget = Under("yaat-server") },
        };

        IReadOnlyList<DetailField> fields = DetailText.Fields(candidate, Now, UtcZone);

        Assert.Equal(["What it is", "State", "Activity and size", "Removing it loses"], fields.Select(field => field.Label));
        Assert.Equal(@"container D:\yaat-server.wt; link", Field(fields, "What it is"));
        Assert.Equal(@"→ D:\yaat-server, not touched", Field(fields, "State"));
        Assert.Equal("last write 2026-09-21 12:13 (2h ago)", Field(fields, "Activity and size"));
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

        IReadOnlyList<DetailField> fields = DetailText.Fields(Registered(@"yaat.wt\clean", "clean", signals), Now, UtcZone);

        Assert.Equal("Nothing is lost.", Field(fields, "Removing it loses"));
    }

    /// <summary>The signals that could not be read join in the last field.</summary>
    [Fact]
    public void SignalErrorsAreJoinedInTheLastField()
    {
        IReadOnlyList<DetailField> fields = DetailText.Fields(
            Registered(@"yaat.wt\one", "one", new WorktreeSignals { Errors = ["a", "b"] }),
            Now,
            UtcZone
        );

        Assert.Equal(new DetailField("Unreadable signals", "a; b"), fields[^1]);
    }

    /// <summary>Each signal error is flattened to one line before the errors are joined.</summary>
    [Fact]
    public void SignalErrorsAreFlattenedToOneLine()
    {
        RegisteredCandidate candidate = Registered(@"yaat.wt\one", "one", new WorktreeSignals { Errors = ["git failed\nfatal: x"] });

        string errors = Field(DetailText.Fields(candidate, Now, UtcZone), "Unreadable signals");

        Assert.Equal("git failed; fatal: x", errors);
        Assert.DoesNotContain("\n", errors, StringComparison.Ordinal);
    }

    /// <summary>Every reason kind a released marker can hold renders in the detail pane rather than throwing.</summary>
    /// <param name="kind">The reason kind.</param>
    [Theory]
    [MemberData(nameof(ReasonKinds))]
    public void EveryReasonKindRendersInAReleasedMarker(ReasonKind kind)
    {
        RegisteredCandidate baseCandidate = Registered(@"yaat.wt\held", null, new WorktreeSignals());
        RegisteredCandidate candidate = baseCandidate with
        {
            Released = new Released
            {
                ReleasedAtUnix = Now,
                Reason = ReasonOf(kind),
                Holders = [],
            },
        };

        string marks = Field(DetailText.Fields(candidate, Now, UtcZone), "Marks");

        Assert.Contains("released ", marks, StringComparison.Ordinal);
    }

    /// <summary>A folder whose users could not be listed reads as cannot tell what holds it.</summary>
    [Fact]
    public void CannotTellReasonReadsAsItsLabel()
    {
        RegisteredCandidate baseCandidate = Registered(@"yaat.wt\held", null, new WorktreeSignals());
        RegisteredCandidate candidate = baseCandidate with
        {
            Released = new Released
            {
                ReleasedAtUnix = Now,
                Reason = Reason.CannotTell,
                Holders = [],
            },
        };

        string marks = Field(DetailText.Fields(candidate, Now, UtcZone), "Marks");

        Assert.Contains(": cannot tell what holds it", marks, StringComparison.Ordinal);
    }

    /// <summary>Every reason kind, for <see cref="EveryReasonKindRendersInAReleasedMarker"/>.</summary>
    /// <returns>The reason kinds.</returns>
    public static TheoryData<ReasonKind> ReasonKinds()
    {
        var data = new TheoryData<ReasonKind>();
        foreach (ReasonKind kind in Enum.GetValues<ReasonKind>())
        {
            data.Add(kind);
        }
        return data;
    }

    /// <summary>The reason a released marker carries for <paramref name="kind"/>.</summary>
    /// <param name="kind">The reason kind.</param>
    /// <returns>The reason.</returns>
    private static Reason ReasonOf(ReasonKind kind) =>
        kind switch
        {
            ReasonKind.Locked => Reason.Locked,
            ReasonKind.MayHold => Reason.MayHold,
            ReasonKind.CannotTell => Reason.CannotTell,
            ReasonKind.TooBigForRecycleBin => Reason.TooBigForRecycleBin,
            ReasonKind.ShellTimeout => Reason.ShellTimeout,
            ReasonKind.WouldLose => Reason.WouldLose,
            ReasonKind.CallerHolds => Reason.CallerHolds,
            ReasonKind.NotRemovable => Reason.NotRemovable(RefusalReason.NotFound),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a reason kind"),
        };

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

        IReadOnlyList<DetailField> fields = DetailText.Fields(Registered(@"yaat.wt\one", "one", signals), Now, UtcZone);

        Assert.Equal("at least 1.5 KB, 12 files, 3 unreadable", Field(fields, "Activity and size"));
    }

    /// <summary>A worktree with no signals read has no state, activity, marks or error field.</summary>
    [Fact]
    public void FieldsWithoutContentAreLeftOut()
    {
        IReadOnlyList<DetailField> fields = DetailText.Fields(Registered(@"yaat.wt\one", "one", new WorktreeSignals()), Now, UtcZone);

        Assert.Equal(["What it is", "Removing it loses"], fields.Select(field => field.Label));
        Assert.Equal([false, true], fields.Select(field => field.IsLoss));
        Assert.Equal("Its uncommitted changes, merge state, upstream could not be read.", Field(fields, "Removing it loses"));
    }

    /// <summary>The text of the field labelled <paramref name="label"/>, failing when there is not exactly one.</summary>
    /// <param name="fields">The fields.</param>
    /// <param name="label">The label.</param>
    /// <returns>The field's text.</returns>
    private static string Field(IReadOnlyList<DetailField> fields, string label) => Assert.Single(fields, field => field.Label == label).Text;
}
