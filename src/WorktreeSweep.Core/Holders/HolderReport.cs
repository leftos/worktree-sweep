namespace WorktreeSweep.Holders;

/// <summary>What <see cref="HolderFinder.Find(string, IReadOnlyCollection{int})"/> found.</summary>
/// <param name="Holders">Processes that hold something inside the folder, by PID.</param>
/// <param name="MayHold">Processes that may hold it, by PID.</param>
public sealed record HolderReport(IReadOnlyList<Holder> Holders, IReadOnlyList<MayHold> MayHold);
