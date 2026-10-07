using System.Diagnostics;

namespace WorktreeSweep.Tests;

/// <summary>
/// A PowerShell child started in a given working directory, returned once it has written a ready file, and killed on dispose: a
/// live process whose current folder the tests control, and which they may inspect, since they started it.
/// </summary>
internal sealed class ReadyChild : IDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly Process process;

    private ReadyChild(Process process) => this.process = process;

    /// <summary>Gets the child's PID.</summary>
    public int Id => process.Id;

    /// <summary>Starts <c>pwsh</c> in <paramref name="workingDirectory"/> and waits until it has written <paramref name="readyFile"/>.</summary>
    /// <param name="workingDirectory">The child's current folder.</param>
    /// <param name="readyFile">A file that does not exist yet; the child writes it once it runs.</param>
    /// <returns>The running child.</returns>
    /// <exception cref="TimeoutException">The ready file did not appear within 30 s; the child has been killed.</exception>
    public static ReadyChild Start(string workingDirectory, string readyFile) => Start("pwsh", workingDirectory, readyFile);

    /// <summary>Starts a PowerShell <paramref name="program"/> in <paramref name="workingDirectory"/> and waits for its ready file.</summary>
    /// <param name="program">A <c>pwsh</c> or Windows PowerShell executable.</param>
    /// <param name="workingDirectory">The child's current folder.</param>
    /// <param name="readyFile">A file that does not exist yet; the child writes it once it runs.</param>
    /// <returns>The running child.</returns>
    /// <exception cref="TimeoutException">The ready file did not appear within 30 s; the child has been killed.</exception>
    public static ReadyChild Start(string program, string workingDirectory, string readyFile)
    {
        string script = $"Set-Content -LiteralPath '{readyFile.Replace("'", "''", StringComparison.Ordinal)}' ready; Start-Sleep 120";
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
        };
        var child = new ReadyChild(Process.Start(info) ?? throw new InvalidOperationException($"cannot start {program}"));
        try
        {
            child.WaitFor(readyFile);
            return child;
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }

    /// <summary>Kills the child when it is still running and waits for it to exit.</summary>
    public void Stop()
    {
        if (!process.HasExited)
        {
            process.Kill();
            process.WaitForExit();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        process.Dispose();
    }

    private void WaitFor(string readyFile)
    {
        var clock = Stopwatch.StartNew();
        while (!File.Exists(readyFile))
        {
            if (clock.Elapsed > ReadyTimeout)
            {
                throw new TimeoutException($"process {process.Id} did not write {readyFile} within {ReadyTimeout.TotalSeconds} s");
            }
            Thread.Sleep(PollInterval);
        }
    }
}
