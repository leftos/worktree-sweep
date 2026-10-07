using System.ComponentModel;
using WorktreeSweep.Processes;

namespace WorktreeSweep.Unlock;

// "Stop" is a reserved word in VB (the rule's own reason to rename); every caller of this seam is C#, and the name mirrors
// ProcessStopper.Stop, the operation it wraps.
#pragma warning disable CA1716
/// <summary>The process operations the unlock session makes: a snapshot, and stopping a process.</summary>
public interface IProcessControl
{
    /// <summary>Every running process's parent and image name, by PID.</summary>
    /// <returns>The table; a PID seen twice keeps its last entry.</returns>
    /// <exception cref="Win32Exception">The process snapshot cannot be taken.</exception>
    IReadOnlyDictionary<int, ProcessEntry> Snapshot();

    /// <summary>Stops a process by PID, after checking it is still the program the scan saw, and waits for it to exit.</summary>
    /// <param name="pid">The process to stop.</param>
    /// <param name="exe">The image file name the scan saw, compared case-insensitively.</param>
    /// <exception cref="InvalidOperationException">The PID now belongs to another image; nothing was stopped.</exception>
    /// <exception cref="Win32Exception">The process cannot be opened, terminated, or has not exited within the wait.</exception>
    void Stop(int pid, string exe);
}
#pragma warning restore CA1716
