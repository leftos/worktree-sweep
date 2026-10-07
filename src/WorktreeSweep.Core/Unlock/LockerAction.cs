namespace WorktreeSweep.Unlock;

/// <summary>What to do about one process holding files under the locked folders.</summary>
public enum LockerAction
{
    /// <summary>Terminate the process.</summary>
    Stop,

    /// <summary>Close the process's handles under the locked folders.</summary>
    CloseHandles,

    /// <summary>Leave the process alone.</summary>
    Skip,

    /// <summary>Stop offering; end the session.</summary>
    Done,
}

/// <summary>The action offered first for a process.</summary>
public static class LockerActions
{
    /// <summary>Skip for the caller, whose stopping would close the user's shell, and Stop for any other process.</summary>
    /// <param name="isCaller">Whether the process is the one that started the unelevated run.</param>
    /// <returns>The action.</returns>
    public static LockerAction DefaultAction(bool isCaller) => isCaller ? LockerAction.Skip : LockerAction.Stop;
}
