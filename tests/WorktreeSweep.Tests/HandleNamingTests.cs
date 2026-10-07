using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WorktreeSweep.Holders;

namespace WorktreeSweep.Tests;

/// <summary>The handle-naming workers, driven by checks and cancel hooks that touch no handle of the system.</summary>
public sealed class HandleNamingTests
{
    /// <summary>How long a naming call may run before the test fails as hung.</summary>
    private static readonly TimeSpan HangLimit = TimeSpan.FromSeconds(30);

    /// <summary>A lookup timeout the tests' slow checks always pass.</summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>A lookup timeout the tests' quick checks never reach.</summary>
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A lookup that completes within the timeout gives its name.</summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task CompletedLookupIsNamed()
    {
        IReadOnlyList<(int Pid, string? Name)> names = await NameWithin([(7, 1)], Quick, 4, LongTimeout);

        Assert.Equal<(int, string?)>([(7, "file 1")], names);
    }

    /// <summary>A lookup that outlasts the timeout and its cancel gives no name, and the call returns without waiting for it.</summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task SlowLookupIsNamedNullAfterTheTimeout()
    {
        using var gate = new ManualResetEventSlim();
        try
        {
            var clock = Stopwatch.StartNew();

            IReadOnlyList<(int Pid, string? Name)> names = await NameWithin([(7, 1)], (_, started, _) => Blocked(gate, started), 4, ShortTimeout);

            Assert.Equal<(int, string?)>([(7, null)], names);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the call waited {clock.Elapsed} for a hung lookup");
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>
    /// A timed-out lookup is cancelled; once its check returns it has released what it held, the handle stays unnamed, and the same
    /// worker goes on to name the next handle.
    /// </summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task CancelledLookupReleasesItsResourceAndKeepsItsWorker()
    {
        using var gate = new ManualResetEventSlim();
        int held = 0;
        int cancels = 0;
        var threads = new HashSet<int>();
        CheckedHandle Check(int handle, Action started, Action ended)
        {
            lock (threads)
            {
                _ = threads.Add(Environment.CurrentManagedThreadId);
            }
            if (handle != 1)
            {
                return Quick(handle, started, ended);
            }
            _ = Interlocked.Increment(ref held);
            try
            {
                return Blocked(gate, started);
            }
            finally
            {
                _ = Interlocked.Decrement(ref held);
            }
        }
        void Cancel(SafeHandle thread)
        {
            _ = Interlocked.Increment(ref cancels);
            gate.Set();
        }

        IReadOnlyList<(int Pid, string? Name)> names = await NameWithin([(1, 1), (2, 2)], Check, 1, TimeSpan.FromMilliseconds(500), Cancel);

        Assert.Equal<(int, string?)>([(1, null), (2, "file 2")], [.. names.OrderBy(name => name.Pid)]);
        Assert.Equal(1, cancels);
        Assert.Equal(0, held);
        Assert.Single(threads);
    }

    /// <summary>
    /// A check that throws because its timed-out lookup was cancelled leaves its handle unnamed rather than failing the call, and its
    /// worker goes on to name the handles after it.
    /// </summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task CheckThatThrowsWhenCancelledLeavesItsHandleUnnamed()
    {
        using var cancelled = new ManualResetEventSlim();
        CheckedHandle Check(int handle, Action started, Action ended)
        {
            if (handle != 1)
            {
                return Quick(handle, started, ended);
            }
            started();
            _ = cancelled.Wait(HangLimit);
            throw new IOException("the lookup was aborted");
        }

        IReadOnlyList<(int Pid, string? Name)> names = await NameWithin(
            [(1, 1), (2, 2), (3, 3)],
            Check,
            1,
            TimeSpan.FromMilliseconds(500),
            _ => cancelled.Set()
        );

        Assert.Equal<(int, string?)>([(1, null), (2, "file 2"), (3, "file 3")], [.. names.OrderBy(name => name.Pid)]);
    }

    /// <summary>A worker whose cancelled lookup still hangs is abandoned, and the handles after it are named by a fresh worker.</summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task AbandonedWorkerDoesNotStopLaterHandles()
    {
        using var gate = new ManualResetEventSlim();
        try
        {
            IReadOnlyList<(int Pid, string? Name)> names = await NameWithin(
                [(1, 1), (2, 2), (3, 3)],
                (handle, started, ended) => handle == 1 ? Blocked(gate, started) : Quick(handle, started, ended),
                1,
                ShortTimeout
            );

            Assert.Equal<(int, string?)>([(1, null), (2, "file 2"), (3, "file 3")], [.. names.OrderBy(name => name.Pid)]);
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>The timeout counts from the start of the lookup, so a check slow to begin but quick to look up is named.</summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task SlowToStartQuickLookupIsNamed()
    {
        IReadOnlyList<(int Pid, string? Name)> names = await NameWithin(
            [(7, 1)],
            (handle, started, ended) =>
            {
                Thread.Sleep(300);
                return Quick(handle, started, ended);
            },
            4,
            ShortTimeout
        );

        Assert.Equal<(int, string?)>([(7, "file 1")], names);
    }

    /// <summary>Exactly as many lookups as there are workers run at once.</summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task WorkerCountIsRespected()
    {
        var sync = new Lock();
        int running = 0;
        int peak = 0;
        using var allBusy = new ManualResetEventSlim();
        CheckedHandle Check(int handle, Action started, Action ended)
        {
            started();
            lock (sync)
            {
                running++;
                peak = Math.Max(peak, running);
                if (running == HandleNaming.DefaultWorkers)
                {
                    allBusy.Set();
                }
            }
            _ = allBusy.Wait(TimeSpan.FromSeconds(5));
            lock (sync)
            {
                running--;
            }
            ended();
            return CheckedHandle.Disk($"file {handle}");
        }
        (int, int)[] handles = [.. Enumerable.Range(1, 12).Select(handle => (handle, handle))];

        IReadOnlyList<(int Pid, string? Name)> names = await NameWithin(handles, Check, HandleNaming.DefaultWorkers, LongTimeout);

        Assert.Equal(12, names.Count);
        Assert.Equal(HandleNaming.DefaultWorkers, peak);
    }

    /// <summary>A handle that is not a file on disk adds nothing.</summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task SkippedHandlesAreOmitted()
    {
        IReadOnlyList<(int Pid, string? Name)> names = await NameWithin(
            [(1, 1), (2, 2), (3, 3)],
            (handle, started, ended) => handle == 2 ? CheckedHandle.Skipped : Quick(handle, started, ended),
            4,
            LongTimeout
        );

        Assert.Equal<(int, string?)>([(1, "file 1"), (3, "file 3")], [.. names.OrderBy(name => name.Pid)]);
    }

    /// <summary>A check that fails at once adds its process to the failed ones, not to the unnamed ones; only a timeout does that.</summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task ImmediateFailureMarksThePidFailedNotUnnamed()
    {
        NamedHandles named = await NameAllWithin(
            [(7, 1)],
            (_, started, _) =>
            {
                started();
                return CheckedHandle.Failed;
            },
            4,
            LongTimeout,
            _ => { }
        );

        Assert.Empty(named.Names);
        Assert.Equal([7], named.Failed);
    }

    /// <summary>
    /// A check whose lookup has ended but that returns only after the timeout is not cancelled, since no lookup is left to cancel;
    /// its handle still counts as unnamed.
    /// </summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task LookupThatEndedIsNotCancelledAfterTheTimeout()
    {
        int cancels = 0;

        IReadOnlyList<(int Pid, string? Name)> names = await NameWithin(
            [(7, 1)],
            (handle, started, ended) =>
            {
                started();
                ended();
                Thread.Sleep(ShortTimeout * 3);
                return CheckedHandle.Disk($"file {handle}");
            },
            1,
            ShortTimeout,
            _ => Interlocked.Increment(ref cancels)
        );

        Assert.Equal<(int, string?)>([(7, null)], names);
        Assert.Equal(0, Volatile.Read(ref cancels));
    }

    /// <summary>
    /// Once a check throws, no driver takes another handle: only the handles taken before the failure are checked, and the exception
    /// surfaces after each of their checks has returned.
    /// </summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task ThrowingCheckStopsTheDriversAndSurfacesAfterEveryDriverFinished()
    {
        using var thrown = new ManualResetEventSlim();
        int running = 0;
        var checkedHandles = new ConcurrentBag<int>();
        CheckedHandle Check(int handle, Action started, Action ended)
        {
            if (handle == 1)
            {
                thrown.Set();
                throw new InvalidOperationException("check failed");
            }
            _ = Interlocked.Increment(ref running);
            checkedHandles.Add(handle);
            // Held until well after handle 1 throws, so every driver takes its next handle only once the failure is captured.
            _ = thrown.Wait(HangLimit);
            Thread.Sleep(200);
            _ = Interlocked.Decrement(ref running);
            return Quick(handle, started, ended);
        }
        (int, int)[] handles = [.. Enumerable.Range(1, 12).Select(handle => (handle, handle))];

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NameWithin(handles, Check, HandleNaming.DefaultWorkers, LongTimeout)
        );

        Assert.Equal("check failed", error.Message);
        Assert.Equal(0, Volatile.Read(ref running));
        Assert.All(checkedHandles, handle => Assert.InRange(handle, 2, HandleNaming.DefaultWorkers));
    }

    /// <summary>A check that starts its lookup, names the file at once and ends its lookup.</summary>
    private static CheckedHandle Quick(int handle, Action started, Action ended)
    {
        started();
        ended();
        return CheckedHandle.Disk($"file {handle}");
    }

    /// <summary>A check that starts its lookup, then waits for <paramref name="gate"/>, at most <see cref="HangLimit"/>.</summary>
    private static CheckedHandle Blocked(ManualResetEventSlim gate, Action started)
    {
        started();
        _ = gate.Wait(HangLimit);
        return CheckedHandle.Disk("late");
    }

    /// <summary>Names the handles on a pool thread with a cancel hook that does nothing.</summary>
    private static Task<IReadOnlyList<(int Pid, string? Name)>> NameWithin(
        IReadOnlyList<(int Pid, int Handle)> handles,
        HandleCheck<int> check,
        int workers,
        TimeSpan timeout
    ) => NameWithin(handles, check, workers, timeout, _ => { });

    /// <summary>Names the handles on a pool thread, failing the test when that takes longer than <see cref="HangLimit"/>.</summary>
    private static async Task<IReadOnlyList<(int Pid, string? Name)>> NameWithin(
        IReadOnlyList<(int Pid, int Handle)> handles,
        HandleCheck<int> check,
        int workers,
        TimeSpan timeout,
        Action<SafeHandle> cancel
    ) => (await NameAllWithin(handles, check, workers, timeout, cancel)).Names;

    /// <summary>Names the handles on a pool thread, failing the test when that takes longer than <see cref="HangLimit"/>.</summary>
    private static Task<NamedHandles> NameAllWithin(
        IReadOnlyList<(int Pid, int Handle)> handles,
        HandleCheck<int> check,
        int workers,
        TimeSpan timeout,
        Action<SafeHandle> cancel
    ) =>
        Task.Run(() => HandleNaming.Name(handles, check, workers, timeout, cancel), TestContext.Current.CancellationToken)
            .WaitAsync(HangLimit, TestContext.Current.CancellationToken);
}
