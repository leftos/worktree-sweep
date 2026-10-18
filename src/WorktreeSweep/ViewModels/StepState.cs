namespace WorktreeSweep.ViewModels;

/// <summary>Where one of the scan's three steps stands.</summary>
public enum StepState
{
    /// <summary>Not started.</summary>
    Waiting,

    /// <summary>Running now.</summary>
    Running,

    /// <summary>Finished.</summary>
    Done,
}
