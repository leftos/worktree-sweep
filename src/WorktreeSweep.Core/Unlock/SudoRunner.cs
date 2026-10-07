namespace WorktreeSweep.Unlock;

/// <summary>The real <c>sudo</c>, <see cref="SudoConfig.Program"/>, run in this console.</summary>
public sealed class SudoRunner : ISudoRunner
{
    /// <inheritdoc/>
    public SudoMode? Mode() => SudoConfig.Read();

    /// <inheritdoc/>
    public int Run(IReadOnlyList<string> arguments) => ChildProcess.RunAttached(SudoConfig.Program, arguments);
}
