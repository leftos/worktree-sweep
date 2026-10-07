using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorktreeSweep.Holders;
using WorktreeSweep.Report;

namespace WorktreeSweep.Agent;

/// <summary>An agent removal's report in JSON, in the scan report's style.</summary>
/// <remarks>
/// Keys are snake_case and absent fields are written as <c>null</c>. A holder's <c>started</c> is an ISO 8601 UTC string in whole
/// seconds, <c>null</c> when its creation time is unknown; each of its <c>holds</c> is <c>{kind, path}</c> with kind
/// <c>current_folder</c> or <c>open_handle</c>. <c>released</c> is <c>{released_at, reason, holders}</c>, its time an ISO string too.
/// </remarks>
public static class RemoveReportJson
{
    /// <summary>The FILETIME count of the Unix epoch, in seconds.</summary>
    private const long EpochFiletimeSeconds = 11_644_473_600;

    private const ulong FiletimeTicksPerSecond = 10_000_000;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Writes the report as one indented JSON document with <c>\n</c> line ends and a trailing <c>\n</c>.</summary>
    /// <param name="report">The report.</param>
    /// <param name="output">The writer to write to.</param>
    public static void Write(RemoveReport report, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);
        output.Write(JsonSerializer.Serialize(Document.From(report), Options) + "\n");
    }

    /// <summary>A FILETIME count as an ISO 8601 UTC string in whole seconds; <see langword="null"/> for 0, an unknown time.</summary>
    private static string? IsoFiletime(ulong filetime) =>
        filetime == 0 ? null : ReportJson.IsoTime((long)(filetime / FiletimeTicksPerSecond) - EpochFiletimeSeconds);

    private sealed record Document
    {
        public required RemoveStatus Status { get; init; }

        public required Reason? Reason { get; init; }

        public required string Path { get; init; }

        public required string? Repo { get; init; }

        public required string? Branch { get; init; }

        public required bool BranchDeleted { get; init; }

        public required string? Loss { get; init; }

        public required string? CdTo { get; init; }

        public required IReadOnlyList<HolderDocument> Holders { get; init; }

        public required IReadOnlyList<MayHold> MayHold { get; init; }

        public required IReadOnlyList<ProcessRef> Stopped { get; init; }

        public required ReleasedDocument? Released { get; init; }

        public required IReadOnlyList<string> Notes { get; init; }

        public static Document From(RemoveReport report) =>
            new()
            {
                Status = report.Status,
                Reason = report.Reason,
                Path = report.Path,
                Repo = report.Repo,
                Branch = report.Branch,
                BranchDeleted = report.BranchDeleted,
                Loss = report.Loss,
                CdTo = report.CdTo,
                Holders = [.. report.Holders.Select(HolderDocument.From)],
                MayHold = report.MayHold,
                Stopped = report.Stopped,
                Released = report.Released is null ? null : ReleasedDocument.From(report.Released),
                Notes = report.Notes,
            };
    }

    private sealed record HolderDocument
    {
        public required int Pid { get; init; }

        public required string Exe { get; init; }

        public required string? Image { get; init; }

        public required string? Started { get; init; }

        public required string? CommandLine { get; init; }

        public required IReadOnlyList<HoldDocument> Holds { get; init; }

        public static HolderDocument From(Holder holder) =>
            new()
            {
                Pid = holder.Pid,
                Exe = holder.Exe,
                Image = holder.Image,
                Started = IsoFiletime(holder.Started),
                CommandLine = holder.CommandLine,
                Holds = [.. holder.Holds.Select(HoldDocument.From)],
            };
    }

    private sealed record HoldDocument
    {
        public required string Kind { get; init; }

        public required string Path { get; init; }

        public static HoldDocument From(Hold hold) =>
            new()
            {
                Kind = hold switch
                {
                    Hold.CurrentFolder => "current_folder",
                    Hold.OpenHandle => "open_handle",
                    _ => throw new UnreachableException($"unknown hold {hold}"),
                },
                Path = hold.Path,
            };
    }
}
