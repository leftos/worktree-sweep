using CommunityToolkit.Mvvm.ComponentModel;

namespace WorktreeSweep.ViewModels;

/// <summary>One runnable pick on the Removing screen: its path and how far its removal is.</summary>
public sealed partial class RemovalRowViewModel : ObservableObject
{
    /// <summary>The status before the pick's removal starts.</summary>
    public const string Pending = "pending";

    /// <summary>The status while the pick is being removed.</summary>
    public const string Removing = "removing…";

    /// <summary>The status once the pick is finished, removed or failed.</summary>
    public const string Done = "done";

    /// <summary>Initializes a new instance of the <see cref="RemovalRowViewModel"/> class, pending.</summary>
    /// <param name="index">The pick's index among the decisions.</param>
    /// <param name="path">The pick's path, relative to the scanned root.</param>
    public RemovalRowViewModel(int index, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Index = index;
        Path = path;
        Status = Pending;
    }

    /// <summary>Gets the pick's index among the decisions, as <see cref="Removal.Progress"/> names it.</summary>
    public int Index { get; }

    /// <summary>Gets the pick's path, relative to the scanned root.</summary>
    public string Path { get; }

    /// <summary>Gets the status: <see cref="Pending"/>, <see cref="Removing"/> or <see cref="Done"/>.</summary>
    [ObservableProperty]
    public partial string Status { get; internal set; }
}
