namespace WorktreeSweep.Agent;

/// <summary>The flags of an agent's removal.</summary>
/// <param name="Force">Remove even when work would be lost; lifts a git lock first.</param>
/// <param name="StopBuildServers">Stop allowlisted build and language servers that hold the worktree, then retry.</param>
public sealed record AgentOptions(bool Force, bool StopBuildServers);
