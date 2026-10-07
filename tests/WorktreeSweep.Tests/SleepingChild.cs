using System.Diagnostics;

namespace WorktreeSweep.Tests;

/// <summary>A <c>pwsh</c> child that sleeps for a minute: a live process the tests may stop, since they started it.</summary>
internal static class SleepingChild
{
    /// <summary>Starts the child.</summary>
    /// <returns>The running child.</returns>
    /// <exception cref="InvalidOperationException"><c>pwsh</c> did not start.</exception>
    public static Process Start()
    {
        var info = new ProcessStartInfo("pwsh") { UseShellExecute = false, ArgumentList = { "-NoProfile", "-Command", "Start-Sleep 60" } };
        return Process.Start(info) ?? throw new InvalidOperationException("cannot start pwsh");
    }

    /// <summary>Kills the child when it is still running and waits for it to exit.</summary>
    /// <param name="child">A child from <see cref="Start"/>.</param>
    public static void KillIfAlive(Process child)
    {
        if (!child.HasExited)
        {
            child.Kill();
            child.WaitForExit();
        }
    }
}
