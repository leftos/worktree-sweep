using System.IO;
using WorktreeSweep.ViewModels;
using WorktreeSweep.Window;

namespace WorktreeSweep;

/// <summary>The default mode: the window, which scans the root, lets the user pick and removes the picks.</summary>
public static class WindowMode
{
    /// <summary>Shows the window on this thread, which must be STA, and waits for it to close.</summary>
    /// <param name="root">The folder to scan, as given on the command line.</param>
    /// <returns>0, once the window has closed; 1, with the reason on standard error and no window, when <paramref name="root"/> is
    /// not a usable path.</returns>
    public static int Run(string root)
    {
        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return Program.Failure;
        }
        var app = new App();
        var main = new MainViewModel(LocalOffset);
        var workers = new WindowWorkers(main, WindowSeams.Production(new WpfUiDispatcher(app.Dispatcher)));
        var window = new MainWindow(main, workers, fullRoot);
        workers.Start(fullRoot);
        app.Run(window);
        return 0;
    }

    /// <summary>The local time zone's offset from UTC at a Unix time.</summary>
    /// <param name="unixSeconds">The time, in Unix seconds.</param>
    /// <returns>The offset.</returns>
    private static TimeSpan LocalOffset(long unixSeconds) => TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
}
