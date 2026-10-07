namespace WorktreeSweep.Unlock;

/// <summary>Something in the unlock flow failed: <c>sudo</c> cannot be run, or the elevated side failed.</summary>
public class UnlockException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="UnlockException"/> class.</summary>
    public UnlockException() { }

    /// <summary>Initializes a new instance of the <see cref="UnlockException"/> class with a message.</summary>
    /// <param name="message">What failed.</param>
    public UnlockException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance of the <see cref="UnlockException"/> class with a message and its cause.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="innerException">The error that stopped it.</param>
    public UnlockException(string message, Exception innerException)
        : base(message, innerException) { }
}
