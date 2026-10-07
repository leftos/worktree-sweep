namespace WorktreeSweep.Git;

/// <summary>The result of a git command whose non-zero exit code is an answer rather than a failure.</summary>
/// <param name="Code">The exit code.</param>
/// <param name="Stdout">Standard output, decoded as UTF-8 with replacement characters and not trimmed.</param>
/// <param name="Stderr">Standard error, decoded as UTF-8 with replacement characters and trimmed.</param>
public sealed record GitStatus(int Code, string Stdout, string Stderr)
{
    /// <summary>Gets a value indicating whether git exited with code 0.</summary>
    public bool Success => Code == 0;
}
