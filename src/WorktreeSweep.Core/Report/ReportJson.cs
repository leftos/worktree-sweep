using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorktreeSweep.Signals;

namespace WorktreeSweep.Report;

/// <summary>The scan report's JSON form, as <c>--json</c> prints it.</summary>
/// <remarks>
/// <para>Keys are snake_case and null fields are written as <c>null</c>. <c>candidates</c> is one array whose objects start with
/// <c>"kind": "registered"</c> or <c>"kind": "orphan"</c>; a registered candidate carries its signals as its own fields and always an
/// <c>errors</c> array; an orphan's own kind is <c>orphan_kind</c>.</para>
/// <para>Timestamps are ISO 8601 UTC strings in whole seconds (<c>2026-09-21T14:13:20Z</c>): <c>last_activity</c>,
/// <c>size.last_write</c>, <c>released.released_at</c>. <c>merge_state</c> and <c>upstream</c> are objects tagged by <c>state</c>.</para>
/// </remarks>
public static class ReportJson
{
    private const string IsoFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'";

    private static readonly long MinUnix = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaxUnix = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower), new MergeStateConverter(), new UpstreamConverter() },
    };

    /// <summary>Writes the report as one indented JSON document in UTF-8 without a BOM, with <c>\n</c> line ends and a trailing <c>\n</c>.</summary>
    /// <param name="report">The report; its candidates are written in stored order.</param>
    /// <param name="output">The stream to write to.</param>
    public static void Write(ScanReport report, Stream output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);
        JsonSerializer.Serialize(output, ReportDocument.From(report), Options);
        output.WriteByte((byte)'\n');
    }

    /// <summary>A Unix time as an ISO 8601 UTC string in whole seconds.</summary>
    /// <param name="unix">The time in Unix seconds; <see langword="null"/> when unknown.</param>
    /// <returns>
    /// The string; <see langword="null"/> when <paramref name="unix"/> is, or lies outside the years 1 to 9999 a timestamp can hold.
    /// </returns>
    internal static string? IsoTime(long? unix) =>
        unix is long seconds && seconds >= MinUnix && seconds <= MaxUnix
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString(IsoFormat, CultureInfo.InvariantCulture)
            : null;

    private static string StateName(Enum kind) => JsonNamingPolicy.SnakeCaseLower.ConvertName(kind.ToString());

    /// <summary>
    /// Writes a <see cref="MergeState"/> as <c>{"state": ...}</c>, with <c>commits</c> when unmerged and <c>contained</c> when detached.
    /// </summary>
    private sealed class MergeStateConverter : JsonConverter<MergeState>
    {
        public override MergeState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("the scan report is written, never read");

        public override void Write(Utf8JsonWriter writer, MergeState value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("state", StateName(value.Kind));
            if (value.Kind == MergeStateKind.Unmerged)
            {
                writer.WriteNumber("commits", value.Commits);
            }
            else if (value.Kind == MergeStateKind.Detached)
            {
                writer.WriteBoolean("contained", value.Contained);
            }
            writer.WriteEndObject();
        }
    }

    /// <summary>Writes an <see cref="Upstream"/> as <c>{"state": ...}</c>, with <c>ahead</c> when tracking.</summary>
    private sealed class UpstreamConverter : JsonConverter<Upstream>
    {
        public override Upstream Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("the scan report is written, never read");

        public override void Write(Utf8JsonWriter writer, Upstream value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("state", StateName(value.Kind));
            if (value.Kind == UpstreamKind.Tracking)
            {
                writer.WriteNumber("ahead", value.Ahead);
            }
            writer.WriteEndObject();
        }
    }
}
