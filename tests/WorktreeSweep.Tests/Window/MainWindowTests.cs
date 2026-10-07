using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using WorktreeSweep.Report;
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
    /// The window builds from its XAML on an STA thread, every converter and style resolving, and takes its title from the root;
    /// it is closed without ever being shown.
    /// </summary>
    [Fact]
    public void MainWindowLoadsItsXaml()
    {
        ExceptionDispatchInfo? failure = null;
        string? title = null;
        var thread = new Thread(() =>
        {
            try
            {
                var main = new MainViewModel(_ => TimeSpan.Zero);
                var window = new MainWindow(main, new WindowWorkers(main, IdleSeams()), @"C:\fixture");
                title = window.Title;
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
        Assert.Equal(@"worktree-sweep — C:\fixture", title);
    }

    /// <summary>Seams that start no job and touch no disk: a scan that finds nothing, never run.</summary>
    /// <returns>The seams.</returns>
    private static WindowSeams IdleSeams() =>
        new()
        {
            Scan = _ => ReportSamples.ReportOf(),
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
