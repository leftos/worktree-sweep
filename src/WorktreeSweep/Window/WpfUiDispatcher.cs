using System.Windows.Threading;

namespace WorktreeSweep.Window;

/// <summary>The window's UI thread, through its WPF <see cref="Dispatcher"/>.</summary>
/// <param name="dispatcher">The dispatcher of the thread the window runs on.</param>
public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    /// <inheritdoc/>
    public void Post(Action action) => _ = dispatcher.BeginInvoke(action);

    /// <inheritdoc/>
    public void Invoke(Action action) => dispatcher.Invoke(action);
}
