using WorktreeSweep.Recycle;
using WorktreeSweep.Removal;

namespace WorktreeSweep.Tests;

/// <summary>Which Shell HRESULTs mean a lock, and retrying a recycle while it fails with one.</summary>
public sealed class RecycleRetryTests
{
    /// <summary><c>COPYENGINE_E_ACCESS_DENIED_SRC</c>, <c>sherrors.h</c>.</summary>
    private const int AccessDeniedSrc = unchecked((int)0x80270021);

    /// <summary><c>COPYENGINE_E_SHARING_VIOLATION_SRC</c>, <c>sherrors.h</c>.</summary>
    private const int SharingViolationSrc = unchecked((int)0x80270027);

    /// <summary><c>COPYENGINE_E_SHARING_VIOLATION_DEST</c>, <c>sherrors.h</c>.</summary>
    private const int SharingViolationDest = unchecked((int)0x80270028);

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION)</c>.</summary>
    private const int Win32SharingViolation = unchecked((int)0x80070020);

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED)</c>.</summary>
    private const int Win32AccessDenied = unchecked((int)0x80070005);

    /// <summary><c>COPYENGINE_E_USER_CANCELLED</c>, <c>sherrors.h</c>.</summary>
    private const int UserCancelled = unchecked((int)0x80270000);

    /// <summary><c>E_FAIL</c>, <c>winerror.h</c>.</summary>
    private const int Fail = unchecked((int)0x80004005);

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)];

    /// <summary>
    /// From <c>PerformOperations</c>, the Win32 and copy-engine sharing violations and access denials are locks; a cancel or a generic
    /// failure is not.
    /// </summary>
    [Fact]
    public void CopyengineLockCodesAreLocked()
    {
        foreach (int hr in new[] { SharingViolationSrc, SharingViolationDest, AccessDeniedSrc, Win32SharingViolation, Win32AccessDenied })
        {
            Assert.True(LockedHresults.IsLocked(ShellCall.PerformOperations, hr), $"0x{hr:X8} should be locked");
        }
        foreach (int hr in new[] { UserCancelled, Fail })
        {
            Assert.False(LockedHresults.IsLocked(ShellCall.PerformOperations, hr), $"0x{hr:X8} should not be locked");
        }
    }

    /// <summary>
    /// An access denied from any call before <c>PerformOperations</c>, such as naming the folder as a Shell item, is not a lock: no
    /// other process holds anything yet.
    /// </summary>
    [Fact]
    public void OnlyPerformOperationsFailsWithALock()
    {
        foreach (ShellCall call in Enum.GetValues<ShellCall>().Where(call => call != ShellCall.PerformOperations))
        {
            Assert.False(LockedHresults.IsLocked(call, Win32AccessDenied), $"access denied from {call} should not be locked");
        }
        Assert.True(LockedHresults.IsLocked(ShellCall.PerformOperations, Win32AccessDenied));
    }

    /// <summary>A lock that never clears is tried four times, 250, 500 and 1000 ms apart, and the last lock is thrown.</summary>
    [Fact]
    public void LockedRecycleIsRetriedWithBackoff()
    {
        var run = new Run([Locked, Locked, Locked, Locked]);

        LockedException error = Assert.Throws<LockedException>(() => run.Execute());

        Assert.Equal(AccessDeniedSrc, error.InnerException?.HResult);
        Assert.Equal(4, run.Attempts);
        Assert.Equal(Backoff, run.Sleeps);
    }

    /// <summary>A lock that clears on the second try returns that try's result after one 250 ms wait.</summary>
    [Fact]
    public void TransientLockClearsOnRetry()
    {
        var run = new Run([Locked, () => false]);

        bool aborted = run.Execute();

        Assert.False(aborted);
        Assert.Equal(2, run.Attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(250)], run.Sleeps);
    }

    /// <summary>A failure that is not a lock is thrown at once, even with a lock's HRESULT.</summary>
    /// <param name="hr">The failure's HRESULT.</param>
    [Theory]
    [InlineData(Fail)]
    [InlineData(Win32AccessDenied)]
    public void NonLockErrorIsNotRetried(int hr)
    {
        var run = new Run([() => throw new IOException("failed", hr), Locked]);

        IOException error = Assert.Throws<IOException>(() => run.Execute());

        Assert.Equal(hr, error.HResult);
        Assert.Equal(1, run.Attempts);
        Assert.Empty(run.Sleeps);
    }

    private static bool Locked() => throw new LockedException("tree", firstLockedFile: null, new IOException("locked", AccessDeniedSrc));

    /// <summary>Runs <see cref="RecycleRetry.WhileLocked"/> over scripted attempts, recording the attempts and the sleeps.</summary>
    /// <param name="outcomes">Each attempt's outcome in order; an attempt past the end returns <see langword="false"/>.</param>
    private sealed class Run(IReadOnlyList<Func<bool>> outcomes)
    {
        public int Attempts { get; private set; }

        public List<TimeSpan> Sleeps { get; } = [];

        public bool Execute() => RecycleRetry.WhileLocked(Attempt, Sleeps.Add);

        private bool Attempt()
        {
            int index = Attempts;
            Attempts++;
            return index < outcomes.Count && outcomes[index]();
        }
    }
}
