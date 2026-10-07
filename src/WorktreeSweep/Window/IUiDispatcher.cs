namespace WorktreeSweep.Window;

/// <summary>Runs work on the window's UI thread, from any thread.</summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> to run on the UI thread and returns at once.</summary>
    /// <param name="action">The work.</param>
    void Post(Action action);

    /// <summary>Runs <paramref name="action"/> on the UI thread and returns once it has run.</summary>
    /// <param name="action">The work.</param>
    void Invoke(Action action);
}
