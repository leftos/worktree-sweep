using System.Text.Json.Serialization;

namespace WorktreeSweep.Report;

/// <summary>
/// The released marker (<see cref="ReleasedMarker.FileName"/>) a worktree carries in its admin dir: written by an agent's removal
/// when it could not remove the worktree, read by the scan. Its file shape is
/// <c>{"released_at": &lt;Unix seconds&gt;, "reason": "&lt;snake_case&gt;", "holders": [{"pid": &lt;int&gt;, "exe": "&lt;name&gt;"}]}</c>.
/// </summary>
public sealed record Released
{
    /// <summary>Gets when it was released, in Unix seconds.</summary>
    [JsonPropertyName("released_at")]
    public required long ReleasedAtUnix { get; init; }

    /// <summary>Gets why it was released.</summary>
    public required Reason Reason { get; init; }

    /// <summary>Gets the processes that held it, or may have.</summary>
    public required IReadOnlyList<ProcessRef> Holders { get; init; }
}
