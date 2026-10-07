namespace WorktreeSweep.Agent;

/// <summary>The process exit code that carries an agent removal's status.</summary>
public static class RemoveExitCode
{
    /// <summary>The exit code for <paramref name="status"/>: 0 removed, 5 released, 6 refused.</summary>
    /// <param name="status">What the removal did.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not a defined status.</exception>
    public static int For(RemoveStatus status) =>
        status switch
        {
            RemoveStatus.Removed => 0,
            RemoveStatus.Released => 5,
            RemoveStatus.Refused => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "not a removal status"),
        };
}
