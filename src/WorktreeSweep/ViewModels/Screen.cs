namespace WorktreeSweep.ViewModels;

/// <summary>The screen the window shows.</summary>
public enum Screen
{
    /// <summary>The scan is running.</summary>
    Scanning,

    /// <summary>The scan found no candidates.</summary>
    Empty,

    /// <summary>The scan failed; <see cref="MainViewModel.Message"/> says why.</summary>
    Failed,

    /// <summary>The candidates, to tick.</summary>
    List,

    /// <summary>The questions about the ticked picks and the final confirmation.</summary>
    Review,

    /// <summary>The removal is running.</summary>
    Removing,

    /// <summary>What the removal did.</summary>
    Results,
}
