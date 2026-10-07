namespace WorktreeSweep.Tests;

/// <summary>
/// The collection of tests that set variables in this test process's environment, as a calling git hook would. It runs with no
/// other test in parallel, so no other test's git process inherits them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironment
{
    /// <summary>The collection's name.</summary>
    public const string Name = "process environment";
}

/// <summary>Sets variables in this process's environment and restores their previous values on dispose.</summary>
public sealed class InheritedEnv : IDisposable
{
    private readonly Dictionary<string, string?> previous = [];

    /// <summary>Initializes a new instance of the <see cref="InheritedEnv"/> class, setting every variable given.</summary>
    /// <param name="vars">The variables to set.</param>
    public InheritedEnv(IReadOnlyDictionary<string, string> vars)
    {
        ArgumentNullException.ThrowIfNull(vars);
        foreach (KeyValuePair<string, string> pair in vars)
        {
            previous[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (KeyValuePair<string, string?> pair in previous)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
