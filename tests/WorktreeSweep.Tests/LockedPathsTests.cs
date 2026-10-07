using WorktreeSweep.Processes;

namespace WorktreeSweep.Tests;

/// <summary>Which handle names fall under a locked path.</summary>
public sealed class LockedPathsTests
{
    /// <summary>A path matches itself and what lies under it, whatever the case, separator or trailing slash.</summary>
    [Fact]
    public void RowFilterMatchesPathAndChildrenOnly()
    {
        string[] paths = [@"D:\a.wt\x", "D:/other/"];

        Assert.True(LockedPaths.Matches(@"D:\a.wt\x", paths));
        Assert.True(LockedPaths.Matches(@"D:\a.wt\x\", paths));
        Assert.True(LockedPaths.Matches(@"D:\a.wt\x\held.txt", paths));
        Assert.True(LockedPaths.Matches(@"d:\A.WT\X\Sub\file.rs", paths));
        Assert.True(LockedPaths.Matches(@"\\?\D:\a.wt\x\f", paths));
        Assert.True(LockedPaths.Matches(@"D:\other\f", paths));
        Assert.False(LockedPaths.Matches(@"D:\a.wt\xy", paths));
        Assert.False(LockedPaths.Matches(@"D:\a.wt\xy\f", paths));
        Assert.False(LockedPaths.Matches(@"D:\a.wt", paths));
        Assert.False(LockedPaths.Matches(@"E:\a.wt\x", paths));
        Assert.False(LockedPaths.Matches(@"D:\otherwise", paths));
    }

    /// <summary>The NT object prefix <c>\??\</c> is stripped from the name and from the path.</summary>
    [Fact]
    public void NtPrefixIsStripped()
    {
        Assert.True(LockedPaths.Matches(@"\??\D:\a\b", [@"D:\a"]));
        Assert.True(LockedPaths.Matches(@"D:\a\b", [@"\??\D:\a"]));
    }

    /// <summary>A verbatim UNC name reads as the plain UNC path, matched by whole share components.</summary>
    [Fact]
    public void VerbatimUncPrefixReadsAsUnc()
    {
        Assert.True(LockedPaths.Matches(@"\\?\UNC\srv\share\x", [@"\\srv\share"]));
        Assert.False(LockedPaths.Matches(@"\\?\UNC\srv\share\x", [@"\\srv\sh"]));
    }

    /// <summary>A locked path that is empty once its prefix and trailing separators are gone matches nothing.</summary>
    [Fact]
    public void EmptyLockedPathMatchesNothing()
    {
        Assert.False(LockedPaths.Matches(@"\\srv\share\f", [@"\\?\"]));
        Assert.False(LockedPaths.Matches(@"\Device\HarddiskVolume3\x", [""]));
        Assert.False(LockedPaths.Matches(@"\Device\HarddiskVolume3\x", [@"\"]));
        Assert.False(LockedPaths.Matches(@"\\srv\share\f", [@"\??\"]));
    }

    /// <summary>A sibling whose name starts with the locked path's name does not match.</summary>
    [Fact]
    public void SiblingWithLongerNameDoesNotMatch()
    {
        Assert.False(LockedPaths.Matches(@"D:\ab", [@"D:\a"]));
        Assert.False(LockedPaths.Matches(@"\??\D:\ab", [@"D:\a"]));
    }
}
