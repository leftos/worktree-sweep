using WorktreeSweep.Holders;

namespace WorktreeSweep.Tests;

/// <summary>The parent chain by creation time, and the verbatim prefix a handle name loses.</summary>
public sealed class AncestorsTests
{
    /// <summary>Each parent created before its child is followed.</summary>
    [Fact]
    public void AncestorsFollowAThreeDeepChain()
    {
        IReadOnlyDictionary<int, TimedProcess> processes = Table((30, 20, 300), (20, 10, 200), (10, 1, 100));
        Assert.Equal([30, 20, 10], HolderFinder.Ancestors(30, processes));
    }

    /// <summary>A parent created after its child, or one whose creation time is unknown, is a reused PID and ends the chain.</summary>
    [Fact]
    public void AncestorsStopAtAParentStartedLater()
    {
        IReadOnlyDictionary<int, TimedProcess> processes = Table((30, 20, 300), (20, 10, 200), (10, 5, 250));
        Assert.Equal([30, 20], HolderFinder.Ancestors(30, processes));
        IReadOnlyDictionary<int, TimedProcess> unknown = Table((30, 20, 300), (20, 10, null));
        Assert.Equal([30], HolderFinder.Ancestors(30, unknown));
    }

    /// <summary>A cycle, including a process that is its own parent, ends the chain.</summary>
    [Fact]
    public void AncestorsStopAtACycle()
    {
        IReadOnlyDictionary<int, TimedProcess> processes = Table((30, 20, 300), (20, 30, 200));
        Assert.Equal([30, 20], HolderFinder.Ancestors(30, processes));
        IReadOnlyDictionary<int, TimedProcess> ownParent = Table((7, 7, 1));
        Assert.Equal([7], HolderFinder.Ancestors(7, ownParent));
    }

    /// <summary>A parent missing from the table ends the chain; a PID missing from it is a chain of one.</summary>
    [Fact]
    public void AncestorsStopAtAMissingParent()
    {
        IReadOnlyDictionary<int, TimedProcess> processes = Table((30, 20, 300), (20, 10, 200));
        Assert.Equal([30, 20], HolderFinder.Ancestors(30, processes));
        Assert.Equal([99], HolderFinder.Ancestors(99, processes));
    }

    /// <summary>A verbatim drive path loses its <c>\\?\</c> prefix; a verbatim UNC path keeps it.</summary>
    [Fact]
    public void VerbatimDrivePrefixIsDropped()
    {
        Assert.Equal(@"D:\a\b", HolderFinder.WithoutVerbatimPrefix(@"\\?\D:\a\b"));
        Assert.Equal(@"\\?\UNC\srv\s\x", HolderFinder.WithoutVerbatimPrefix(@"\\?\UNC\srv\s\x"));
    }

    private static Dictionary<int, TimedProcess> Table(params (int Pid, int Parent, int? Started)[] entries) =>
        entries.ToDictionary(entry => entry.Pid, entry => new TimedProcess(entry.Parent, $"p{entry.Pid}.exe", (ulong?)entry.Started));
}
