namespace WorktreeSweep.Git;

/// <summary>A git command that could not be started, that exited with a code its caller does not accept, or that did not exit in
/// time (<see cref="GitTimeoutException"/>).</summary>
public class GitException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="GitException"/> class.</summary>
    public GitException() { }

    /// <summary>Initializes a new instance of the <see cref="GitException"/> class with a message.</summary>
    /// <param name="message">What failed: the command, its directory and git's standard error.</param>
    public GitException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance of the <see cref="GitException"/> class with a message and its cause.</summary>
    /// <param name="message">What failed: the command and its directory.</param>
    /// <param name="innerException">The error that stopped git from starting.</param>
    public GitException(string message, Exception innerException)
        : base(message, innerException) { }
}
