using System.IO;
using WorktreeSweep.ViewModels;
using WorktreeSweep.Window;

namespace WorktreeSweep;

/// <summary>The default mode: the window, which scans a folder, lets the user pick and removes the picks.</summary>
public static class WindowMode
{
    /// <summary>
    /// Shows the window on this thread, which must be STA, and waits for it to close and for the cancel of a scan the close stopped
    /// to return, so the process does not exit before that scan's git processes are killed.
    /// </summary>
    /// <param name="root">
    /// The folder to scan, as given on the command line; <see langword="null"/> when none was given, which opens the Choose screen.
    /// </param>
    /// <returns>0, once the window has closed; 1, with the reason on standard error and no window, when <paramref name="root"/> is
    /// not a usable path.</returns>
    public static int Run(string? root)
    {
        string? fullRoot = null;
        if (root is not null)
        {
            try
            {
                fullRoot = Path.GetFullPath(root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine($"error: {error.Message}");
                return Program.Failure;
            }
        }
        var app = new App();
        var main = new MainViewModel(LocalOffset);
        var workers = new WindowWorkers(main, WindowSeams.Production(new WpfUiDispatcher(app.Dispatcher)));
        var window = new MainWindow(main, workers);
        Open(main, fullRoot, Environment.CurrentDirectory);
        app.Run(window);
        workers.CloseCancelled.Wait();
        return 0;
    }

    /// <summary>
    /// Puts the window on its first screen: with a root, the root is put in the folder box and scanned; without one, the Choose screen
    /// shows with <paramref name="currentDirectory"/> in the folder box.
    /// </summary>
    /// <param name="main">The window's view model, on the Choose screen.</param>
    /// <param name="root">The full path of the root given on the command line; <see langword="null"/> when none was.</param>
    /// <param name="currentDirectory">The folder the tool was started in.</param>
    public static void Open(MainViewModel main, string? root, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(currentDirectory);
        main.Folder = root ?? currentDirectory;
        if (root is not null)
        {
            main.ScanCommand.Execute(null);
        }
    }

    /// <summary>The local time zone's offset from UTC at a Unix time.</summary>
    /// <param name="unixSeconds">The time, in Unix seconds.</param>
    /// <returns>The offset.</returns>
    private static TimeSpan LocalOffset(long unixSeconds) => TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
}
