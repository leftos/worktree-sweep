using System.Text;

namespace WorktreeSweep.Tests;

/// <summary>Which encoding a standard stream gets: UTF-8 when redirected, UTF-16 for a console, neither with a BOM.</summary>
public sealed class ConsoleEncodingsTests
{
    /// <summary>A redirected stream gets UTF-8 without a BOM.</summary>
    [Fact]
    public void RedirectedStreamGetsUtf8WithoutBom()
    {
        Encoding encoding = ConsoleEncodings.For(redirected: true);

        Assert.Equal(65001, encoding.CodePage);
        Assert.Empty(encoding.GetPreamble());
    }

    /// <summary>A console stream gets UTF-16 without a BOM.</summary>
    [Fact]
    public void ConsoleStreamGetsUtf16WithoutBom()
    {
        Encoding encoding = ConsoleEncodings.For(redirected: false);

        Assert.Equal(1200, encoding.CodePage);
        Assert.Empty(encoding.GetPreamble());
    }
}
