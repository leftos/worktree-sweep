using WorktreeSweep.Removal;

namespace WorktreeSweep.Recycle;

/// <summary>
/// Retries a Shell recycle that failed with a lock: a tree written a moment earlier can fail briefly where a scanner holds freshly
/// written files for seconds, as on <c>C:</c>, where <c>%TEMP%</c> is.
/// </summary>
public static class RecycleRetry
{
    /// <summary>Gets the waits before each retry: 250 ms, 500 ms and 1 s, about 1.75 s in all.</summary>
    public static IReadOnlyList<TimeSpan> Delays { get; } = [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)];

    /// <summary>
    /// Runs <paramref name="attempt"/>, and again after each of the <see cref="Delays"/> while it throws a
    /// <see cref="LockedException"/>. Any other result or exception ends the loop at once; the last attempt's result or exception is
    /// the outcome.
    /// </summary>
    /// <typeparam name="T">What an attempt returns.</typeparam>
    /// <param name="attempt">One try.</param>
    /// <param name="sleep">Waits for the given time; <see cref="Thread.Sleep(TimeSpan)"/> outside tests.</param>
    /// <returns>What the first attempt to succeed returned.</returns>
    public static T WhileLocked<T>(Func<T> attempt, Action<TimeSpan> sleep)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(sleep);
        for (int retry = 0; ; retry++)
        {
            try
            {
                return attempt();
            }
            catch (LockedException) when (retry < Delays.Count)
            {
                sleep(Delays[retry]);
            }
        }
    }
}
