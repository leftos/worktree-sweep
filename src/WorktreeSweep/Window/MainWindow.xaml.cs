using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
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
        SourceInitialized += (_, _) => workers.OwnerWindow = new WindowInteropHelper(this).Handle;
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

    /// <summary>Keeps Enter in the List's search box from pressing the default Review button.</summary>
    /// <param name="sender">The search box.</param>
    /// <param name="e">The key pressed.</param>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
        }
    }

    /// <summary>Hands a column header click to the List's sort instead of letting the grid sort its rows itself.</summary>
    /// <param name="sender">The List's grid.</param>
    /// <param name="e">The clicked column, whose sort member path names its <see cref="SortKey"/>.</param>
    private void OnListSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (sender is DataGrid { DataContext: ListViewModel list } grid)
        {
            list.SortByCommand.Execute(Enum.Parse<SortKey>(e.Column.SortMemberPath));
            ShowSortArrows(grid, list);
        }
    }

    /// <summary>Puts the sort arrow back after the grid's rows are replaced, since a new item source clears every column's arrow.</summary>
    /// <param name="sender">The List's grid.</param>
    /// <param name="e">The binding that updated.</param>
    private void OnListShownChanged(object sender, DataTransferEventArgs e)
    {
        if (e.Property == ItemsControl.ItemsSourceProperty && e.TargetObject is DataGrid { DataContext: ListViewModel list } grid)
        {
            ShowSortArrows(grid, list);
        }
    }

    /// <summary>Shows the List's sort direction on its sorted column and no arrow on the others.</summary>
    /// <param name="grid">The List's grid.</param>
    /// <param name="list">The List.</param>
    private static void ShowSortArrows(DataGrid grid, ListViewModel list)
    {
        string? sorted = list.SortKey?.ToString();
        foreach (DataGridColumn column in grid.Columns)
        {
            column.SortDirection = column.SortMemberPath == sorted ? list.SortDirection : null;
        }
    }
}
