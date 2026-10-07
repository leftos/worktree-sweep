using WorktreeSweep.Git;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>A worktree's reads are skipped once one of them times out; any other failure lets the rest run.</summary>
public sealed class StalledSignalsTests
{
    private const string Timeout = "x did not exit within 60 s";
    private const string Worktree = @"D:\a\wt";

    /// <summary>The notice recorded for <see cref="Worktree"/>, with the volume its path really lies on named.</summary>
    private static string Notice(string worktree) => $"git timed out on {VolumeStalls.Volume(worktree)}; remaining signals skipped";

    /// <summary>
    /// A read that times out stops every read after it: they are never called, they report nothing, and one notice naming the
    /// volume is recorded after the timeout's own message.
    /// </summary>
    [Fact]
    public void TimeoutSkipsTheRemainingReads()
    {
        var reads = new SignalReads(new VolumeStalls(), Worktree);
        int called = 0;

        string? first = reads.Read(() =>
        {
            called++;
            return "one";
        });
        string? second = reads.Read<string>(() =>
        {
            called++;
            throw new GitTimeoutException(Timeout);
        });
        string? third = reads.Read(() =>
        {
            called++;
            return "three";
        });
        string? fourth = reads.Read(() =>
        {
            called++;
            return "four";
        });

        Assert.Equal("one", first);
        Assert.Null(second);
        Assert.Null(third);
        Assert.Null(fourth);
        Assert.Equal(2, called);
        string[] expected = [Timeout, Notice(Worktree)];
        Assert.Equal(expected, reads.Errors);
        Assert.Equal(VolumeStalls.Volume(Worktree), reads.Volume);
    }

    /// <summary>The size walk is one of the reads, so a stalled worktree's size is skipped with the rest and stays null.</summary>
    [Fact]
    public void SizeWalkIsSkippedAfterTheTimeout()
    {
        var reads = new SignalReads(new VolumeStalls(), Worktree);
        int walked = 0;

        _ = reads.Read<string>(() => throw new GitTimeoutException(Timeout));
        SizeInfo? size = reads.Read(() =>
        {
            walked++;
            return new SizeInfo { Bytes = 4096 };
        });

        Assert.Null(size);
        Assert.Equal(0, walked);
        string[] expected = [Timeout, Notice(Worktree)];
        Assert.Equal(expected, reads.Errors);
    }

    /// <summary>A failure that is not a timeout leaves the later reads running, and records only that failure's message.</summary>
    [Fact]
    public void OtherGitFailureDoesNotSkip()
    {
        var reads = new SignalReads(new VolumeStalls(), Worktree);

        _ = reads.Read(() => "one");
        _ = reads.Read<string>(() => throw new GitException("plain failure"));
        string? third = reads.Read(() => "three");
        string? fourth = reads.Read(() => "four");

        Assert.Equal("three", third);
        Assert.Equal("four", fourth);
        string[] expected = ["plain failure"];
        Assert.Equal(expected, reads.Errors);
    }

    /// <summary>Reads after the timeout add the skip notice once, however many of them there are.</summary>
    [Fact]
    public void SkipNoticeIsRecordedOnce()
    {
        var reads = new SignalReads(new VolumeStalls(), Worktree);

        _ = reads.Read<string>(() => throw new GitTimeoutException(Timeout));
        _ = reads.Read(() => "two");
        _ = reads.Read(() => "three");

        string[] expected = [Timeout, Notice(Worktree)];
        Assert.Equal(expected, reads.Errors);
    }

    /// <summary>
    /// A failure that is not a timeout leaves the volume usable: the next worktree on it still makes its git calls.
    /// </summary>
    [Fact]
    public void OtherGitFailureDoesNotStallTheVolume()
    {
        var stalls = new VolumeStalls();
        var reads = new SignalReads(stalls, @"D:\one");

        _ = reads.Read<string>(() => throw new GitException("plain failure"));

        Assert.False(stalls.IsStalled(@"D:\two"));
    }

    /// <summary>
    /// A worktree on a volume another worktree's timeout stalled makes no git call at all, and records the stall notice naming the
    /// volume by itself.
    /// </summary>
    [Fact]
    public void ASecondWorktreeOnAStalledVolumeMakesNoGitCall()
    {
        var stalls = new VolumeStalls();
        var first = new SignalReads(stalls, @"D:\a");
        int calls = 0;

        _ = first.Read<string>(() =>
        {
            calls++;
            throw new GitTimeoutException(Timeout);
        });
        var second = new SignalReads(stalls, @"D:\b\c");
        string? value = second.Read(() =>
        {
            calls++;
            return "value";
        });

        Assert.Equal(1, calls);
        Assert.Null(value);
        Assert.Equal(VolumeStalls.Key(@"D:\a"), VolumeStalls.Key(@"D:\b\c"));
        string[] expected = [Notice(@"D:\b\c")];
        Assert.Equal(expected, second.Errors);
    }

    /// <summary>
    /// A git timeout reading the first default is not retried against the next one: it stops the state read at once and reaches the
    /// caller, so the worktree's stall skip can arm on it.
    /// </summary>
    [Fact]
    public void MergeStateTimeoutIsNotRetriedOnTheNextDefault()
    {
        (string Name, string Ref)[] refs = [("main", "refs/heads/main"), ("origin/main", "refs/remotes/origin/main")];
        var asked = new List<string>();

        GitTimeoutException error = Assert.Throws<GitTimeoutException>(() =>
        {
            _ = SignalReader.BestState(
                refs,
                reference =>
                {
                    asked.Add(reference);
                    if (asked.Count == 1)
                    {
                        throw new GitTimeoutException(Timeout);
                    }
                    return MergeState.Ancestor;
                }
            );
        });

        Assert.Equal(Timeout, error.Message);
        string[] expected = ["refs/heads/main"];
        Assert.Equal(expected, asked);
    }
}
