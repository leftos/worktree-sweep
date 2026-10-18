using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace WorktreeSweep.Holders;

/// <summary>How checking one handle ended.</summary>
internal enum CheckOutcome
{
    /// <summary>The handle is not a file on disk.</summary>
    Skipped,

    /// <summary>The handle could not be duplicated, or its name could not be read; the check failed at once.</summary>
    Failed,

    /// <summary>The handle is a file on disk, and was named.</summary>
    Disk,
}

/// <summary>What checking one handle found.</summary>
/// <param name="Outcome">Whether the handle was skipped, failed or named.</param>
/// <param name="Name">The file's name, or <see langword="null"/> unless the handle was named.</param>
/// <param name="Handle">The handle's value in its process, or 0 unless the handle was named.</param>
internal readonly record struct CheckedHandle(CheckOutcome Outcome, string? Name, nint Handle)
{
    /// <summary>Gets the result for a handle that is not a file on disk.</summary>
    internal static CheckedHandle Skipped => new(CheckOutcome.Skipped, null, 0);

    /// <summary>Gets the result for a handle that could not be duplicated or named.</summary>
    internal static CheckedHandle Failed => new(CheckOutcome.Failed, null, 0);

    /// <summary>The result for a named file on disk.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="handle">The handle's value in its process.</param>
    /// <returns>The result.</returns>
    internal static CheckedHandle Disk(string name, nint handle) => new(CheckOutcome.Disk, name, handle);
}

/// <summary>One disk handle a scan found in a process.</summary>
/// <param name="Pid">The process holding it.</param>
/// <param name="Name">The file's name, or <see langword="null"/> when it could not be read in time, or when the check failed on a
/// process the file system reports as using the folder.</param>
/// <param name="Handle">The handle's value in that process, meaningful only with the process's start time; 0 when the name is
/// <see langword="null"/>.</param>
internal sealed record NamedHandle(int Pid, string? Name, nint Handle);

/// <summary>What naming a set of handles found.</summary>
/// <param name="Names">Each named disk handle; the name is <see langword="null"/> when its lookup timed out.</param>
/// <param name="Failed">Processes with a handle whose check failed at once.</param>
internal sealed record NamedHandles(IReadOnlyList<NamedHandle> Names, IReadOnlySet<int> Failed);

/// <summary>Checks one handle on a <see cref="HandleNaming"/> worker.</summary>
/// <typeparam name="T">What identifies the handle.</typeparam>
/// <param name="handle">The handle.</param>
/// <param name="lookupStarting">Called once the check is about to do what may hang; the lookup's time counts from there.</param>
/// <param name="lookupEnded">
/// Called once nothing that may hang is left, before the check releases what it holds; a timed-out lookup is not cancelled after it.
/// </param>
/// <returns>What the check found.</returns>
internal delegate CheckedHandle HandleCheck<in T>(T handle, Action lookupStarting, Action lookupEnded);

/// <summary>How many workers name handles, and how long a check may take to start its lookup and to finish it.</summary>
/// <param name="Workers">Workers checking handles at once; at least 1.</param>
/// <param name="Timeout">How long one lookup may take from its start, and how long a cancelled lookup may take to return.</param>
/// <param name="StartTimeout">
/// How long a worker may take to start a lookup, which covers its thread being scheduled and the handle being duplicated; past it,
/// the handle is treated as a timed-out lookup.
/// </param>
internal sealed record NamingOptions(int Workers, TimeSpan Timeout, TimeSpan StartTimeout)
{
    /// <summary>Gets what each worker calls on its own thread before it claims a handle; <see langword="null"/> outside tests.</summary>
    internal Action? BeforeClaim { get; init; }

    /// <summary>Gets what each worker calls on its own thread once it has tried to claim a handle, with whether it did;
    /// <see langword="null"/> outside tests.</summary>
    internal Action<bool>? AfterClaim { get; init; }

    /// <summary>Gets a test seam: what each driver calls on its own thread once it has tried to cancel a timed-out lookup, with
    /// whether a cancel was sent; <see langword="null"/> outside tests.</summary>
    internal Action<bool>? AfterCancelAttempt { get; init; }
}

/// <summary>
/// Names handles on abandonable workers: a name lookup can hang forever (on a pipe or a network file), so each check runs on a
/// dedicated thread that is given one handle at a time. Once the check says its lookup is starting, the lookup is given a limited
/// time; when that runs out, the lookup, if it is running, is cancelled. A worker whose check returns after the cancel is kept, and
/// the handle keeps a disk name the check returned, while a failure or a skip after a cancel counts as a timed-out, unnamed handle;
/// a check that still does not return is abandoned, recorded in the caller's <see cref="LookupAccount"/> until it returns, and a
/// fresh worker takes its place. A handle the worker has not claimed within its start timeout is withdrawn, so it is never checked,
/// and its worker is replaced as an abandoned one is. Workers are threads rather than tasks, so a hung lookup never holds a
/// thread-pool thread.
/// </summary>
internal static class HandleNaming
{
    /// <summary>Workers naming handles at once.</summary>
    internal const int DefaultWorkers = 4;

    /// <summary>Gets how long one name lookup may take, from its start, before the handle counts as unnamed.</summary>
    internal static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Gets how long a worker may take to start a lookup, which covers its thread being scheduled and the handle being duplicated;
    /// past it, the handle is treated as a timed-out lookup.
    /// </summary>
    internal static TimeSpan DefaultStartTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Checks every handle on <see cref="NamingOptions.Workers"/> workers and returns the disk files' names by process. A skipped
    /// handle adds nothing; a failed one adds its process to the failed ones; a lookup that times out gives a <see langword="null"/>
    /// name, unless its check, once cancelled, returns a disk name, which is kept; a failure, a skip or a throw after a cancel stays a
    /// <see langword="null"/> name, while a late result with no cancel sent, the lookup not running, is recorded as it is. A handle
    /// its worker had not claimed when its driver gave up on it is never checked, and that worker is replaced; one whose check was
    /// claimed and did not return after its cancel is recorded in <paramref name="account"/> until it returns, and its worker is
    /// replaced too. Once a check throws otherwise, no driver takes another handle and this rethrows. Every worker has finished or
    /// been abandoned when this returns or throws.
    /// </summary>
    /// <typeparam name="T">What identifies a handle to <paramref name="check"/>.</typeparam>
    /// <param name="handles">Each handle with its process.</param>
    /// <param name="check">Checks one handle; it runs on a worker thread.</param>
    /// <param name="options">The workers and the time limits.</param>
    /// <param name="cancel">Cancels a timed-out lookup, given the worker's thread; <see cref="CancelLookup"/> outside tests.</param>
    /// <param name="account">Records each check given up on while it runs; the caller's, read before it decides a folder is free.</param>
    /// <returns>The disk files' names by process, in no particular order, and the processes with a failed check.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="NamingOptions.Workers"/> is less than 1.</exception>
    internal static NamedHandles Name<T>(
        IReadOnlyList<(int Pid, T Handle)> handles,
        HandleCheck<T> check,
        NamingOptions options,
        Action<SafeHandle> cancel,
        LookupAccount account
    )
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Workers, 1);
        if (handles.Count == 0)
        {
            return new NamedHandles([], new HashSet<int>());
        }
        var queue = new ConcurrentQueue<(int Pid, T Handle)>(handles);
        var stop = new StopSignal();
        var drivers = new List<Driver<T>>();
        for (int index = 0; index < options.Workers; index++)
        {
            drivers.Add(Driver<T>.Start(queue, new DriverSetup<T>(check, options, cancel, account), stop));
        }
        drivers.ForEach(driver => driver.Join());
        drivers.Find(driver => driver.Failure is not null)?.Failure?.Throw();
        return new NamedHandles([.. drivers.SelectMany(driver => driver.Names)], drivers.SelectMany(driver => driver.Failed).ToHashSet());
    }

    /// <summary>
    /// Cancels the synchronous I/O pending on a worker's thread; a failure is traced, never thrown. <c>ERROR_NOT_FOUND</c> means no
    /// synchronous I/O was pending on the thread: the lookup ended before the cancel reached it, or the worker is blocked before it
    /// issued a request of its own, which a cancel cannot reach. It is traced as such, not as a failure to cancel.
    /// </summary>
    /// <param name="thread">The worker's thread, open with <c>THREAD_TERMINATE</c> access.</param>
    internal static void CancelLookup(SafeHandle thread)
    {
        if (PInvoke.CancelSynchronousIo(thread))
        {
            return;
        }
        int error = Marshal.GetLastPInvokeError();
        if (error == (int)WIN32_ERROR.ERROR_NOT_FOUND)
        {
            Trace.WriteLine("no pending I/O to cancel on a timed-out name lookup's thread: it ended, or it is blocked before its request");
            return;
        }
        Trace.WriteLine($"cannot cancel a timed-out name lookup: {new Win32Exception(error).Message}");
    }

    /// <summary>Set once a driver fails, so the other drivers take no more handles.</summary>
    private sealed class StopSignal
    {
        private volatile bool requested;

        /// <summary>Gets a value indicating whether a driver has failed.</summary>
        internal bool Requested => requested;

        internal void Request() => requested = true;
    }

    /// <summary>What every driver of one <see cref="Name{T}"/> call works with.</summary>
    /// <typeparam name="T">What identifies a handle to <paramref name="Check"/>.</typeparam>
    /// <param name="Check">Checks one handle.</param>
    /// <param name="Options">The workers and the time limits.</param>
    /// <param name="Cancel">Cancels a timed-out lookup.</param>
    /// <param name="Account">Records each check given up on while it runs.</param>
    private sealed record DriverSetup<T>(HandleCheck<T> Check, NamingOptions Options, Action<SafeHandle> Cancel, LookupAccount Account);

    /// <summary>
    /// One handle given to a worker: where it is in its life, whether its lookup has started, and its result. It is posted, then
    /// either claimed by the worker, which then checks it and completes <see cref="ReturnedTask"/>, or withdrawn by its driver before
    /// the worker claims it, so it is never checked.
    /// </summary>
    private sealed class Pending
    {
        private const int Posted = 0;
        private const int Claimed = 1;
        private const int Withdrawn = 2;

        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CheckedHandle> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int state = Posted;

        /// <summary>Gets a task that completes once the check's lookup starts.</summary>
        internal Task Started => started.Task;

        /// <summary>Gets the check's result; a check that throws faults it.</summary>
        internal Task<CheckedHandle> Done => done.Task;

        /// <summary>Gets a task that completes once the check returns or throws; it never faults.</summary>
        internal Task ReturnedTask => returned.Task;

        /// <summary>Claims the handle for the worker, unless its driver has withdrawn it.</summary>
        /// <returns><see langword="true"/> when the worker is to check it.</returns>
        internal bool TryClaim() => Interlocked.CompareExchange(ref state, Claimed, Posted) == Posted;

        /// <summary>Withdraws the handle, unless the worker has claimed it.</summary>
        /// <returns><see langword="true"/> when the check will never run.</returns>
        internal bool TryWithdraw() => Interlocked.CompareExchange(ref state, Withdrawn, Posted) == Posted;

        internal void MarkStarted() => started.TrySetResult();

        internal void Finish(CheckedHandle found)
        {
            _ = done.TrySetResult(found);
            _ = returned.TrySetResult();
        }

        internal void Fail(Exception error)
        {
            _ = done.TrySetException(error);
            _ = returned.TrySetResult();
        }
    }

    /// <summary>
    /// A thread that takes handles from the shared queue and hands each to its worker, cancelling a lookup that times out and
    /// replacing the worker when the cancelled lookup does not return.
    /// </summary>
    private sealed class Driver<T>
    {
        private readonly ConcurrentQueue<(int Pid, T Handle)> queue;
        private readonly DriverSetup<T> setup;
        private readonly StopSignal stop;
        private readonly List<NamedHandle> names = [];
        private readonly HashSet<int> failed = [];
        private readonly Thread thread;

        private Driver(ConcurrentQueue<(int Pid, T Handle)> queue, DriverSetup<T> setup, StopSignal stop)
        {
            this.queue = queue;
            this.setup = setup;
            this.stop = stop;
            thread = new Thread(Drive) { IsBackground = true, Name = "handle-naming driver" };
        }

        private TimeSpan Timeout => setup.Options.Timeout;

        /// <summary>Gets the names this driver found.</summary>
        internal IReadOnlyList<NamedHandle> Names => names;

        /// <summary>Gets the processes with a handle whose check failed at once.</summary>
        internal IReadOnlyCollection<int> Failed => failed;

        /// <summary>Gets what made this driver stop early, if anything.</summary>
        internal ExceptionDispatchInfo? Failure { get; private set; }

        /// <summary>Starts a driver.</summary>
        /// <param name="queue">The handles still to check, shared by every driver.</param>
        /// <param name="setup">The check, the limits, the cancel and the account.</param>
        /// <param name="stop">Shared by every driver; set by the first that fails.</param>
        /// <returns>The running driver.</returns>
        internal static Driver<T> Start(ConcurrentQueue<(int Pid, T Handle)> queue, DriverSetup<T> setup, StopSignal stop)
        {
            var driver = new Driver<T>(queue, setup, stop);
            driver.thread.Start();
            return driver;
        }

        /// <summary>Waits until the driver has stopped: the queue is empty, or a check failed.</summary>
        internal void Join() => thread.Join();

        private void Drive()
        {
            Worker<T>? worker = null;
            // Every exception is carried to Name, since one escaping this thread would end the process.
            try
            {
                worker = NewWorker();
                while (!stop.Requested && queue.TryDequeue(out (int Pid, T Handle) next))
                {
                    Pending pending = worker.Post(next.Handle);
                    if (Settled(pending))
                    {
                        Record(next.Pid, pending.Done.GetAwaiter().GetResult());
                        continue;
                    }
                    // A handle the worker has not claimed yet is never checked, so no duplicate of it is ever made; a worker that has
                    // not claimed in time is replaced, so it costs one start timeout, not one per handle left.
                    if (pending.TryWithdraw())
                    {
                        names.Add(new NamedHandle(next.Pid, null, 0));
                        Trace.WriteLine($"a worker did not start naming a handle of pid {next.Pid} in time; replacing it");
                        worker = Replace(worker);
                        continue;
                    }
                    if (!CancelledInTime(worker, pending, next.Pid, out bool cancelSent))
                    {
                        names.Add(new NamedHandle(next.Pid, null, 0));
                        Trace.WriteLine($"naming a handle of pid {next.Pid} timed out and did not stop; abandoning its worker");
                        setup.Account.AddUnsettled(pending.ReturnedTask);
                        worker = Replace(worker);
                        continue;
                    }
                    RecordLate(next.Pid, pending, cancelSent);
                }
            }
            catch (Exception error)
            {
                stop.Request();
                Failure = ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                worker?.Abandon();
            }
        }

        private Worker<T> NewWorker() => new(setup.Check, setup.Options);

        /// <summary>Abandons <paramref name="worker"/> and starts a fresh one in its place.</summary>
        private Worker<T> Replace(Worker<T> worker)
        {
            worker.Abandon();
            return NewWorker();
        }

        /// <summary>
        /// Keeps what a check that returned only after its timeout, but within the wait that followed, found. When no cancel was sent,
        /// its result is genuine and is recorded as any other. After a cancel, a disk file's name is kept, as the check did read it,
        /// while a failure or a skip, which the cancel itself may have caused, leaves the handle unnamed, a timed-out lookup. A check
        /// that threw leaves it unnamed either way.
        /// </summary>
        private void RecordLate(int pid, Pending pending, bool cancelSent)
        {
            if (pending.Done.IsCompletedSuccessfully && (!cancelSent || pending.Done.Result.Outcome == CheckOutcome.Disk))
            {
                Record(pid, pending.Done.Result);
                return;
            }
            names.Add(new NamedHandle(pid, null, 0));
        }

        /// <summary>Keeps what a check that returned in time found: a disk file's name, or its process as failed.</summary>
        private void Record(int pid, CheckedHandle found)
        {
            if (found.Outcome == CheckOutcome.Disk)
            {
                names.Add(new NamedHandle(pid, found.Name, found.Handle));
            }
            else if (found.Outcome == CheckOutcome.Failed)
            {
                _ = failed.Add(pid);
            }
        }

        /// <summary>
        /// Waits for the check to start its lookup, at most <see cref="NamingOptions.StartTimeout"/>, then for its result, at most the
        /// timeout.
        /// </summary>
        /// <returns><see langword="true"/> when the check returned or threw in time.</returns>
        private bool Settled(Pending pending) =>
            Task.WaitAny([pending.Started, pending.Done], setup.Options.StartTimeout) >= 0 && Task.WaitAny([pending.Done], Timeout) >= 0;

        /// <summary>
        /// Cancels the worker's timed-out lookup, if it has not ended, and waits, at most the timeout, for its check to return. A check
        /// that throws after the cancel, as an aborted lookup may, is traced rather than rethrown: its handle counts as unnamed.
        /// </summary>
        /// <param name="worker">The worker running the check.</param>
        /// <param name="pending">The handle given to it.</param>
        /// <param name="pid">The handle's process.</param>
        /// <param name="cancelSent">Set to whether a cancel was sent: none is when no lookup was running, as it had ended or not yet
        /// begun.</param>
        /// <returns><see langword="true"/> when the check returned or threw, so the worker can be kept.</returns>
        private bool CancelledInTime(Worker<T> worker, Pending pending, int pid, out bool cancelSent)
        {
            cancelSent = worker.CancelRunningLookup(setup.Cancel);
            NoteCancelAttempt(cancelSent);
            if (Task.WaitAny([pending.Done], Timeout) < 0)
            {
                return false;
            }
            if (pending.Done.Exception is { } error)
            {
                Trace.WriteLine($"the cancelled name lookup of a handle of pid {pid} failed: {error.GetBaseException().Message}");
            }
            return true;
        }

        /// <summary>Tells the test seam <see cref="NamingOptions.AfterCancelAttempt"/>, if any, whether a cancel was sent; a throw from it
        /// is traced and never escapes this thread.</summary>
        private void NoteCancelAttempt(bool sent)
        {
            try
            {
                setup.Options.AfterCancelAttempt?.Invoke(sent);
            }
            catch (Exception error)
            {
                Trace.WriteLine($"the cancel-attempt hook failed: {error.Message}");
            }
        }
    }

    /// <summary>
    /// A background thread checking handles one at a time, so it holds at most one duplicate open. Once abandoned it ends after its
    /// current check returns, if that ever happens; until then the duplicate it is naming stays open, at worst until this process exits,
    /// and the check counts as unsettled in the caller's account. A handle its driver withdrew before the worker claimed it is passed
    /// over without being checked.
    /// </summary>
    private sealed class Worker<T>
    {
        private readonly BlockingCollection<(T Handle, Pending Pending)> inbox = [];
        private readonly HandleCheck<T> check;
        private readonly NamingOptions options;
        private readonly Lock lookupLock = new();
        private volatile SafeFileHandle? nativeThread;
        private bool lookupRunning;

        internal Worker(HandleCheck<T> check, NamingOptions options)
        {
            this.check = check;
            this.options = options;
            new Thread(Run) { IsBackground = true, Name = "handle-naming worker" }.Start();
        }

        /// <summary>Gives the worker a handle to check.</summary>
        /// <param name="handle">The handle.</param>
        /// <returns>The check's progress and result.</returns>
        internal Pending Post(T handle)
        {
            var pending = new Pending();
            inbox.Add((handle, pending));
            return pending;
        }

        /// <summary>
        /// Cancels the current check's lookup with <paramref name="cancel"/>, given the worker's thread, unless the lookup has ended or
        /// the thread could not be opened. The check cannot end its lookup while the cancel runs, so a cancel never reaches what the
        /// worker does after the lookup.
        /// </summary>
        /// <param name="cancel">Cancels the synchronous I/O pending on a thread.</param>
        /// <returns><see langword="true"/> when <paramref name="cancel"/> was called.</returns>
        internal bool CancelRunningLookup(Action<SafeHandle> cancel)
        {
            lock (lookupLock)
            {
                if (lookupRunning && nativeThread is { } native)
                {
                    cancel(native);
                    return true;
                }
                return false;
            }
        }

        /// <summary>Lets the worker's thread end once its current check, if any, returns.</summary>
        internal void Abandon() => inbox.CompleteAdding();

        private void Run()
        {
            nativeThread = OpenOwnThread();
            try
            {
                foreach ((T handle, Pending pending) in inbox.GetConsumingEnumerable())
                {
                    if (Claim(pending))
                    {
                        CheckOne(handle, pending);
                    }
                }
            }
            finally
            {
                nativeThread?.Dispose();
            }
        }

        /// <summary>
        /// Claims the handle unless its driver withdrew it, calling the test hooks around the claim. A hook that throws fails the handle
        /// as a throwing check would, unless the driver withdrew it first; the exception never escapes this thread.
        /// </summary>
        /// <returns><see langword="true"/> when the worker is to check the handle.</returns>
        private bool Claim(Pending pending)
        {
            bool claimed = false;
            try
            {
                options.BeforeClaim?.Invoke();
                claimed = pending.TryClaim();
                options.AfterClaim?.Invoke(claimed);
                return claimed;
            }
            catch (Exception error)
            {
                if (claimed || pending.TryClaim())
                {
                    pending.Fail(error);
                }
                else
                {
                    Trace.WriteLine($"a claim hook failed on a withdrawn handle: {error.Message}");
                }
                return false;
            }
        }

        /// <summary>Runs one check with its lookup marked as running until the check ends it or returns, then hands over the result.</summary>
        private void CheckOne(T handle, Pending pending)
        {
            SetLookupRunning(true);
            CheckedHandle found;
            try
            {
                found = check(handle, pending.MarkStarted, EndLookup);
            }
            catch (Exception error)
            {
                // The exception is handed to the driver, which rethrows it unless the lookup was cancelled; an abandoned worker's
                // result is never read.
                EndLookup();
                pending.Fail(error);
                return;
            }
            EndLookup();
            pending.Finish(found);
        }

        private void EndLookup() => SetLookupRunning(false);

        private void SetLookupRunning(bool running)
        {
            lock (lookupLock)
            {
                lookupRunning = running;
            }
        }

        /// <summary>This thread, open with the access <c>CancelSynchronousIo</c> needs, or <see langword="null"/> when it cannot be.</summary>
        private static SafeFileHandle? OpenOwnThread()
        {
            SafeFileHandle own = PInvoke.OpenThread_SafeHandle(THREAD_ACCESS_RIGHTS.THREAD_TERMINATE, false, PInvoke.GetCurrentThreadId());
            if (!own.IsInvalid)
            {
                return own;
            }
            Trace.WriteLine($"cannot open a handle-naming thread to cancel its lookups: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            own.Dispose();
            return null;
        }
    }
}
