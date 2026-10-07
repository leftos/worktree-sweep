using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>Measuring a folder's disk usage.</summary>
public sealed class SizeTests
{
    /// <summary>Every file in the tree is counted with its bytes, empty folders add nothing, and a last write time is read.</summary>
    [Fact]
    public void SizeWalkCountsFilesAndBytes()
    {
        using var fx = new Fixture();
        string dir = fx.PathTo("measured");
        _ = Directory.CreateDirectory(Path.Join(dir, "sub", "empty"));
        File.WriteAllText(Path.Join(dir, "a.txt"), "abc");
        File.WriteAllText(Path.Join(dir, "sub", "b.txt"), "defgh");

        SizeInfo size = SignalReader.WalkSize(dir);

        Assert.True(size is { Files: 2, Bytes: 8, Unreadable: 0 }, size.ToString());
        Assert.NotNull(size.LastWriteUnix);
    }
}
