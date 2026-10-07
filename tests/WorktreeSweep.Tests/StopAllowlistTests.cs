using WorktreeSweep.Holders;

namespace WorktreeSweep.Tests;

/// <summary>Which holders an agent may stop: build and language servers that restart on their own, and nothing else.</summary>
public sealed class StopAllowlistTests
{
    /// <summary>Every <c>rust-analyzer*.exe</c> is stoppable; a non-exe with that prefix is not.</summary>
    [Fact]
    public void RustAnalyzerVariantsAreStoppable()
    {
        Assert.True(IsStoppable("rust-analyzer.exe", null));
        Assert.True(IsStoppable("rust-analyzer-x86_64-pc-windows-msvc.exe", null));
        Assert.False(IsStoppable("rust-analyzer-helper.txt", null));
    }

    /// <summary>The proc-macro server is a <c>rust-analyzer*.exe</c> too.</summary>
    [Fact]
    public void ProcMacroServerIsStoppable() => Assert.True(IsStoppable("rust-analyzer-proc-macro-srv.exe", null));

    /// <summary>Editors and shells are never stopped.</summary>
    /// <param name="exe">The image name.</param>
    [Theory]
    [InlineData("code.exe")]
    [InlineData("devenv.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("bash.exe")]
    [InlineData("node.exe")]
    public void EditorsAndShellsAreNotStoppable(string exe) => Assert.False(IsStoppable(exe, null));

    /// <summary>Build servers match in any letter case.</summary>
    /// <param name="exe">The image name.</param>
    [Theory]
    [InlineData("cargo.exe")]
    [InlineData("MSBuild.exe")]
    [InlineData("VBCSCompiler.exe")]
    [InlineData("CARGO.EXE")]
    [InlineData("msbuild.exe")]
    public void BuildServersAreStoppableInAnyCase(string exe) => Assert.True(IsStoppable(exe, null));

    /// <summary>Programs whose names only resemble cargo's are not stoppable.</summary>
    [Fact]
    public void CargoLookalikesAreNotStoppable()
    {
        Assert.False(IsStoppable("cargo-watch.exe", null));
        Assert.False(IsStoppable("rustc.exe", null));
    }

    /// <summary>A <c>dotnet.exe</c> running an MSBuild node, with either switch prefix, is stoppable.</summary>
    [Fact]
    public void DotnetMsbuildNodeIsStoppable()
    {
        const string Sdk = "\"C:\\Program Files\\dotnet\\sdk\\9.0.100\\MSBuild.dll\"";
        Assert.True(IsStoppable("dotnet.exe", $"dotnet.exe {Sdk} /nologo /nodemode:1 /nodeReuse:true"));
        Assert.True(IsStoppable("dotnet.exe", $"dotnet.exe {Sdk} -nodemode:8"));
    }

    /// <summary>A foreground <c>dotnet build</c> is the user's own work and is not stoppable.</summary>
    [Fact]
    public void DotnetForegroundBuildIsNotStoppable() =>
        Assert.False(IsStoppable("dotnet.exe", @"dotnet.exe C:\sdk\MSBuild.dll -restore D:\repo\app.csproj"));

    /// <summary>A <c>dotnet.exe</c> running the compiler server is stoppable.</summary>
    [Fact]
    public void DotnetCompilerServerIsStoppable() =>
        Assert.True(IsStoppable("dotnet.exe", @"dotnet.exe C:\sdk\Roslyn\bincore\VBCSCompiler.dll -pipename:abc"));

    /// <summary>A <c>dotnet.exe</c> whose command line could not be read is not stoppable.</summary>
    [Fact]
    public void DotnetWithoutCommandLineIsNotStoppable() => Assert.False(IsStoppable("dotnet.exe", null));

    /// <summary>A process that only may hold the folder is never stopped.</summary>
    [Fact]
    public void MayHoldOnlyIsNotStoppable()
    {
        var report = new HolderReport([], [new MayHold(42, "rust-analyzer.exe", MayHoldWhy.UnnamedHandle)]);
        Assert.Empty(StopAllowlist.Stoppable(report));
    }

    /// <summary>A holder that holds nothing is not stoppable.</summary>
    [Fact]
    public void HolderWithoutHoldsIsNotStoppable()
    {
        Holder idle = Holder("cargo.exe", null) with { Holds = [] };
        Assert.Empty(StopAllowlist.Stoppable(new HolderReport([idle], [])));
    }

    private static Holder Holder(string exe, string? commandLine) => new(42, exe, null, 1, commandLine, [new Hold.CurrentFolder(@"D:\repo.wt\x")]);

    private static bool IsStoppable(string exe, string? commandLine) =>
        StopAllowlist.Stoppable(new HolderReport([Holder(exe, commandLine)], [])).Count == 1;
}
