using System.Text.Json.Serialization;
using WorktreeSweep.Discovery;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Report;

/// <summary>The scan report in the shape <see cref="ReportJson"/> writes; properties are serialized in declaration order.</summary>
internal sealed record ReportDocument
{
    public required string Root { get; init; }

    public required IReadOnlyList<RepoReport> Repos { get; init; }

    public required IReadOnlyList<DiscoveryError> DiscoveryErrors { get; init; }

    public required IReadOnlyList<CandidateDocument> Candidates { get; init; }

    /// <summary>The document of a report.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The document.</returns>
    public static ReportDocument From(ScanReport report) =>
        new()
        {
            Root = report.Root,
            Repos = report.Repos,
            DiscoveryErrors = report.DiscoveryErrors,
            Candidates = [.. report.Candidates.Select(CandidateDocument.From)],
        };
}

/// <summary>A candidate, tagged by <c>kind</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RegisteredDocument), "registered")]
[JsonDerivedType(typeof(OrphanDocument), "orphan")]
internal abstract record CandidateDocument
{
    /// <summary>The document of a candidate.</summary>
    /// <param name="candidate">The candidate.</param>
    /// <returns>The document.</returns>
    public static CandidateDocument From(Candidate candidate) =>
        candidate switch
        {
            RegisteredCandidate registered => RegisteredDocument.From(registered),
            OrphanCandidate orphan => OrphanDocument.From(orphan),
            _ => throw new ArgumentException($"unknown candidate type {candidate.GetType().Name}", nameof(candidate)),
        };
}

/// <summary>A registered worktree with its record, released marker and signals as fields of one object.</summary>
internal sealed record RegisteredDocument : CandidateDocument
{
    public required string Path { get; init; }

    public required string Repo { get; init; }

    public string? Branch { get; init; }

    public string? Head { get; init; }

    public string? Prunable { get; init; }

    public string? GitLock { get; init; }

    public ReleasedDocument? Released { get; init; }

    public MergeState? MergeState { get; init; }

    public string? MergeStateAgainst { get; init; }

    public Dirty? Dirty { get; init; }

    public Upstream? Upstream { get; init; }

    public string? LastActivity { get; init; }

    public SizeDocument? Size { get; init; }

    public required IReadOnlyList<string> Errors { get; init; }

    public static RegisteredDocument From(RegisteredCandidate candidate)
    {
        WorktreeSignals signals = candidate.Signals;
        return new()
        {
            Path = candidate.Path,
            Repo = candidate.Repo,
            Branch = candidate.Record.Branch,
            Head = candidate.Record.Head,
            Prunable = candidate.Record.Prunable,
            GitLock = candidate.Record.Locked,
            Released = candidate.Released is null ? null : ReleasedDocument.From(candidate.Released),
            MergeState = signals.MergeState,
            MergeStateAgainst = signals.MergeStateAgainst,
            Dirty = signals.Dirty,
            Upstream = signals.Upstream,
            LastActivity = ReportJson.IsoTime(signals.LastActivityUnix),
            Size = signals.Size is null ? null : SizeDocument.From(signals.Size),
            Errors = signals.Errors,
        };
    }
}

/// <summary>An orphan folder or link and its size.</summary>
internal sealed record OrphanDocument : CandidateDocument
{
    public required string Path { get; init; }

    public required string Container { get; init; }

    public required OrphanKind OrphanKind { get; init; }

    public string? LinkTarget { get; init; }

    public required bool StaleGitdir { get; init; }

    public string? LiveGitdir { get; init; }

    public required bool HasGitDir { get; init; }

    public required SizeDocument Size { get; init; }

    public static OrphanDocument From(OrphanCandidate candidate)
    {
        Orphan orphan = candidate.Orphan;
        return new()
        {
            Path = orphan.Path,
            Container = orphan.Container,
            OrphanKind = orphan.Kind,
            LinkTarget = orphan.LinkTarget,
            StaleGitdir = orphan.StaleGitdir,
            LiveGitdir = orphan.LiveGitdir,
            HasGitDir = orphan.HasGitDir,
            Size = SizeDocument.From(candidate.Size),
        };
    }
}

/// <summary>A size with its newest write as an ISO 8601 UTC string.</summary>
internal sealed record SizeDocument
{
    public required long Bytes { get; init; }

    public required long Files { get; init; }

    public required int Unreadable { get; init; }

    public string? LastWrite { get; init; }

    public static SizeDocument From(SizeInfo size) =>
        new()
        {
            Bytes = size.Bytes,
            Files = size.Files,
            Unreadable = size.Unreadable,
            LastWrite = ReportJson.IsoTime(size.LastWriteUnix),
        };
}

/// <summary>A released marker with its time as an ISO 8601 UTC string.</summary>
internal sealed record ReleasedDocument
{
    public string? ReleasedAt { get; init; }

    public required Reason Reason { get; init; }

    public required IReadOnlyList<ProcessRef> Holders { get; init; }

    public static ReleasedDocument From(Released released) =>
        new()
        {
            ReleasedAt = ReportJson.IsoTime(released.ReleasedAtUnix),
            Reason = released.Reason,
            Holders = released.Holders,
        };
}
