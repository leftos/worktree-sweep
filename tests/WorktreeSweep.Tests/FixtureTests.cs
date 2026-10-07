namespace WorktreeSweep.Tests;

/// <summary>How the fixture compares paths.</summary>
public sealed class FixtureTests
{
    /// <summary>An 8.3 short form of the fixture root, as a short <c>%TEMP%</c> gives, names the same folder as its long form.</summary>
    [Fact]
    public void SamePathMatchesTheShortFormOfTheRoot()
    {
        using var fx = new Fixture();
        string shortForm = NativeMethods.ShortPath(fx.Root);
        Assert.SkipWhen(
            string.Equals(shortForm, fx.Root, StringComparison.OrdinalIgnoreCase),
            $"8.3 short names are disabled on the volume of {fx.Root}"
        );

        Assert.True(Fixture.SamePath(shortForm, fx.Root), $"short {shortForm}, long {fx.Root}");
        Assert.True(Fixture.SamePath(shortForm.Replace('\\', '/'), fx.Root), $"short {shortForm}, long {fx.Root}");
    }
}
