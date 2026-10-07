namespace WorktreeSweep.Report;

/// <summary>What a scan could read of one pick's size; the size is unknown where there is none.</summary>
/// <param name="Bytes">The bytes on disk, a lower bound when <paramref name="Partial"/> is set.</param>
/// <param name="Partial">Whether the scan could read only part of the tree, so the real size is larger.</param>
public sealed record KnownSize(long Bytes, bool Partial);
