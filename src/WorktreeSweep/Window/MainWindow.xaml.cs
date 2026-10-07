using System.ComponentModel;
using System.Windows;
using WorktreeSweep.ViewModels;

namespace WorktreeSweep.Window;

/// <summary>The window: one panel per screen of the <see cref="MainViewModel"/>, shown by its <see cref="MainViewModel.Screen"/>.</summary>
public partial class MainWindow
{
    /// <summary>The background jobs, asked before the window closes.</summary>
    private readonly WindowWorkers workers;

    /// <summary>Initializes a new instance of the <see cref="MainWindow"/> class.</summary>
    /// <param name="main">The window's view model.</param>
    /// <param name="workers">The background jobs, which decide whether the window may close.</param>
    /// <param name="root">The full path of the folder scanned, for the title and the Scanning screen.</param>
    public MainWindow(MainViewModel main, WindowWorkers workers, string root)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(workers);
        ArgumentNullException.ThrowIfNull(root);
        this.workers = workers;
        InitializeComponent();
        DataContext = main;
        Title = $"worktree-sweep — {root}";
        ScanningText.Text = $"Scanning {root}…";
        Closing += OnClosing;
    }

    /// <summary>Keeps the window open when the workers refuse the close.</summary>
    /// <param name="sender">The window.</param>
    /// <param name="e">The close, to cancel.</param>
    private void OnClosing(object? sender, CancelEventArgs e) => e.Cancel = !workers.RequestClose();

    /// <summary>Closes the window from a Close button.</summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The click.</param>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
