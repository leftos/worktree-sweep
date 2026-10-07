namespace WorktreeSweep.Git;

/// <summary>Git did not exit within its time limit and was killed.</summary>
public sealed class GitTimeoutException : GitException
{
    /// <summary>Initializes a new instance of the <see cref="GitTimeoutException"/> class.</summary>
    public GitTimeoutException() { }

    /// <summary>Initializes a new instance of the <see cref="GitTimeoutException"/> class with a message.</summary>
    /// <param name="message">What timed out: the command and its time limit.</param>
    public GitTimeoutException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance of the <see cref="GitTimeoutException"/> class with a message and its cause.</summary>
    /// <param name="message">What timed out: the command and its time limit.</param>
    /// <param name="innerException">The error that stopped git's process tree from being killed.</param>
    public GitTimeoutException(string message, Exception innerException)
        : base(message, innerException) { }
}
