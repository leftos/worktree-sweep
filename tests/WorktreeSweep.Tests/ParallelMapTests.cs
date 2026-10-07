using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>Mapping items on parallel workers.</summary>
public sealed class ParallelMapTests
{
    /// <summary>Results come back in input order even when later items finish first.</summary>
    [Fact]
    public void KeepsInputOrder()
    {
        List<int> items = [.. Enumerable.Range(0, 200)];

        IReadOnlyList<int> results = SignalReader.ParallelMap(
            items,
            item =>
            {
                Thread.Sleep((item * 7) % 5);
                return item * 2;
            }
        );

        Assert.Equal(items.Select(item => item * 2), results);
    }

    /// <summary>An exception the function throws reaches the caller as itself, not wrapped in an <see cref="AggregateException"/>.</summary>
    [Fact]
    public void ThrownExceptionReachesTheCallerUnwrapped()
    {
        List<int> items = [.. Enumerable.Range(0, 20)];

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            SignalReader.ParallelMap(items, item => item == 10 ? throw new InvalidOperationException("item 10") : item)
        );

        Assert.Equal("item 10", error.Message);
    }
}
