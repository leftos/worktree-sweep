namespace WorktreeSweep.Signals;

/// <summary>A repo's default branches, as short names (<c>main</c>, <c>origin/main</c>).</summary>
public sealed record DefaultBranches
{
    /// <summary>Gets the local default branch.</summary>
    public string? Local { get; init; }

    /// <summary>Gets the remote-tracking default branch <c>refs/remotes/origin/HEAD</c> points to.</summary>
    public string? Origin { get; init; }

    /// <summary>Each default that exists, local first, as its short name and its full ref name.</summary>
    /// <returns>The defaults.</returns>
    internal IReadOnlyList<(string Name, string Ref)> Refs()
    {
        var refs = new List<(string Name, string Ref)>();
        if (Local is not null)
        {
            refs.Add((Local, $"refs/heads/{Local}"));
        }
        if (Origin is not null)
        {
            refs.Add((Origin, $"refs/remotes/{Origin}"));
        }
        return refs;
    }
}
