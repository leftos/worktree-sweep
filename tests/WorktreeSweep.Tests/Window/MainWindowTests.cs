using System.Runtime.ExceptionServices;
using System.Windows.Interop;
using System.Windows.Threading;
using WorktreeSweep.Recycle;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;
using WorktreeSweep.Tests.ViewModels;
using WorktreeSweep.Unlock;
using WorktreeSweep.ViewModels;
using WorktreeSweep.Window;

namespace WorktreeSweep.Tests.Window;

/// <summary>The window's XAML, loaded without showing the window.</summary>
public sealed class MainWindowTests
{
    /// <summary>How long the window may take to build and close on its thread before the test fails.</summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The window builds from its XAML on an STA thread, every converter and style resolving, and binds its title to the view model's;
    /// it is closed without ever being shown.
    /// </summary>
    [Fact]
    public void MainWindowLoadsItsXaml()
    {
        ExceptionDispatchInfo? failure = null;
        string? title = null;
        string? boundTitle = null;
        var thread = new Thread(() =>
        {
            try
            {
                MainViewModel main = ViewModelSamples.Listed(ViewModelSamples.Worktree("a", MergeState.Ancestor, 10));
                var window = new MainWindow(main, new WindowWorkers(main, IdleSeams()));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                title = window.Title;
                boundTitle = main.Title;
                window.Close();
            }
#pragma warning disable CA1031 // The failure is rethrown on the test's thread below.
            catch (Exception error)
#pragma warning restore CA1031
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
        Assert.True(thread.Join(LoadTimeout), $"the window did not load within {LoadTimeout}");

        failure?.Throw();
        Assert.Equal(boundTitle, title);
        Assert.Equal(@"worktree-sweep — D:\", title);
    }

    /// <summary>
    /// Creating the window's handle points <see cref="WindowWorkers.OwnerWindow"/> at it, so the Shell's prompts are owned by the window
    /// without ever showing it.
    /// </summary>
    [Fact]
    public void CreatingTheWindowHandleSetsTheOwnerWindow()
    {
        ExceptionDispatchInfo? failure = null;
        nint owner = ShellRecycler.NoOwner;
        nint? handle = null;
        var thread = new Thread(() =>
        {
            try
            {
                var main = new MainViewModel(_ => TimeSpan.Zero);
                var workers = new WindowWorkers(main, IdleSeams());
                var window = new MainWindow(main, workers);
                handle = new WindowInteropHelper(window).EnsureHandle();
                owner = workers.OwnerWindow;
                window.Close();
            }
#pragma warning disable CA1031 // The failure is rethrown on the test's thread below.
            catch (Exception error)
#pragma warning restore CA1031
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
        Assert.True(thread.Join(LoadTimeout), $"the window did not load within {LoadTimeout}");

        failure?.Throw();
        Assert.NotNull(handle);
        Assert.NotEqual(ShellRecycler.NoOwner, owner);
        Assert.Equal(handle.Value, owner);
    }

    /// <summary>
    /// The window takes the Removing screen with a populated view model, off screen, as the app takes it, and pumps the dispatcher at
    /// context idle so that screen's bindings resolve against it: a read-only property bound two-way throws here rather than in the
    /// shown window.
    /// </summary>
    [Fact]
    public void MainWindowTakesTheRemovingScreenPopulated()
    {
        ExceptionDispatchInfo? failure = null;
        int rows = 0;
        string? heading = null;
        var thread = new Thread(() =>
        {
            try
            {
                MainViewModel main = ViewModelSamples.Listed(ViewModelSamples.Worktree("a", MergeState.Ancestor, 10));
                var window = new MainWindow(main, new WindowWorkers(main, IdleSeams()));

                ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy).Answer(true);
                main.Review?.ConfirmCommand.Execute(null);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

                rows = main.Removing?.Rows.Count ?? 0;
                heading = main.Removing?.Heading;
                window.Close();
            }
#pragma warning disable CA1031 // The failure is rethrown on the test's thread below.
            catch (Exception error)
#pragma warning restore CA1031
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
        Assert.True(thread.Join(LoadTimeout), $"the window did not load within {LoadTimeout}");

        failure?.Throw();
        Assert.Equal(1, rows);
        Assert.Equal("Removing 0 of 1", heading);
    }

    /// <summary>
    /// Seams that start no job and touch no disk: a scan that finds nothing, never run, and a recent folders file that does not exist
    /// and is never written, since no scan completes.
    /// </summary>
    /// <returns>The seams.</returns>
    private static WindowSeams IdleSeams() =>
        new()
        {
            Scan = (_, _, _) => ReportSamples.ReportOf(),
            Clock = () => TimeSpan.Zero,
            NowUnix = () => ReportSamples.Now,
            StartTimer = (_, _) => () => { },
            RecentFoldersPath = Path.Combine(Path.GetTempPath(), $"wts-no-recent-{Guid.NewGuid():N}.json"),
            ReadCapacity = _ => null,
            RemovePicks = (_, _, _, _, _) => [],
            OfferUnlock = _ => UnlockOutcome.Skipped,
            RunInBackground = _ => { },
            RunOnStaThread = _ => { },
            Dispatcher = new IdleDispatcher(),
        };

    /// <summary>A UI thread that is never reached, since no job runs.</summary>
    private sealed class IdleDispatcher : IUiDispatcher
    {
        /// <inheritdoc/>
        public void Post(Action action) => throw new InvalidOperationException("no job runs in this test");

        /// <inheritdoc/>
        public void Invoke(Action action) => throw new InvalidOperationException("no job runs in this test");
    }
}
