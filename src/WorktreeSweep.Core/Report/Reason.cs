using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorktreeSweep.Report;

/// <summary>
/// Why a worktree was released or refused. In JSON it is one snake_case string: the reason's own name, or for
/// <see cref="ReasonKind.NotRemovable"/> the name of its <see cref="RefusalReason"/>.
/// </summary>
[JsonConverter(typeof(ReasonJsonConverter))]
public sealed record Reason
{
    private Reason() { }

    /// <summary>Gets the reason for a worktree a process holds (or one that cannot be seen does).</summary>
    public static Reason Locked { get; } = new() { Kind = ReasonKind.Locked };

    /// <summary>Gets the reason for a worktree only processes that could not be fully inspected may hold.</summary>
    public static Reason MayHold { get; } = new() { Kind = ReasonKind.MayHold };

    /// <summary>Gets the reason for a worktree that cannot go to the Recycle Bin.</summary>
    public static Reason TooBigForRecycleBin { get; } = new() { Kind = ReasonKind.TooBigForRecycleBin };

    /// <summary>Gets the reason for a worktree the Shell did not finish recycling in time.</summary>
    public static Reason ShellTimeout { get; } = new() { Kind = ReasonKind.ShellTimeout };

    /// <summary>Gets the reason for a worktree whose removal would lose work.</summary>
    public static Reason WouldLose { get; } = new() { Kind = ReasonKind.WouldLose };

    /// <summary>Gets the reason for a worktree a process in the caller's own parent chain holds.</summary>
    public static Reason CallerHolds { get; } = new() { Kind = ReasonKind.CallerHolds };

    /// <summary>Gets which reason this is.</summary>
    public ReasonKind Kind { get; private init; }

    /// <summary>
    /// Gets, for <see cref="ReasonKind.NotRemovable"/>, why the path is not a removable worktree; <see langword="null"/> otherwise.
    /// </summary>
    public RefusalReason? Refusal { get; private init; }

    /// <summary>The path is not a removable worktree.</summary>
    /// <param name="refusal">Why it is not.</param>
    /// <returns>The reason.</returns>
    public static Reason NotRemovable(RefusalReason refusal) => new() { Kind = ReasonKind.NotRemovable, Refusal = refusal };
}

/// <summary>The kinds of <see cref="Reason"/>.</summary>
public enum ReasonKind
{
    /// <summary>A process holds it (or one that cannot be seen does).</summary>
    Locked,

    /// <summary>Only processes that could not be fully inspected may hold it.</summary>
    MayHold,

    /// <summary>It cannot go to the Recycle Bin.</summary>
    TooBigForRecycleBin,

    /// <summary>The Shell did not finish the recycle in time.</summary>
    ShellTimeout,

    /// <summary>Removing it would lose work.</summary>
    WouldLose,

    /// <summary>A process in the caller's own parent chain holds it.</summary>
    CallerHolds,

    /// <summary>The path is not a removable worktree; <see cref="Reason.Refusal"/> says why.</summary>
    NotRemovable,
}

/// <summary>Why a path given for removal is not a removable worktree.</summary>
public enum RefusalReason
{
    /// <summary>Nothing is there, and no repo registers a worktree there.</summary>
    NotFound,

    /// <summary>It is not in a git repo, or is a file or a git dir rather than a worktree.</summary>
    NotAWorktree,

    /// <summary>It is a repo's main worktree.</summary>
    MainWorktree,

    /// <summary>It is a bare repo.</summary>
    BareRepo,

    /// <summary>It lies inside a worktree but is not its root.</summary>
    Subfolder,

    /// <summary>It is a junction or symbolic link.</summary>
    Link,

    /// <summary>
    /// It has a <c>.git</c> file no repo registers as a worktree, or a registration git calls prunable (its <c>.git</c> file is gone)
    /// while the folder remains.
    /// </summary>
    Orphan,
}

/// <summary>Reads and writes a <see cref="Reason"/> as its snake_case name; a refusal is written untagged, as its own name.</summary>
internal sealed class ReasonJsonConverter : JsonConverter<Reason>
{
    private static readonly Dictionary<string, Reason> ByName = BuildNames();

    /// <inheritdoc/>
    public override Reason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"a reason must be a string, not {reader.TokenType}");
        }
        string name = reader.GetString() ?? "";
        return ByName.TryGetValue(name, out Reason? reason) ? reason : throw new JsonException($"unknown reason \"{name}\"");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Reason value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(Name(value));
    }

    private static string Name(Reason reason) =>
        reason.Refusal is RefusalReason refusal ? SnakeCase(refusal.ToString()) : SnakeCase(reason.Kind.ToString());

    private static string SnakeCase(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);

    private static Dictionary<string, Reason> BuildNames()
    {
        Reason[] reasons =
        [
            Reason.Locked,
            Reason.MayHold,
            Reason.TooBigForRecycleBin,
            Reason.ShellTimeout,
            Reason.WouldLose,
            Reason.CallerHolds,
            .. Enum.GetValues<RefusalReason>().Select(Reason.NotRemovable),
        ];
        return reasons.ToDictionary(Name, StringComparer.Ordinal);
    }
}
