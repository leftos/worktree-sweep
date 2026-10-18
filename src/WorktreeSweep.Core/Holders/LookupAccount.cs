namespace WorktreeSweep.Holders;

/// <summary>
/// The name lookups a caller gave up on while their checks were still running: each such check may still hold a duplicate of another
/// process's handle in this process, until it returns. The caller creates one, passes it to every <see cref="HandleNaming.Name{T}"/>
/// call whose lookups it answers for, and reads it before it decides that nothing holds a folder. It is never shared between callers.
/// </summary>
internal sealed class LookupAccount
{
    private readonly Lock sync = new();
    private readonly List<Task> unsettled = [];

    /// <summary>Gets how many checks this account records that have not returned yet.</summary>
    internal int Unsettled
    {
        get
        {
            lock (sync)
            {
                return unsettled.Count(returned => !returned.IsCompleted);
            }
        }
    }

    /// <summary>Records a check its caller gave up on while it was running, and forgets the recorded checks that have returned.</summary>
    /// <param name="returned">Completes once the check returns or throws; it never faults.</param>
    internal void AddUnsettled(Task returned)
    {
        ArgumentNullException.ThrowIfNull(returned);
        lock (sync)
        {
            _ = unsettled.RemoveAll(task => task.IsCompleted);
            unsettled.Add(returned);
        }
    }

    /// <summary>Waits, at most <paramref name="limit"/>, for every unsettled check to return.</summary>
    /// <param name="limit">How long to wait.</param>
    /// <returns>How many checks are still unsettled after the wait.</returns>
    internal int WaitForUnsettled(TimeSpan limit)
    {
        Task[] waiting;
        lock (sync)
        {
            waiting = [.. unsettled.Where(returned => !returned.IsCompleted)];
        }
        if (waiting.Length > 0)
        {
            _ = Task.WaitAll(waiting, limit);
        }
        return Unsettled;
    }
}
