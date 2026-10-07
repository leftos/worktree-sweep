using System.Diagnostics;
using System.Text;
using WorktreeSweep.Recycle;

namespace WorktreeSweep.Tests;

/// <summary>
/// Reading a volume's Recycle Bin settings: the parsing over values the tests give, and the read of real volumes. In the process
/// environment collection because the warning tests listen on the process-wide <see cref="Trace.Listeners"/>.
/// </summary>
[Collection(ProcessEnvironment.Name)]
public sealed class BinCapacityReaderTests
{
    private const string Key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{guid}";

    private const string Guid = "{0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0}";

    /// <summary>The GUID, braces included, is taken from a volume name.</summary>
    [Fact]
    public void GuidIsTakenFromTheVolumeName() => Assert.Equal(Guid, BinCapacityReader.GuidOf($@"\\?\Volume{Guid}\", @"D:\"));

    /// <summary>A volume name without a <c>{GUID}</c>, or with its braces the wrong way round, is an error naming the path.</summary>
    /// <param name="volumeName">The volume name.</param>
    [Theory]
    [InlineData(@"\\?\Volume\")]
    [InlineData(@"\\?\Volume}0b1c{\")]
    public void MalformedVolumeNameIsAnError(string volumeName)
    {
        IOException error = Assert.Throws<IOException>(() => BinCapacityReader.GuidOf(volumeName, @"D:\dev"));

        Assert.Contains(@"D:\dev", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A volume with no key has an unknown bin size.</summary>
    [Fact]
    public void MissingKeyIsUnknown() => Assert.Null(BinCapacityReader.FromValues(Key, readValue: null));

    /// <summary>A key missing either value has an unknown bin size.</summary>
    [Fact]
    public void MissingValueIsUnknown()
    {
        Assert.Null(BinCapacityReader.FromValues(Key, Values(("MaxCapacity", 14_844))));
        Assert.Null(BinCapacityReader.FromValues(Key, Values(("NukeOnDelete", 0))));
    }

    /// <summary>A value that is not a DWORD gives an unknown bin size and a warning naming the value.</summary>
    /// <param name="maxCapacity">A <c>MaxCapacity</c> read as a string or a QWORD.</param>
    [Theory]
    [InlineData("14844")]
    [InlineData(14_844L)]
    public void NonDwordValueIsUnknownWithAWarning(object maxCapacity)
    {
        using var trace = new TraceCapture();

        BinCapacity? capacity = BinCapacityReader.FromValues(Key, Values(("MaxCapacity", maxCapacity), ("NukeOnDelete", 0)));

        Assert.Null(capacity);
        Assert.Contains($@"{Key}\MaxCapacity is not a DWORD; treating the Recycle Bin size as unknown", trace.Text, StringComparison.Ordinal);
    }

    /// <summary>Any non-zero <c>NukeOnDelete</c> means files skip the bin.</summary>
    [Fact]
    public void NonZeroNukeOnDeleteIsNuke() =>
        Assert.Equal(new BinCapacity(100, NukeOnDelete: true), BinCapacityReader.FromValues(Key, Values(("MaxCapacity", 100), ("NukeOnDelete", 2))));

    /// <summary>Both DWORDs read as the bin's settings; a DWORD above <see cref="int.MaxValue"/> keeps its unsigned value.</summary>
    /// <param name="maxCapacity">The DWORD as the registry gives it, an <see langword="int"/>.</param>
    /// <param name="expectedMb">The bin's size in MB.</param>
    [Theory]
    [InlineData(14_844, 14_844u)]
    [InlineData(-1, uint.MaxValue)]
    public void DwordValuesAreTheSettings(int maxCapacity, uint expectedMb) =>
        Assert.Equal(
            new BinCapacity(expectedMb, NukeOnDelete: false),
            BinCapacityReader.FromValues(Key, Values(("MaxCapacity", maxCapacity), ("NukeOnDelete", 0)))
        );

    /// <summary>Reading the bin settings of the volume the tests run on succeeds: a value, or null when the bin has no settings.</summary>
    [Fact]
    public void BinCapacityReadsTheTestVolume()
    {
        using var fx = new Fixture();

        Exception? error = Record.Exception(() => BinCapacityReader.Read(fx.Root));

        Assert.Null(error);
    }

    /// <summary>A path on a drive letter that is not mounted is an <see cref="IOException"/> naming the path.</summary>
    [Fact]
    public void MissingDriveIsAnIOException()
    {
        var mounted = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        char letter = "ZYXWVUTSRQPONMLKJIHGFE".First(candidate => !mounted.Contains(candidate));
        string path = $@"{letter}:\nowhere";

        IOException error = Assert.Throws<IOException>(() => BinCapacityReader.Read(path));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
    }

    private static Func<string, object?> Values(params (string Name, object Value)[] values)
    {
        Dictionary<string, object> byName = values.ToDictionary(value => value.Name, value => value.Value);
        return name => byName.GetValueOrDefault(name);
    }

    /// <summary>Collects what is traced while it is alive.</summary>
    private sealed class TraceCapture : TraceListener
    {
        private readonly StringBuilder text = new();

        public TraceCapture() => Trace.Listeners.Add(this);

        public string Text => text.ToString();

        public override void Write(string? message) => text.Append(message);

        public override void WriteLine(string? message) => text.AppendLine(message);

        protected override void Dispose(bool disposing)
        {
            Trace.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
}
