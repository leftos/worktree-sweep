using WorktreeSweep.Discovery;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Tests;

/// <summary>Report candidates built by hand for the table and JSON tests; no git runs.</summary>
internal static class ReportSamples
{
    /// <summary>The time ages are measured from, in Unix seconds.</summary>
    public const long Now = 1_790_000_000;

    /// <summary>One day in seconds.</summary>
    public const long Day = 86_400;

    /// <summary>The scanned root of every sample report.</summary>
    public const string Root = @"D:\";

    /// <summary>A path under <see cref="Root"/>.</summary>
    /// <param name="relative">The path relative to the root.</param>
    /// <returns>The full path.</returns>
    public static string Under(string relative) => Path.Join(Root, relative);

    /// <summary>A worktree of <c>D:\yaat</c>, not released, not locked.</summary>
    /// <param name="path">The worktree's path relative to the root.</param>
    /// <param name="branch">The branch checked out; <see langword="null"/> when detached.</param>
    /// <param name="signals">Its signals.</param>
    /// <returns>The candidate.</returns>
    public static RegisteredCandidate Registered(string path, string? branch, WorktreeSignals signals) =>
        new()
        {
            Record = new WorktreeRecord
            {
                Path = Under(path),
                Branch = branch,
                Head = "0123456789abcdef",
            },
            Repo = Under("yaat"),
            Signals = signals,
        };

    /// <summary>A worktree on <c>feat</c> carrying a released marker with reason <c>locked</c> and no holders.</summary>
    /// <param name="repo">The repo's path relative to the root.</param>
    /// <param name="path">The worktree's path relative to the root.</param>
    /// <returns>The candidate.</returns>
    public static RegisteredCandidate Released(string repo, string path) =>
        Registered(path, "feat", new WorktreeSignals()) with
        {
            Repo = Under(repo),
            Released = new Released
            {
                ReleasedAtUnix = Now,
                Reason = Reason.Locked,
                Holders = [],
            },
        };

    /// <summary>A size of one file.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="lastWriteUnix">The newest modification time, in Unix seconds.</param>
    /// <returns>The size.</returns>
    public static SizeInfo Size(long bytes, long lastWriteUnix) =>
        new()
        {
            Bytes = bytes,
            Files = 1,
            Unreadable = 0,
            LastWriteUnix = lastWriteUnix,
        };

    /// <summary>An orphan in <c>D:\yaat.wt</c>.</summary>
    /// <param name="path">The orphan's path relative to the root.</param>
    /// <param name="kind">Folder or link.</param>
    /// <param name="size">Its size.</param>
    /// <returns>The candidate.</returns>
    public static OrphanCandidate Orphan(string path, OrphanKind kind, SizeInfo size) =>
        new()
        {
            Orphan = new Orphan
            {
                Path = Under(path),
                Container = Under("yaat.wt"),
                Kind = kind,
            },
            Size = size,
        };

    /// <summary>A report of <see cref="Root"/> with no repos.</summary>
    /// <param name="candidates">Its candidates, in stored order.</param>
    /// <returns>The report.</returns>
    public static ScanReport ReportOf(params Candidate[] candidates) =>
        new()
        {
            Root = Root,
            Repos = [],
            DiscoveryErrors = [],
            Candidates = candidates,
        };
}
