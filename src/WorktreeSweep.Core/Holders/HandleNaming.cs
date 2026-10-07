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
internal readonly record struct CheckedHandle(CheckOutcome Outcome, string? Name)
{
    /// <summary>Gets the result for a handle that is not a file on disk.</summary>
    internal static CheckedHandle Skipped => new(CheckOutcome.Skipped, null);

    /// <summary>Gets the result for a handle that could not be duplicated or named.</summary>
    internal static CheckedHandle Failed => new(CheckOutcome.Failed, null);

    /// <summary>The result for a named file on disk.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The result.</returns>
    internal static CheckedHandle Disk(string name) => new(CheckOutcome.Disk, name);
}

/// <summary>What naming a set of handles found.</summary>
/// <param name="Names">Each named disk handle's process and name; the name is <see langword="null"/> when its lookup timed out.</param>
/// <param name="Failed">Processes with a handle whose check failed at once.</param>
internal sealed record NamedHandles(IReadOnlyList<(int Pid, string? Name)> Names, IReadOnlySet<int> Failed);

/// <summary>Checks one handle on a <see cref="HandleNaming"/> worker.</summary>
/// <typeparam name="T">What identifies the handle.</typeparam>
/// <param name="handle">The handle.</param>
/// <param name="lookupStarting">Called once the check is about to do what may hang; the lookup's time counts from there.</param>
/// <param name="lookupEnded">
/// Called once nothing that may hang is left, before the check releases what it holds; a timed-out lookup is not cancelled after it.
/// </param>
/// <returns>What the check found.</returns>
internal delegate CheckedHandle HandleCheck<in T>(T handle, Action lookupStarting, Action lookupEnded);

/// <summary>
/// Names handles on abandonable workers: a name lookup can hang forever (on a pipe or a network file), so each check runs on a
/// dedicated thread that is given one handle at a time. Once the check says its lookup is starting, the lookup is given a limited
/// time; when that runs out, the handle counts as unnamed and the lookup, if it has not ended, is cancelled. A worker whose check
/// returns after the cancel is kept; one that still does not return is abandoned and a fresh worker takes its place. Workers are
/// threads rather than tasks, so a hung lookup never holds a thread-pool thread.
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
    internal static TimeSpan StartTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Checks every handle on <paramref name="workers"/> workers and returns the disk files' names by process. A skipped handle adds
    /// nothing; a failed one adds its process to the failed ones; a lookup that times out gives a <see langword="null"/> name, even
    /// when its check throws once cancelled. Once a check throws otherwise, no driver takes another handle and this rethrows. Every
    /// worker has finished or been abandoned when this returns or throws.
    /// </summary>
    /// <typeparam name="T">What identifies a handle to <paramref name="check"/>.</typeparam>
    /// <param name="handles">Each handle with its process.</param>
    /// <param name="check">Checks one handle; it runs on a worker thread.</param>
    /// <param name="workers">Workers checking handles at once.</param>
    /// <param name="timeout">How long one lookup may take from its start, and how long a cancelled lookup may take to return.</param>
    /// <param name="cancel">Cancels a timed-out lookup, given the worker's thread; <see cref="CancelLookup"/> outside tests.</param>
    /// <returns>The disk files' names by process, in no particular order, and the processes with a failed check.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workers"/> is less than 1.</exception>
    internal static NamedHandles Name<T>(
        IReadOnlyList<(int Pid, T Handle)> handles,
        HandleCheck<T> check,
        int workers,
        TimeSpan timeout,
        Action<SafeHandle> cancel
    )
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        if (handles.Count == 0)
        {
            return new NamedHandles([], new HashSet<int>());
        }
        var queue = new ConcurrentQueue<(int Pid, T Handle)>(handles);
        var stop = new StopSignal();
        var drivers = new List<Driver<T>>();
        for (int index = 0; index < workers; index++)
        {
            drivers.Add(Driver<T>.Start(queue, check, timeout, cancel, stop));
        }
        drivers.ForEach(driver => driver.Join());
        drivers.Find(driver => driver.Failure is not null)?.Failure?.Throw();
        return new NamedHandles([.. drivers.SelectMany(driver => driver.Names)], drivers.SelectMany(driver => driver.Failed).ToHashSet());
    }

    /// <summary>
    /// Cancels the synchronous I/O pending on a worker's thread; a failure is traced, never thrown. <c>ERROR_NOT_FOUND</c> means the
    /// lookup ended before the cancel reached it, so it is not a failure.
    /// </summary>
    /// <param name="thread">The worker's thread, open with <c>THREAD_TERMINATE</c> access.</param>
    internal static void CancelLookup(SafeHandle thread)
    {
        if (PInvoke.CancelSynchronousIo(thread))
        {
            return;
        }
        int error = Marshal.GetLastPInvokeError();
        if (error != (int)WIN32_ERROR.ERROR_NOT_FOUND)
        {
            Trace.WriteLine($"cannot cancel a timed-out name lookup: {new Win32Exception(error).Message}");
        }
    }

    /// <summary>Set once a driver fails, so the other drivers take no more handles.</summary>
    private sealed class StopSignal
    {
        private volatile bool requested;

        /// <summary>Gets a value indicating whether a driver has failed.</summary>
        internal bool Requested => requested;

        internal void Request() => requested = true;
    }

    /// <summary>One handle given to a worker: whether its lookup has started, and its result.</summary>
    private sealed class Pending
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CheckedHandle> done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task that completes once the check's lookup starts.</summary>
        internal Task Started => started.Task;

        /// <summary>Gets the check's result; a check that throws faults it.</summary>
        internal Task<CheckedHandle> Done => done.Task;

        internal void MarkStarted() => started.TrySetResult();

        internal void Finish(CheckedHandle found) => done.TrySetResult(found);

        internal void Fail(Exception error) => done.TrySetException(error);
    }

    /// <summary>
    /// A thread that takes handles from the shared queue and hands each to its worker, cancelling a lookup that times out and
    /// replacing the worker when the cancelled lookup does not return.
    /// </summary>
    private sealed class Driver<T>
    {
        private readonly ConcurrentQueue<(int Pid, T Handle)> queue;
        private readonly HandleCheck<T> check;
        private readonly TimeSpan timeout;
        private readonly Action<SafeHandle> cancel;
        private readonly StopSignal stop;
        private readonly List<(int Pid, string? Name)> names = [];
        private readonly HashSet<int> failed = [];
        private readonly Thread thread;

        private Driver(ConcurrentQueue<(int Pid, T Handle)> queue, HandleCheck<T> check, TimeSpan timeout, Action<SafeHandle> cancel, StopSignal stop)
        {
            this.queue = queue;
            this.check = check;
            this.timeout = timeout;
            this.cancel = cancel;
            this.stop = stop;
            thread = new Thread(Drive) { IsBackground = true, Name = "handle-naming driver" };
        }

        /// <summary>Gets the names this driver found.</summary>
        internal IReadOnlyList<(int Pid, string? Name)> Names => names;

        /// <summary>Gets the processes with a handle whose check failed at once.</summary>
        internal IReadOnlyCollection<int> Failed => failed;

        /// <summary>Gets what made this driver stop early, if anything.</summary>
        internal ExceptionDispatchInfo? Failure { get; private set; }

        /// <summary>Starts a driver.</summary>
        /// <param name="queue">The handles still to check, shared by every driver.</param>
        /// <param name="check">Checks one handle.</param>
        /// <param name="timeout">How long one lookup may take.</param>
        /// <param name="cancel">Cancels a timed-out lookup.</param>
        /// <param name="stop">Shared by every driver; set by the first that fails.</param>
        /// <returns>The running driver.</returns>
        internal static Driver<T> Start(
            ConcurrentQueue<(int Pid, T Handle)> queue,
            HandleCheck<T> check,
            TimeSpan timeout,
            Action<SafeHandle> cancel,
            StopSignal stop
        )
        {
            var driver = new Driver<T>(queue, check, timeout, cancel, stop);
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
                worker = new Worker<T>(check);
                while (!stop.Requested && queue.TryDequeue(out (int Pid, T Handle) next))
                {
                    Pending pending = worker.Post(next.Handle);
                    if (Settled(pending))
                    {
                        Record(next.Pid, pending.Done.GetAwaiter().GetResult());
                        continue;
                    }
                    names.Add((next.Pid, null));
                    if (!CancelledInTime(worker, pending, next.Pid))
                    {
                        Trace.WriteLine($"naming a handle of pid {next.Pid} timed out and did not stop; abandoning its worker");
                        worker.Abandon();
                        worker = new Worker<T>(check);
                    }
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

        /// <summary>Keeps what a check that returned in time found: a disk file's name, or its process as failed.</summary>
        private void Record(int pid, CheckedHandle found)
        {
            if (found.Outcome == CheckOutcome.Disk)
            {
                names.Add((pid, found.Name));
            }
            else if (found.Outcome == CheckOutcome.Failed)
            {
                _ = failed.Add(pid);
            }
        }

        /// <summary>
        /// Waits for the check to start its lookup, at most <see cref="StartTimeout"/>, then for its result, at most the timeout.
        /// </summary>
        /// <returns><see langword="true"/> when the check returned or threw in time.</returns>
        private bool Settled(Pending pending) =>
            Task.WaitAny([pending.Started, pending.Done], StartTimeout) >= 0 && Task.WaitAny([pending.Done], timeout) >= 0;

        /// <summary>
        /// Cancels the worker's timed-out lookup, if it has not ended, and waits, at most the timeout, for its check to return. A check
        /// that throws after the cancel, as an aborted lookup may, is traced rather than rethrown: its handle already counts as unnamed.
        /// </summary>
        /// <returns><see langword="true"/> when the check returned or threw, so the worker can be kept.</returns>
        private bool CancelledInTime(Worker<T> worker, Pending pending, int pid)
        {
            worker.CancelRunningLookup(cancel);
            if (Task.WaitAny([pending.Done], timeout) < 0)
            {
                return false;
            }
            if (pending.Done.Exception is { } error)
            {
                Trace.WriteLine($"the cancelled name lookup of a handle of pid {pid} failed: {error.GetBaseException().Message}");
            }
            return true;
        }
    }

    /// <summary>
    /// A background thread checking handles one at a time, so it holds at most one duplicate open. Once abandoned it ends after its
    /// current check returns, if that ever happens; until then the duplicate it is naming stays open, at worst until this process exits.
    /// </summary>
    private sealed class Worker<T>
    {
        private readonly BlockingCollection<(T Handle, Pending Pending)> inbox = [];
        private readonly HandleCheck<T> check;
        private readonly Lock lookupLock = new();
        private volatile SafeFileHandle? nativeThread;
        private bool lookupRunning;

        internal Worker(HandleCheck<T> check)
        {
            this.check = check;
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
        internal void CancelRunningLookup(Action<SafeHandle> cancel)
        {
            lock (lookupLock)
            {
                if (lookupRunning && nativeThread is { } native)
                {
                    cancel(native);
                }
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
                    CheckOne(handle, pending);
                }
            }
            finally
            {
                nativeThread?.Dispose();
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
