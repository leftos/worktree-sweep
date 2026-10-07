namespace WorktreeSweep.ViewModels;

/// <summary>Where the Review screen is.</summary>
public enum ReviewState
{
    /// <summary>Waiting for the Recycle Bin capacities of <see cref="ReviewViewModel.CapacityPaths"/>.</summary>
    Preparing,

    /// <summary>Asking one question about one pick.</summary>
    Asking,

    /// <summary>Showing the final sentence, waiting for Confirm or Cancel.</summary>
    Confirming,
}
