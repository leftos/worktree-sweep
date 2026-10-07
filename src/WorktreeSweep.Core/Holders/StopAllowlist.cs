namespace WorktreeSweep.Holders;

/// <summary>Which holders an agent may stop without asking: build and language servers that restart on their own.</summary>
public static class StopAllowlist
{
    private static readonly string[] BuildServers = ["cargo.exe", "msbuild.exe", "vbcscompiler.exe"];

    /// <summary>
    /// The holders that hold something and whose program is a build server or language server that restarts on its own:
    /// <c>rust-analyzer*.exe</c>, <c>cargo.exe</c>, <c>MSBuild.exe</c>, <c>VBCSCompiler.exe</c>, and <c>dotnet.exe</c> running an
    /// MSBuild node or the compiler server. A process that only may hold the folder is never among them.
    /// </summary>
    /// <param name="report">What the holder finder found.</param>
    /// <returns>The stoppable holders, in report order.</returns>
    public static IReadOnlyList<Holder> Stoppable(HolderReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return [.. report.Holders.Where(holder => holder.Holds.Count > 0 && IsAllowlisted(holder))];
    }

    private static bool IsAllowlisted(Holder holder)
    {
        string exe = holder.Exe.ToLowerInvariant();
        if (exe.StartsWith("rust-analyzer", StringComparison.Ordinal) && exe.EndsWith(".exe", StringComparison.Ordinal))
        {
            return true;
        }
        if (BuildServers.Contains(exe))
        {
            return true;
        }
        if (exe != "dotnet.exe" || holder.CommandLine is null)
        {
            return false;
        }
        string commandLine = holder.CommandLine.ToLowerInvariant();
        bool msbuildNode =
            commandLine.Contains("msbuild.dll", StringComparison.Ordinal)
            && (commandLine.Contains("/nodemode", StringComparison.Ordinal) || commandLine.Contains("-nodemode", StringComparison.Ordinal));
        return msbuildNode || commandLine.Contains("vbcscompiler.dll", StringComparison.Ordinal);
    }
}
