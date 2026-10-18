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
        IReadOnlyList<NamedHandle> names = await NameWithin([(7, 1)], Quick, 4, LongTimeout);

        Assert.Equal<NamedHandle>([new(7, "file 1", 1)], names);
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

            IReadOnlyList<NamedHandle> names = await NameWithin([(7, 1)], (_, started, _) => Blocked(gate, started), 4, ShortTimeout);

            Assert.Equal<NamedHandle>([new(7, null, 0)], names);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the call waited {clock.Elapsed} for a hung lookup");
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>
    /// A timed-out lookup is cancelled; once its check returns it has released what it held, the handle keeps the name the check
    /// returned, and the same worker goes on to name the next handle.
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

        IReadOnlyList<NamedHandle> names = await NameWithin([(1, 1), (2, 2)], Check, 1, TimeSpan.FromMilliseconds(500), Cancel);

        Assert.Equal<NamedHandle>([new(1, "late", 0), new(2, "file 2", 2)], [.. names.OrderBy(name => name.Pid)]);
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

        IReadOnlyList<NamedHandle> names = await NameWithin([(1, 1), (2, 2), (3, 3)], Check, 1, TimeSpan.FromMilliseconds(500), _ => cancelled.Set());

        Assert.Equal<NamedHandle>([new(1, null, 0), new(2, "file 2", 2), new(3, "file 3", 3)], [.. names.OrderBy(name => name.Pid)]);
    }

    /// <summary>A worker whose cancelled lookup still hangs is abandoned, and the handles after it are named by a fresh worker.</summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task AbandonedWorkerDoesNotStopLaterHandles()
    {
        using var gate = new ManualResetEventSlim();
        try
        {
            IReadOnlyList<NamedHandle> names = await NameWithin(
                [(1, 1), (2, 2), (3, 3)],
                (handle, started, ended) => handle == 1 ? Blocked(gate, started) : Quick(handle, started, ended),
                1,
                ShortTimeout
            );

            Assert.Equal<NamedHandle>([new(1, null, 0), new(2, "file 2", 2), new(3, "file 3", 3)], [.. names.OrderBy(name => name.Pid)]);
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
        IReadOnlyList<NamedHandle> names = await NameWithin(
            [(7, 1)],
            (handle, started, ended) =>
            {
                Thread.Sleep(300);
                return Quick(handle, started, ended);
            },
            4,
            ShortTimeout
        );

        Assert.Equal<NamedHandle>([new(7, "file 1", 1)], names);
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
            return CheckedHandle.Disk($"file {handle}", handle);
        }
        (int, int)[] handles = [.. Enumerable.Range(1, 12).Select(handle => (handle, handle))];

        IReadOnlyList<NamedHandle> names = await NameWithin(handles, Check, HandleNaming.DefaultWorkers, LongTimeout);

        Assert.Equal(12, names.Count);
        Assert.Equal(HandleNaming.DefaultWorkers, peak);
    }

    /// <summary>A handle that is not a file on disk adds nothing.</summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task SkippedHandlesAreOmitted()
    {
        IReadOnlyList<NamedHandle> names = await NameWithin(
            [(1, 1), (2, 2), (3, 3)],
            (handle, started, ended) => handle == 2 ? CheckedHandle.Skipped : Quick(handle, started, ended),
            4,
            LongTimeout
        );

        Assert.Equal<NamedHandle>([new(1, "file 1", 1), new(3, "file 3", 3)], [.. names.OrderBy(name => name.Pid)]);
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
    /// A check whose lookup has ended but that returns only after its driver gave up is not cancelled, since no lookup is left to
    /// cancel; its handle still counts as unnamed.
    /// </summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task LookupThatEndedIsNotCancelledAfterTheTimeout()
    {
        int cancels = 0;
        var release = new TaskCompletionSource();
        IReadOnlyList<NamedHandle> names;
        try
        {
            names = await NameWithin(
                [(7, 1)],
                (handle, started, ended) =>
                {
                    started();
                    ended();
                    // Released only once the call has returned, which it does only after its driver gave up on this check.
                    _ = release.Task.Wait(HangLimit);
                    return CheckedHandle.Disk($"file {handle}", handle);
                },
                1,
                ShortTimeout,
                _ => Interlocked.Increment(ref cancels)
            );
        }
        finally
        {
            _ = release.TrySetResult();
        }

        Assert.Equal<NamedHandle>([new(7, null, 0)], names);
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

    /// <summary>
    /// Handles whose workers had not claimed them when their start timeout ran out are withdrawn: once each worker tries to claim its
    /// handle, the claim fails, no check is ever called, and nothing is left unsettled.
    /// </summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task CheckWithdrawnBeforeItStartsNeverRuns()
    {
        var release = new TaskCompletionSource();
        var bothRefused = new TaskCompletionSource();
        int refused = 0;
        int checks = 0;
        void AfterClaim(bool claimed)
        {
            if (!claimed && Interlocked.Increment(ref refused) == 2)
            {
                _ = bothRefused.TrySetResult();
            }
        }
        CheckedHandle Check(int handle, Action started, Action ended)
        {
            _ = Interlocked.Increment(ref checks);
            return Quick(handle, started, ended);
        }
        var account = new LookupAccount();
        var options = new NamingOptions(1, LongTimeout, ShortTimeout) { BeforeClaim = () => release.Task.Wait(HangLimit), AfterClaim = AfterClaim };
        NamedHandles named;
        try
        {
            named = await NameOn([(1, 1), (2, 2)], Check, options, _ => { }, account);
        }
        finally
        {
            _ = release.TrySetResult();
        }

        // Both claim attempts have returned, refused, before anything is asserted.
        await bothRefused.Task.WaitAsync(HangLimit, TestContext.Current.CancellationToken);
        Assert.Equal<NamedHandle>([new(1, null, 0), new(2, null, 0)], [.. named.Names.OrderBy(name => name.Pid)]);
        Assert.Equal(0, Volatile.Read(ref checks));
        Assert.Equal(0, account.Unsettled);
    }

    /// <summary>
    /// A worker that does not claim its handle within the start timeout is replaced after that one handle: the handles after it are
    /// named by a second worker.
    /// </summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task WorkerThatNeverClaimsIsReplacedAfterOneHandle()
    {
        var release = new TaskCompletionSource();
        var hookThreads = new List<int>();
        int hookCalls = 0;
        void BeforeClaim()
        {
            lock (hookThreads)
            {
                hookThreads.Add(Environment.CurrentManagedThreadId);
            }
            if (Interlocked.Increment(ref hookCalls) == 1)
            {
                _ = release.Task.Wait(HangLimit);
            }
        }
        var options = new NamingOptions(1, LongTimeout, HandleNaming.DefaultStartTimeout) { BeforeClaim = BeforeClaim };
        NamedHandles named;
        int threadsBeforeRelease;
        try
        {
            named = await NameOn([(1, 1), (2, 2), (3, 3)], Quick, options, _ => { }, new LookupAccount());
            lock (hookThreads)
            {
                threadsBeforeRelease = hookThreads.Distinct().Count();
            }
        }
        finally
        {
            _ = release.TrySetResult();
        }

        Assert.Equal<NamedHandle>([new(1, null, 0), new(2, "file 2", 2), new(3, "file 3", 3)], [.. named.Names.OrderBy(name => name.Pid)]);
        Assert.Equal(2, threadsBeforeRelease);
    }

    /// <summary>
    /// A handle claimed in time whose lookup had not begun when its start timeout ran out gets no cancel, so the skip its check then
    /// returns is genuine: it adds nothing, not a timed-out lookup.
    /// </summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task LateClaimThatSkipsWithoutACancelIsNotATimeout()
    {
        (NamedHandles named, IReadOnlyList<bool> attempts) = await NameLateClaim(CheckedHandle.Skipped);

        Assert.Equal([false], attempts);
        Assert.Empty(named.Names);
        Assert.Empty(named.Failed);
    }

    /// <summary>A handle claimed in time whose lookup had not begun gets no cancel, so the failure its check then returns is genuine.</summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task LateClaimThatFailsWithoutACancelIsAFailure()
    {
        (NamedHandles named, IReadOnlyList<bool> attempts) = await NameLateClaim(CheckedHandle.Failed);

        Assert.Equal([false], attempts);
        Assert.Empty(named.Names);
        Assert.Equal([7], named.Failed);
    }

    /// <summary>A check that returns a disk name after its timed-out lookup was cancelled keeps that name, not a timeout.</summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task CheckThatReturnsAfterItsCancelKeepsItsName()
    {
        var cancelled = new TaskCompletionSource();
        CheckedHandle Check(int handle, Action started, Action ended)
        {
            started();
            _ = cancelled.Task.Wait(HangLimit);
            ended();
            return CheckedHandle.Disk($"file {handle}", handle);
        }

        IReadOnlyList<NamedHandle> names = await NameWithin([(7, 7)], Check, 1, TimeSpan.FromSeconds(1), _ => cancelled.TrySetResult());

        Assert.Equal<NamedHandle>([new(7, "file 7", 7)], names);
    }

    /// <summary>
    /// A check that ignores its cancel is unsettled once the call returns, a bounded wait for it returns with it still unsettled, and
    /// once it returns nothing is unsettled.
    /// </summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task CheckThatIgnoresItsCancelIsUnsettledUntilItReturns()
    {
        using var gate = new ManualResetEventSlim();
        var account = new LookupAccount();
        try
        {
            _ = await NameOn([(7, 1)], (_, started, _) => Blocked(gate, started), new NamingOptions(1, ShortTimeout, LongTimeout), _ => { }, account);

            Assert.Equal(1, account.Unsettled);
            Assert.Equal(1, account.WaitForUnsettled(ShortTimeout));
        }
        finally
        {
            gate.Set();
        }

        Assert.Equal(0, account.WaitForUnsettled(HangLimit));
    }

    /// <summary>One account given to two calls adds up the checks each gave up on; an account given to neither has none.</summary>
    /// <returns>The test.</returns>
    [Fact]
    public async Task OneAccountAddsUpAcrossCalls()
    {
        using var gate = new ManualResetEventSlim();
        var account = new LookupAccount();
        var other = new LookupAccount();
        var options = new NamingOptions(1, ShortTimeout, LongTimeout);
        try
        {
            _ = await NameOn([(7, 1)], (_, started, _) => Blocked(gate, started), options, _ => { }, account);
            _ = await NameOn([(8, 2)], (_, started, _) => Blocked(gate, started), options, _ => { }, account);

            Assert.Equal(2, account.Unsettled);
            Assert.Equal(0, other.Unsettled);
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>
    /// Names one handle of pid 7 whose worker claims it at once and is then held until its driver, the start timeout having run out
    /// with no lookup running, has tried to cancel; the check then returns <paramref name="result"/> without starting a lookup.
    /// </summary>
    /// <returns>What the naming found, and whether each cancel attempt sent a cancel.</returns>
    private static async Task<(NamedHandles Named, IReadOnlyList<bool> Attempts)> NameLateClaim(CheckedHandle result)
    {
        var cancelTried = new TaskCompletionSource();
        var attempts = new List<bool>();
        void AfterCancelAttempt(bool sent)
        {
            lock (attempts)
            {
                attempts.Add(sent);
            }
            _ = cancelTried.TrySetResult();
        }
        var options = new NamingOptions(1, LongTimeout, HandleNaming.DefaultStartTimeout)
        {
            AfterClaim = claimed =>
            {
                if (claimed)
                {
                    _ = cancelTried.Task.Wait(HangLimit);
                }
            },
            AfterCancelAttempt = AfterCancelAttempt,
        };
        NamedHandles named;
        try
        {
            named = await NameOn([(7, 1)], (_, _, _) => result, options, _ => { }, new LookupAccount());
        }
        finally
        {
            _ = cancelTried.TrySetResult();
        }
        lock (attempts)
        {
            return (named, [.. attempts]);
        }
    }

    /// <summary>A check that starts its lookup, names the file at once and ends its lookup.</summary>
    private static CheckedHandle Quick(int handle, Action started, Action ended)
    {
        started();
        ended();
        return CheckedHandle.Disk($"file {handle}", handle);
    }

    /// <summary>A check that starts its lookup, then waits for <paramref name="gate"/>, at most <see cref="HangLimit"/>.</summary>
    private static CheckedHandle Blocked(ManualResetEventSlim gate, Action started)
    {
        started();
        _ = gate.Wait(HangLimit);
        return CheckedHandle.Disk("late", 0);
    }

    /// <summary>Names the handles on a pool thread with a cancel hook that does nothing.</summary>
    private static Task<IReadOnlyList<NamedHandle>> NameWithin(
        IReadOnlyList<(int Pid, int Handle)> handles,
        HandleCheck<int> check,
        int workers,
        TimeSpan timeout
    ) => NameWithin(handles, check, workers, timeout, _ => { });

    /// <summary>Names the handles on a pool thread, failing the test when that takes longer than <see cref="HangLimit"/>.</summary>
    private static async Task<IReadOnlyList<NamedHandle>> NameWithin(
        IReadOnlyList<(int Pid, int Handle)> handles,
        HandleCheck<int> check,
        int workers,
        TimeSpan timeout,
        Action<SafeHandle> cancel
    ) => (await NameAllWithin(handles, check, workers, timeout, cancel)).Names;

    /// <summary>
    /// Names the handles on a pool thread with the production start timeout and an account of their own, failing the test when that
    /// takes longer than <see cref="HangLimit"/>.
    /// </summary>
    private static Task<NamedHandles> NameAllWithin(
        IReadOnlyList<(int Pid, int Handle)> handles,
        HandleCheck<int> check,
        int workers,
        TimeSpan timeout,
        Action<SafeHandle> cancel
    ) => NameOn(handles, check, new NamingOptions(workers, timeout, HandleNaming.DefaultStartTimeout), cancel, new LookupAccount());

    /// <summary>Names the handles on a pool thread on <paramref name="account"/>, failing the test when that takes longer than
    /// <see cref="HangLimit"/>.</summary>
    private static Task<NamedHandles> NameOn(
        IReadOnlyList<(int Pid, int Handle)> handles,
        HandleCheck<int> check,
        NamingOptions options,
        Action<SafeHandle> cancel,
        LookupAccount account
    ) =>
        Task.Run(() => HandleNaming.Name(handles, check, options, cancel, account), TestContext.Current.CancellationToken)
            .WaitAsync(HangLimit, TestContext.Current.CancellationToken);
}
