namespace WorktreeSweep.Recycle;

/// <summary>A volume's Recycle Bin settings, from <c>HKCU\...\Explorer\BitBucket\Volume\{GUID}</c>.</summary>
/// <param name="MaxCapacityMb">The <c>MaxCapacity</c> DWORD: the most the bin holds, in MB (MiB).</param>
/// <param name="NukeOnDelete">The <c>NukeOnDelete</c> DWORD is not 0: deleted files skip the bin.</param>
public sealed record BinCapacity(uint MaxCapacityMb, bool NukeOnDelete);
